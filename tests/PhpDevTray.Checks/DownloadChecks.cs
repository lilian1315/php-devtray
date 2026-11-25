using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhpDevTray.Core;
using PhpDevTray.Infrastructure.Downloads;
using PhpDevTray.Infrastructure.Releases;

namespace PhpDevTray.Checks;

public static class DownloadChecks
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PhpDevTray-download-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var fixture = new RuntimeRelease { Id = "fp", Kind = ServiceKind.FrankenPhp, Version = "1.9.9", Channel = "8.5",
                DisplayName = "Fixture", DownloadUri = new Uri("https://example.invalid/archive.zip") };
            var catalog = RuntimeReleaseManager.LatestForEachPhpMinor([fixture, fixture with { Version = "1.10.0" },
                fixture with { Channel = "8.6" }, fixture with { Channel = null }]);
            Require(catalog.Length == 2 && catalog[0].Channel == "8.6" && catalog[1].Version == "1.10.0",
                "Offer the newest FrankenPHP version once per known PHP minor.");
            await CheckChecksumsAsync(directory);
            await CheckArchivesAsync(directory);
            await CheckReleaseTrustAsync(directory);
            Console.WriteLine("PASS: publisher checksums, and tamper rejection.");
            Console.WriteLine("PASS: ZIP traversal, alternate streams, device names, symlinks, collisions, bombs, and cancellation cleanup.");
            Console.WriteLine("PASS: fresh trusted release resolution, Windows PHP rebuild suffixes, redirect allowlists, and disposable download staging.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task CheckChecksumsAsync(string directory)
    {
        var artifact = Path.Combine(directory, "artifact.bin");
        await File.WriteAllBytesAsync(artifact, [1, 2, 3, 4]);
        var digest = Convert.ToHexString(SHA256.HashData([1, 2, 3, 4]));
        await ArtifactIntegrity.VerifySha256Async(artifact, digest);
        await ExpectAsync<InvalidDataException>(() => ArtifactIntegrity.VerifySha256Async(artifact, new string('0', 64)), "Tampered archives must fail checksum verification.");
        await ExpectAsync<InvalidDataException>(() => ArtifactIntegrity.VerifySha256Async(artifact, "not a digest"), "Missing or malformed checksums must not become an integrity bypass.");
    }

    private static async Task CheckArchivesAsync(string directory)
    {
        foreach (var name in new[] { "../escape.exe", "folder/../../escape.exe", "C:/escape.exe", "/escape.exe", "file.txt:payload.exe", "CON.txt", "folder/COM1", "folder./file", "folder /file", "a//b", "folder\\..\\outside" })
            RequireThrows<InvalidDataException>(() => SafeArchiveExtractor.ValidateName(name), "Unsafe archive name was accepted: " + name);
        var good = Path.Combine(directory, "good.zip");
        CreateZip(good, [("nested/php.exe", new byte[] { 1, 2, 3 }, 0)]);
        var extracted = Path.Combine(directory, "good-extracted");
        await SafeArchiveExtractor.ExtractAsync(good, extracted, null);
        Require(File.Exists(Path.Combine(extracted, "nested", "php.exe")), "A valid nested file must be extracted.");

        foreach (var test in new[]
        {
            (Name: "traversal", Entries: new[] { ("../outside.exe", new byte[] { 1 }, 0) }),
            (Name: "symlink", Entries: new[] { ("link", Encoding.UTF8.GetBytes("../../outside"), unchecked((int)0xA1FF0000u)) }),
            (Name: "reparse", Entries: new[] { ("link", new byte[] { 1 }, (int)FileAttributes.ReparsePoint) }),
            (Name: "collision", Entries: new[] { ("PHP.exe", new byte[] { 1 }, 0), ("php.exe", new byte[] { 2 }, 0) }),
            (Name: "bomb", Entries: new[] { ("zeros.bin", new byte[2 * 1024 * 1024], 0) })
        })
        {
            var path = Path.Combine(directory, test.Name + ".zip");
            CreateZip(path, test.Entries);
            var target = Path.Combine(directory, test.Name + "-extracted");
            await ExpectAsync<InvalidDataException>(() => SafeArchiveExtractor.ExtractAsync(path, target, null), "Malicious archive must be rejected: " + test.Name);
            Require(!Directory.Exists(target), "Failed extraction must remove its own partial staging: " + test.Name);
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceledTarget = Path.Combine(directory, "canceled-extracted");
        await ExpectAsync<OperationCanceledException>(() => SafeArchiveExtractor.ExtractAsync(good, canceledTarget, null, cancellation.Token), "Canceled extraction must not continue.");
        Require(!Directory.Exists(canceledTarget), "Canceled extraction must clean partial files.");
        RequireThrows<InvalidDataException>(() => SafeArchiveExtractor.DeleteContainedDirectory(directory, Path.GetDirectoryName(directory)!), "Cleanup must refuse targets outside staging.");
    }

    private static async Task CheckReleaseTrustAsync(string directory)
    {
        var fixture = Path.Combine(directory, "php-fixture.zip");
        CreateZip(fixture,
        [
            ("php.exe", PeExecutable(), 0), ("php-cgi.exe", PeExecutable(), 0),
            ("php.ini-development", Encoding.UTF8.GetBytes("; fixture\n"), 0)
        ]);
        var archive = await File.ReadAllBytesAsync(fixture);
        var hash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        var handler = new FixtureHandler(archive, hash);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var data = Path.Combine(directory, "catalog-data");
        var manager = new RuntimeReleaseManager(data, client);
        var releases = await manager.GetReleasesAsync(ServiceKind.Php);
        Require(releases.Count == 1 && releases[0].Version == "8.4.18-1" && releases[0].Channel == "8.4", "Official Windows rebuild suffixes must remain part of release identity.");
        var manifestsBefore = handler.ManifestRequests;
        var result = await manager.DownloadAsync(releases[0].Id);
        Require(handler.ManifestRequests > manifestsBefore, "Installation must refresh trusted metadata instead of trusting caller release objects.");
        Require(File.Exists(result.ExecutablePath) && result.Kind == ServiceKind.Php, "Verified PHP must be returned only after executable layout validation.");
        var staging = result.StagingDirectory;
        await result.DisposeAsync();
        Require(!Directory.Exists(staging), "Disposing a download must delete contained staging after import.");

        handler.TamperArchive = true;
        await ExpectAsync<InvalidDataException>(() => manager.DownloadAsync(releases[0].Id), "Corrupted downloads must never reach extraction.");
        Require(!Directory.EnumerateDirectories(Path.Combine(data, "downloads")).Any(), "Failed downloads must remove staging.");
        handler.TamperArchive = false;
        handler.RedirectArchiveOutsidePublisher = true;
        await ExpectAsync<InvalidDataException>(() => manager.DownloadAsync(releases[0].Id), "Off-publisher redirects must fail closed.");
        Require(handler.Requests.All(uri => uri.Host is "downloads.php.net" or "api.github.com"), "A rejected redirect target must never be requested.");
        handler.RedirectArchiveOutsidePublisher = false;
        await ExpectAsync<InvalidOperationException>(() => manager.DownloadAsync("php:https://attacker.invalid/x"), "Caller-controlled IDs must not become download URLs.");
        RequireThrows<InvalidDataException>(() => RuntimeReleaseManager.ValidatePublisherUri(new("http://downloads.php.net/~windows/releases/test.zip"), ServiceKind.Php), "HTTP downgrade must be rejected.");
        RequireThrows<InvalidDataException>(() => RuntimeReleaseManager.ValidatePublisherUri(new("https://downloads.php.net.attacker.invalid/~windows/releases/test.zip"), ServiceKind.Php), "Lookalike publisher hosts must be rejected.");
    }

    private static byte[] PeExecutable()
    {
        var bytes = new byte[128];
        using var stream = new MemoryStream(bytes);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x5A4D);
        stream.Position = 0x3C;
        writer.Write(64);
        stream.Position = 64;
        writer.Write((uint)0x00004550);
        writer.Write((ushort)0x8664);
        return bytes;
    }

    private static void CreateZip(string path, IEnumerable<(string Name, byte[] Content, int Attributes)> entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Name, CompressionLevel.SmallestSize);
            entry.ExternalAttributes = item.Attributes;
            using var output = entry.Open();
            output.Write(item.Content);
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task ExpectAsync<T>(Func<Task> operation, string message) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }
    private static void RequireThrows<T>(Action operation, string message) where T : Exception
    {
        try { operation(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    private sealed class FixtureHandler(byte[] archive, string checksum) : HttpMessageHandler
    {
        public int ManifestRequests { get; private set; }
        public List<Uri> Requests { get; } = [];
        public bool TamperArchive { get; set; }
        public bool RedirectArchiveOutsidePublisher { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            Requests.Add(uri);
            if (uri.AbsolutePath.EndsWith("releases.json", StringComparison.Ordinal))
            {
                ManifestRequests++;
                var metadata = new Dictionary<string, object>
                {
                    ["8.4"] = new Dictionary<string, object>
                    {
                        ["version"] = "8.4.18-1",
                        ["nts-vs17-x64"] = new { zip = new { path = "php-8.4.18-1-nts-Win32-vs17-x64.zip", sha256 = checksum } },
                        ["ts-vs17-x64"] = new { zip = new { path = "do-not-select.zip", sha256 = checksum } }
                    }
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(metadata)) });
            }
            if (RedirectArchiveOutsidePublisher) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                { Headers = { Location = new Uri("https://attacker.invalid/runtime.zip") } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(TamperArchive ? [1, 2, 3] : archive) });
        }
    }
}
