using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PhpDevTray.Core;
using PhpDevTray.Infrastructure.Configuration;
using PhpDevTray.Infrastructure.Downloads;

namespace PhpDevTray.Infrastructure.Releases;

/// <summary>Discovers publisher releases and verifies their archives before making an executable available for import.</summary>
public sealed partial class RuntimeReleaseManager
{
    private const long MaximumDownloadBytes = 512L * 1024 * 1024;
    private const long MaximumMetadataBytes = 8L * 1024 * 1024;
    private static readonly Uri PhpManifest = new("https://downloads.php.net/~windows/releases/releases.json");
    private static readonly Uri MailpitManifest = new("https://api.github.com/repos/axllent/mailpit/releases/latest");
    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _downloadsDirectory;
    private readonly HttpClient _client;

    public RuntimeReleaseManager(string dataDirectory) : this(dataDirectory, SharedClient) { }

    internal RuntimeReleaseManager(string dataDirectory, HttpClient client)
    {
        _downloadsDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "downloads");
        Directory.CreateDirectory(_downloadsDirectory);
        _client = client;
    }

    public async Task<IReadOnlyList<RuntimeRelease>> GetReleasesAsync(ServiceKind kind, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        return kind switch
        {
            ServiceKind.FrankenPhp => await AddPhpVersionsAsync(await GetWebReleasesAsync(kind, timeout.Token), timeout.Token),
            ServiceKind.Caddy => await GetWebReleasesAsync(kind, timeout.Token),
            ServiceKind.Php => await GetPhpReleasesAsync(timeout.Token),
            ServiceKind.Mailpit => await GetMailpitReleasesAsync(timeout.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    public async Task<DownloadedRuntime> DownloadAsync(string releaseId, IProgress<RuntimeInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);
        var kind = releaseId.StartsWith("php:", StringComparison.Ordinal) ? ServiceKind.Php
            : releaseId.StartsWith("caddy:", StringComparison.Ordinal) ? ServiceKind.Caddy
            : releaseId.StartsWith("frankenphp:", StringComparison.Ordinal) ? ServiceKind.FrankenPhp
            : releaseId.StartsWith("mailpit:", StringComparison.Ordinal) ? ServiceKind.Mailpit
            : throw new ArgumentException("Choose a release returned by the official runtime catalog.", nameof(releaseId));
        progress?.Report(new("Refreshing publisher release metadata"));
        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        refresh.CancelAfter(TimeSpan.FromSeconds(45));
        var releases = kind == ServiceKind.FrankenPhp ? await GetWebReleasesAsync(kind, refresh.Token) : await GetReleasesAsync(kind, refresh.Token);
        var release = releases.SingleOrDefault(item => item.Id == releaseId)
            ?? throw new InvalidOperationException("The selected release is no longer present in the publisher's current catalog. Refresh releases and choose again.");
        if (release.Sha256 is null)
            throw new NotSupportedException("This release has no supported publisher integrity metadata. Automatic installation is unavailable; import a separately verified distribution instead.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var staging = Path.Combine(_downloadsDirectory, "stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var archive = Path.Combine(staging, "package.zip");
            await DownloadFileAsync(release.DownloadUri, archive, MaximumDownloadBytes, kind, progress, timeout.Token);
            if (release.Sha256 is not null)
            {
                progress?.Report(new("Verifying publisher SHA-256 checksum"));
                await ArtifactIntegrity.VerifySha256Async(archive, release.Sha256, timeout.Token);
            }
            var extracted = Path.Combine(staging, "extracted");
            await SafeArchiveExtractor.ExtractAsync(archive, extracted, progress, timeout.Token);
            var executable = kind switch
            {
                ServiceKind.Caddy => Path.Combine(extracted, "caddy.exe"),
                ServiceKind.FrankenPhp => Path.Combine(extracted, "frankenphp.exe"),
                ServiceKind.Php => Path.Combine(extracted, "php.exe"),
                ServiceKind.Mailpit => Path.Combine(extracted, "mailpit.exe"),
                _ => throw new InvalidOperationException()
            };
            progress?.Report(new("Checking verified distribution layout"));
            RuntimePaths.VerifyExecutable(executable, true);
            if (kind == ServiceKind.Php)
            {
                RuntimePaths.VerifyExecutable(Path.Combine(extracted, "php-cgi.exe"), true);
                if (!File.Exists(Path.Combine(extracted, "php.ini-development"))) throw new InvalidDataException("The verified PHP archive does not contain its required stock configuration.");
            }
            progress?.Report(new("Verified runtime ready for import"));
            return new(kind, executable, staging, _downloadsDirectory);
        }
        catch
        {
            SafeArchiveExtractor.DeleteContainedDirectory(_downloadsDirectory, staging);
            throw;
        }
    }

    private async Task<IReadOnlyList<RuntimeRelease>> GetPhpReleasesAsync(CancellationToken cancellationToken)
    {
        using var metadata = JsonDocument.Parse(await ReadBytesAsync(PhpManifest, MaximumMetadataBytes, ServiceKind.Php, cancellationToken));
        var releases = new List<RuntimeRelease>();
        foreach (var channel in metadata.RootElement.EnumerateObject())
        {
            if (!ChannelPattern().IsMatch(channel.Name) || !Version.TryParse(channel.Name, out var channelVersion) || channelVersion < new Version(8, 2)) continue;
            if (!channel.Value.TryGetProperty("version", out var versionNode)) continue;
            var version = versionNode.GetString();
            if (version is null || !PackageVersionPattern().IsMatch(version) || !version.StartsWith(channel.Name + ".", StringComparison.Ordinal)) continue;
            var candidate = channel.Value.EnumerateObject().Where(item => item.Name.StartsWith("nts-", StringComparison.Ordinal) && item.Name.EndsWith("-x64", StringComparison.Ordinal))
                .OrderByDescending(item => item.Name, StringComparer.Ordinal).FirstOrDefault();
            if (candidate.Name is null || !candidate.Value.TryGetProperty("zip", out var zip)) continue;
            var filename = String(zip, "path");
            var expected = "^php-" + Regex.Escape(version) + @"-nts-Win32-v(?:s|c)\d+-x64\.zip$";
            if (!Regex.IsMatch(filename, expected, RegexOptions.CultureInvariant)) throw new InvalidDataException("The official PHP manifest contains an unexpected Windows archive path.");
            var checksum = ValidSha256(String(zip, "sha256"));
            releases.Add(new RuntimeRelease
            {
                Id = $"php:{version}:x64:nts", Kind = ServiceKind.Php, Version = version, Channel = channel.Name,
                DisplayName = $"PHP {version} · x64 NTS", DownloadUri = new Uri("https://downloads.php.net/~windows/releases/" + filename),
                Sha256 = checksum, IntegrityDescription = checksum is not null ? "SHA-256 from official PHP Windows release metadata" : "Automatic installation unavailable: publisher SHA-256 missing"
            });
        }
        if (releases.Count == 0) throw new NotSupportedException("The official PHP manifest contains no supported Windows x64 NTS releases.");
        return releases.OrderByDescending(release => Version.Parse(release.Channel!)).ToArray();
    }

    private static string WebRepository(ServiceKind kind) => kind == ServiceKind.Caddy ? "caddyserver/caddy" : "php/frankenphp";

    private async Task<IReadOnlyList<RuntimeRelease>> GetWebReleasesAsync(ServiceKind kind, CancellationToken token)
    {
        var repo = WebRepository(kind);
        var uri = new Uri($"https://api.github.com/repos/{repo}/releases?per_page=20");
        using var metadata = JsonDocument.Parse(await ReadBytesAsync(uri, MaximumMetadataBytes, kind, token));
        var result = new List<RuntimeRelease>();
        foreach (var release in metadata.RootElement.EnumerateArray())
        {
            var tag = String(release, "tag_name");
            if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$") || release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) continue;
            var version = tag[1..];
            var name = kind == ServiceKind.Caddy ? $"caddy_{version}_windows_amd64.zip" : "frankenphp-windows-x86_64.zip";
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (String(asset, "name") != name) continue;
                var url = $"https://github.com/{repo}/releases/download/{tag}/{name}";
                if (String(asset, "browser_download_url") != url) throw new InvalidDataException("Unexpected publisher asset URL.");
                var digest = String(asset, "digest");
                var sha = digest.StartsWith("sha256:", StringComparison.Ordinal) ? ValidSha256(digest[7..]) : null;
                if (sha is null) continue;
                var label = kind == ServiceKind.Caddy ? "Caddy" : "FrankenPHP";
                result.Add(new RuntimeRelease { Id = $"{label.ToLowerInvariant()}:{version}", Kind = kind, Version = version,
                    ArchiveBytes = asset.GetProperty("size").GetInt64(), DisplayName = $"{label} {version} · Windows x64", DownloadUri = new Uri(url), Sha256 = sha,
                    IntegrityDescription = $"SHA-256 supplied by {repo} on GitHub" });
            }
        }
        if (result.Count == 0) throw new NotSupportedException("No verified Windows x64 release was found.");
        return result;
    }

    private async Task<IReadOnlyList<RuntimeRelease>> GetMailpitReleasesAsync(CancellationToken cancellationToken)
    {
        using var metadata = JsonDocument.Parse(await ReadBytesAsync(MailpitManifest, MaximumMetadataBytes, ServiceKind.Mailpit, cancellationToken));
        var release = metadata.RootElement;
        if ((release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) || (release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()))
            throw new NotSupportedException("The official Mailpit catalog did not return a stable published release.");
        var tag = String(release, "tag_name");
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)) throw new InvalidDataException("The Mailpit release tag has an unsupported format.");
        if (!release.TryGetProperty("assets", out var assets)) throw new InvalidDataException("The Mailpit release has no asset metadata.");
        foreach (var asset in assets.EnumerateArray())
        {
            if (String(asset, "name") != "mailpit-windows-amd64.zip") continue;
            var expectedUri = "https://github.com/axllent/mailpit/releases/download/" + tag + "/mailpit-windows-amd64.zip";
            if (!string.Equals(String(asset, "browser_download_url"), expectedUri, StringComparison.Ordinal)) throw new InvalidDataException("The Mailpit asset URL is outside its official release path.");
            var digest = String(asset, "digest");
            var checksum = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? ValidSha256(digest[7..]) : null;
            return [new RuntimeRelease
            {
                Id = "mailpit:" + tag[1..] + ":windows-amd64", Kind = ServiceKind.Mailpit, Version = tag[1..],
                DisplayName = "Mailpit " + tag[1..] + " · Windows x64", DownloadUri = new Uri(expectedUri), Sha256 = checksum,
                IntegrityDescription = checksum is not null ? "SHA-256 digest from the official axllent/mailpit GitHub release asset" : "Automatic installation unavailable: publisher SHA-256 missing"
            }];
        }
        throw new NotSupportedException("The official Mailpit release has no supported Windows amd64 ZIP asset.");
    }

    private async Task DownloadFileAsync(Uri uri, string path, long maximumBytes, ServiceKind kind,
        IProgress<RuntimeInstallProgress>? progress, CancellationToken cancellationToken)
    {
        using var response = await GetResponseAsync(uri, kind, cancellationToken);
        if (response.Content.Headers.ContentLength is > 0 && response.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("The runtime download exceeds the configured size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long received = 0;
        var nextReport = DateTime.UtcNow;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            received = checked(received + count);
            if (received > maximumBytes) throw new InvalidDataException("The runtime download exceeded the configured size limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            if (DateTime.UtcNow >= nextReport)
            {
                progress?.Report(new("Downloading official archive", received, response.Content.Headers.ContentLength));
                nextReport = DateTime.UtcNow.AddMilliseconds(100);
            }
        }
        if (received == 0 || (response.Content.Headers.ContentLength is { } expected && received != expected))
            throw new InvalidDataException("The publisher download ended before the advertised content was received.");
        progress?.Report(new("Download complete", received, response.Content.Headers.ContentLength));
    }

    private async Task<byte[]> ReadBytesAsync(Uri uri, long maximumBytes, ServiceKind kind, CancellationToken cancellationToken)
    {
        using var response = await GetResponseAsync(uri, kind, cancellationToken);
        if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("Publisher metadata exceeds the configured size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + count > maximumBytes) throw new InvalidDataException("Publisher metadata exceeds the configured size limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        return output.ToArray();
    }

    private async Task<HttpResponseMessage> GetResponseAsync(Uri uri, ServiceKind kind, CancellationToken cancellationToken, System.Net.Http.Headers.RangeHeaderValue? range = null)
    {
        for (var redirect = 0; redirect < 6; redirect++)
        {
            ValidatePublisherUri(uri, kind);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = range;
            request.Headers.UserAgent.ParseAdd("PhpDevTray/0.1");
            request.Headers.Accept.ParseAdd(uri.Host == "api.github.com" ? "application/vnd.github+json" : "*/*");
            if (uri.Host == "api.github.com") request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("The publisher returned a redirect without a destination.");
                uri = new Uri(uri, location);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException(status == HttpStatusCode.Forbidden && uri.Host == "api.github.com"
                    ? "GitHub refused the release request, possibly because its anonymous API rate limit was reached. Retry later."
                    : $"The official publisher returned HTTP {(int)status} for {uri.Host}.", null, status);
            }
            return response;
        }
        throw new InvalidDataException("The publisher exceeded the permitted redirect count.");
    }

    internal static void ValidatePublisherUri(Uri uri, ServiceKind kind)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            throw new InvalidDataException("Runtime downloads require an official HTTPS endpoint without embedded credentials.");
        var allowed = kind switch
        {
            ServiceKind.Php => (uri.Host.Equals("downloads.php.net", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/~windows/releases/", StringComparison.Ordinal))
                || (uri.Host.Equals("windows.php.net", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/downloads/releases/", StringComparison.Ordinal)),
            ServiceKind.Caddy or ServiceKind.FrankenPhp =>
                uri.Host == "api.github.com" && uri.AbsolutePath == $"/repos/{WebRepository(kind)}/releases"
                || uri.Host == "github.com" && uri.AbsolutePath.StartsWith($"/{WebRepository(kind)}/releases/download/", StringComparison.Ordinal)
                || uri.Host == "release-assets.githubusercontent.com",
            ServiceKind.Mailpit => (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath == "/repos/axllent/mailpit/releases/latest")
                || (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/axllent/mailpit/releases/download/", StringComparison.Ordinal))
                || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        if (!allowed) throw new InvalidDataException("The publisher attempted to redirect outside its approved release hosts. The download was stopped.");
    }

    private static string String(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string? ValidSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;


    [GeneratedRegex(@"^\d+\.\d+$", RegexOptions.CultureInvariant)] private static partial Regex ChannelPattern();
    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:-\d+)?$", RegexOptions.CultureInvariant)] private static partial Regex PackageVersionPattern();
}
