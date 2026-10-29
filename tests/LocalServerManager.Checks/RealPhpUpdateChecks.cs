using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LocalServerManager.Core;
using LocalServerManager.Infrastructure;
using LocalServerManager.Infrastructure.Downloads;
using LocalServerManager.Infrastructure.Processes;
using LocalServerManager.Infrastructure.Runtimes;

namespace LocalServerManager.Checks;

/// <summary>Explicit network/actual-runtime acceptance test, never run by the ordinary build.</summary>
internal static class RealPhpUpdateChecks
{
    private const string OldVersion = "8.4.26";
    private const string ArchiveName = "php-8.4.26-nts-Win32-vs17-x64.zip";
    private const string ArchiveBase = "https://downloads.php.net/~windows/releases/archives/";

    public static async Task RunAsync(string[] args)
    {
        var profile = Argument(args, "--profile");
        var cliBundle = Argument(args, "--cli-bundle");
        var report = Argument(args, "--report");
        Require(File.Exists(Path.Combine(cliBundle, "php.exe")), "Publish the CLI launcher first.");
        Require(!Directory.Exists(profile) || !Directory.EnumerateFileSystemEntries(profile).Any(), "Use a new empty isolated PHP update profile.");
        Require(PortFree(8080), "Port 8080 is in use. Unrelated listeners are never stopped.");
        Directory.CreateDirectory(profile);
        await File.WriteAllTextAsync(Path.Combine(profile, ".php-update-check-profile"), "Isolated PHP patch update acceptance test\n");
        var lines = new List<string>
        {
            "# Actual PHP patch update integration", "", $"Run: {DateTimeOffset.UtcNow:O}", "",
            "Dedicated empty profile; official downloads and publisher integrity metadata; loopback only. No PATH/hosts modifications.", ""
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(90) };
        await using var supervisor = new WindowsProcessSupervisor();
        var catalog = new RuntimeCatalog(profile, supervisor, cliBundle);
        await using var manager = new EnvironmentManager(profile, supervisor, catalog);
        try
        {
            var fixtureRoot = Path.Combine(profile, "fixtures");
            Directory.CreateDirectory(fixtureRoot);
            var sbomPath = Path.Combine(fixtureRoot, ArchiveName + ".cdx.json");
            await FetchAsync(new Uri(ArchiveBase + ArchiveName + ".cdx.json"), sbomPath, 2 * 1024 * 1024);
            using var sbom = JsonDocument.Parse(await File.ReadAllTextAsync(sbomPath, ct));
            var component = sbom.RootElement.GetProperty("metadata").GetProperty("component");
            Require(component.GetProperty("name").GetString() == "php" && component.GetProperty("version").GetString() == OldVersion, "Unexpected publisher SBOM component.");
            Require(component.GetProperty("properties").EnumerateArray().Any(property => property.GetProperty("name").GetString() == "php:artifact-file-name" && property.GetProperty("value").GetString() == ArchiveName), "Publisher SBOM describes another artifact.");
            var digest = component.GetProperty("hashes").EnumerateArray().Single(hash => hash.GetProperty("alg").GetString() == "SHA-256").GetProperty("content").GetString()!;
            var archive = Path.Combine(fixtureRoot, ArchiveName);
            await FetchAsync(new Uri(ArchiveBase + ArchiveName), archive, 64 * 1024 * 1024);
            await ArtifactIntegrity.VerifySha256Async(archive, digest, ct);
            var extracted = Path.Combine(fixtureRoot, "php-" + OldVersion);
            await SafeArchiveExtractor.ExtractAsync(archive, extracted, null, ct);
            lines.Add($"- Archived PHP {OldVersion}: publisher SBOM SHA-256 `{digest}`. [Metadata]({ArchiveBase + ArchiveName}.cdx.json).");
            await manager.InitializeAsync(ct);
            await manager.RegisterPhpAsync(Path.Combine(extracted, "php.exe"), ct);
            var available = await manager.GetAvailableReleasesAsync(ServiceKind.Php, ct);
            var replacement = available.Single(release => release.Channel == "8.4");
            var replacementVersion = replacement.Version.Split('-')[0];
            Require(Version.Parse(replacementVersion) > Version.Parse(OldVersion), "The current PHP 8.4 patch must be newer than the archived test patch.");
            lines.Add($"- Replacement: {replacement.DisplayName}; official current-manifest SHA-256 `{replacement.Sha256}`.");
            var sibling = available.Single(release => release.Channel == "8.5");
            await manager.InstallReleaseAsync(sibling.Id, new Progress(), ct);
            var nginx = (await manager.GetAvailableReleasesAsync(ServiceKind.Nginx, ct)).First();
            await manager.InstallReleaseAsync(nginx.Id, new Progress(), ct);
            Pass("Archived PHP was verified against its official artifact SBOM before extraction/execution; sibling PHP and Nginx used the application's verified installer.");

            var projects = Path.Combine(profile, "projects with spaces é");
            Directory.CreateDirectory(projects);
            await File.WriteAllTextAsync(Path.Combine(projects, "index.php"), "<?php header('Content-Type: application/json'); echo json_encode(['version'=>PHP_VERSION,'openssl'=>extension_loaded('openssl'),'memory'=>ini_get('memory_limit')]);", ct);
            var site = new SiteDefinition { Id = "patch-update", Hostname = "patch.update.test", DocumentRoot = projects, PhpChannel = "8.4" };
            await manager.SaveSiteAsync(site, ct);
            await manager.SetDefaultPhpAsync("8.4", ct);
            await manager.SetPhpExtensionEnabledAsync("8.4", "openssl", true, ct);
            var custom = Path.Combine(profile, "user", "php", "8.4", "conf.d", "20-custom.ini");
            var customBytes = System.Text.Encoding.UTF8.GetBytes("; user-owned update check\nmemory_limit=320M\n");
            await File.WriteAllBytesAsync(custom, customBytes, ct);
            await manager.StartAllAsync(ct);
            await AssertVersionAsync(OldVersion);
            var before = manager.Snapshot.PhpInstallations.Single(php => php.Channel == "8.4");
            var siblingProcess = manager.Snapshot.Services.Single(service => service.Id == "php:8.5").ProcessId;
            var source = Path.Combine(profile, "user", "nginx", "sites-available", site.Id + ".conf");
            var sourceBytes = await File.ReadAllBytesAsync(source, ct);
            await manager.InstallReleaseAsync(replacement.Id, new Progress(), ct);
            await AssertVersionAsync(replacementVersion);
            AssertChannelState();
            Require(manager.Snapshot.RetainedPhpVersions.Any(version => version.Channel == "8.4" && version.Version == OldVersion), "The old patch must be retained after activation.");
            Require((await catalog.LoadAsync(ct)).PhpInstallations.Single(php => php.Channel == "8.4").Version == replacementVersion, "The activated patch must persist.");
            Pass($"Live update {OldVersion} → {replacementVersion} changes actual HTTP execution and CLI output while retaining the old patch.");

            // A bad candidate must fail validation while the current processes continue serving their loaded configuration.
            await File.AppendAllTextAsync(custom, "\nextension=missing_update_check_module\n", ct);
            try
            {
                var rejected = false;
                try { await manager.ActivatePhpVersionAsync("8.4", OldVersion, ct); }
                catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { rejected = true; }
                Require(rejected, "Invalid user extension configuration must reject candidate activation.");
                await AssertHttpAsync(replacementVersion);
                Require(manager.Snapshot.PhpInstallations.Single(php => php.Channel == "8.4").Version == replacementVersion, "Rejected candidate must preserve the active patch.");
            }
            finally { await File.WriteAllBytesAsync(custom, customBytes, CancellationToken.None); }
            Pass("A real invalid candidate configuration is rejected without disrupting the active PHP/Nginx route.");

            var slowMarker = Path.Combine(projects, "slow-request-started");
            await File.WriteAllTextAsync(Path.Combine(projects, "slow.php"), "<?php file_put_contents(__DIR__.'/slow-request-started','ready'); usleep(1200000); echo PHP_VERSION;", ct);
            var slowRequest = RequestAsync("/slow.php");
            var wait = Stopwatch.StartNew();
            while (!File.Exists(slowMarker)) { Require(wait.Elapsed < TimeSpan.FromSeconds(15), "Slow request did not start."); await Task.Delay(50, ct); }
            await manager.ActivatePhpVersionAsync("8.4", OldVersion, ct);
            using (var drained = await slowRequest)
            {
                drained.EnsureSuccessStatusCode();
                Require((await drained.Content.ReadAsStringAsync(ct)) == replacementVersion, "The old in-flight request must complete on the draining patch.");
            }
            await AssertVersionAsync(OldVersion);
            AssertChannelState();
            Require(manager.Snapshot.RetainedPhpVersions.Any(version => version.Channel == "8.4" && version.Version == replacementVersion), "Rollback must retain the outgoing patch.");
            Require((await File.ReadAllBytesAsync(custom, ct)).SequenceEqual(customBytes), "User INI bytes changed.");
            Require((await File.ReadAllBytesAsync(source, ct)).SequenceEqual(sourceBytes), "User Nginx source bytes changed.");
            Pass("Explicit rollback drains an active real PHP request, restores actual HTTP/CLI versions, preserves user INI/Nginx sources, and leaves the sibling process untouched.");
            await manager.StopServiceAsync("nginx", ct);
            await manager.StopServiceAsync("php:8.4", ct);
            await manager.ActivatePhpVersionAsync("8.4", replacementVersion, ct);
            Require(manager.Snapshot.Services.Single(service => service.Id == "php:8.4").Status == ServiceStatus.Stopped, "Activating a stopped channel must not start it.");
            await AssertCliAsync(replacementVersion);
            await manager.ActivatePhpVersionAsync("8.4", OldVersion, ct);
            await AssertCliAsync(OldVersion);
            Pass("Stopped-channel patch changes leave the service stopped while updating the independent CLI selection.");
            await manager.StopAllAsync(ct);
            lines.Add(""); lines.Add("Result: **PASS**. All owned services stopped. PATH and hosts were unchanged.");

            void AssertChannelState()
            {
                var active = manager.Snapshot.PhpInstallations.Single(php => php.Channel == "8.4");
                Require(active.Id == before.Id && active.Port == before.Port && active.MailIntegrationEnabled == before.MailIntegrationEnabled && active.AutoStart == before.AutoStart && active.EnabledExtensions.SequenceEqual(before.EnabledExtensions), "Channel policy changed during activation.");
                Require(manager.Snapshot.DefaultPhpChannel == "8.4" && manager.Snapshot.Sites.Single().PhpChannel == "8.4", "CLI/site channel assignment changed.");
                Require(manager.Snapshot.Services.Single(service => service.Id == "php:8.5").ProcessId == siblingProcess, "Sibling PHP process was restarted.");
            }
        }
        catch (Exception error)
        {
            lines.Add(""); lines.Add("Result: **FAIL**."); lines.Add("```"); lines.Add(error.ToString()); lines.Add("```"); throw;
        }
        finally
        {
            await manager.DisposeAsync();
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            await File.WriteAllLinesAsync(report, lines, CancellationToken.None);
        }

        void Pass(string message) { Console.WriteLine("PASS: " + message); lines.Add("- PASS: " + message); }
        async Task FetchAsync(Uri uri, string path, int maximum)
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            Require(response.Content.Headers.ContentLength is null || response.Content.Headers.ContentLength <= maximum, "Publisher artifact exceeds the fixture download bound.");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920]; long total = 0; int read;
            while ((read = await input.ReadAsync(buffer, ct)) != 0) { total += read; Require(total <= maximum, "Publisher artifact exceeded the fixture download bound."); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
        }
        async Task<HttpResponseMessage> RequestAsync(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:8080" + path);
            request.Headers.Host = "patch.update.test";
            return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        }
        async Task AssertHttpAsync(string version)
        {
            using var response = await RequestAsync("/index.php"); response.EnsureSuccessStatusCode();
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            Require(result.RootElement.GetProperty("version").GetString() == version, "Actual HTTP PHP version differs from the active patch.");
            Require(result.RootElement.GetProperty("openssl").GetBoolean() && result.RootElement.GetProperty("memory").GetString() == "320M", "Actual PHP lost its extension or user INI configuration.");
        }
        async Task AssertCliAsync(string version)
        {
            var result = await supervisor.RunAsync(new ProcessCommand { ExecutablePath = Path.Combine(profile, "bin", "php.exe"), Arguments = ["-r", "echo PHP_VERSION;"], WorkingDirectory = profile }, TimeSpan.FromSeconds(15), ct);
            Require(result.ExitCode == 0 && result.StandardOutput == version, "Actual independent CLI version differs from the active patch: " + result.StandardError);
        }
        async Task AssertVersionAsync(string version) { await AssertHttpAsync(version); await AssertCliAsync(version); }
    }

    private static string Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length || !Path.IsPathFullyQualified(args[index + 1])) throw new ArgumentException($"Provide {name} with an absolute path.");
        return Path.GetFullPath(args[index + 1]);
    }
    private static bool PortFree(int port) { try { var listener = new TcpListener(IPAddress.Loopback, port); listener.Server.ExclusiveAddressUse = true; listener.Start(); listener.Stop(); return true; } catch (SocketException) { return false; } }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Progress : IProgress<RuntimeInstallProgress>
    {
        private string? _phase;
        public void Report(RuntimeInstallProgress value) { if (_phase == value.Phase) return; _phase = value.Phase; Console.WriteLine("INSTALL: " + value.Phase); }
    }
}
