using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PhpDevTray.Core;
using PhpDevTray.Infrastructure;
using PhpDevTray.Infrastructure.Configuration;
using PhpDevTray.Infrastructure.Processes;
using PhpDevTray.Infrastructure.Runtimes;
using PhpDevTray.Infrastructure.Storage;

namespace PhpDevTray.Checks;

internal static class RealNginxIncludeChecks
{
    public static async Task RunAsync(string[] args)
    {
        var profile = Argument("--profile"); var source = Argument("--runtime-source"); var report = Argument("--report");
        if (!File.Exists(Path.Combine(source, ".php-update-check-profile"))) throw new InvalidOperationException("Use the previously publisher-verified isolated runtime profile.");
        if (Directory.Exists(profile) && Directory.EnumerateFileSystemEntries(profile).Any()) throw new InvalidOperationException("Use an empty include-check profile.");
        var listener = new TcpListener(IPAddress.Loopback, 8080); listener.Server.ExclusiveAddressUse = true; listener.Start(); listener.Stop();
        var seed = await AtomicJsonStore.ReadAsync<AppSettings>(Path.Combine(source, "settings.json")) ?? throw new InvalidDataException("Missing verified runtime settings.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)); var ct = timeout.Token;
        await using var supervisor = new WindowsProcessSupervisor();
        var catalog = new RuntimeCatalog(profile, supervisor);
        await using var manager = new EnvironmentManager(profile, supervisor, catalog);
        var record = new List<string> { "# Actual Nginx include verification", "", $"Run: {DateTimeOffset.UtcNow:O}", "", "Previously publisher-verified PHP/Nginx distributions, copied into a fresh isolated profile. No PATH/hosts changes.", "" };
        try
        {
            await manager.InitializeAsync(ct);
            await manager.RegisterPhpAsync(Path.Combine(seed.PhpInstallations.First().Directory, "php.exe"), ct);
            await manager.RegisterNginxAsync(Path.Combine(seed.NginxDirectory!, "nginx.exe"), ct);
            var documentRoot = Path.Combine(profile, "project"); Directory.CreateDirectory(documentRoot);
            var site = new SiteDefinition { Id = "include-check", Hostname = "includes.test", DocumentRoot = documentRoot, PhpChannel = manager.Snapshot.PhpInstallations.Single().Channel };
            await manager.SaveSiteAsync(site, ct);
            var sourceSite = Path.Combine(profile, "user", "nginx", "sites-available", site.Id + ".conf");
            var generated = Path.Combine(profile, "user", "nginx", "generated");
            var snippets = Path.Combine(profile, "snippets with spaces");
            Directory.CreateDirectory(snippets); Directory.CreateDirectory(Path.Combine(generated, "nested")); Directory.CreateDirectory(Path.Combine(generated, "more")); Directory.CreateDirectory(Path.Combine(snippets, "direct"));
            var outer = Path.Combine(snippets, "outer.conf"); var absolute = Path.Combine(snippets, "absolute.conf");
            var middle = Path.Combine(generated, "nested", "middle.conf"); var leaf = Path.Combine(generated, "more", "leaf.conf");
            await File.WriteAllTextAsync(outer, "include nested/middle.conf;\n", ct);
            await File.WriteAllTextAsync(middle, "include more/*.conf;\ninclude " + RuntimePaths.NginxQuote(absolute) + ";\n", ct);
            await File.WriteAllTextAsync(absolute, "add_header X-Include-Test absolute always;\n", ct);
            const string leafText = "location = /nested-test { default_type text/plain; return 200 'nested-ok'; }\n";
            await File.WriteAllTextAsync(leaf, leafText, ct);
            await File.WriteAllTextAsync(Path.Combine(snippets, "direct", "endpoint.conf"), "location = /direct-glob { return 200 'direct-ok'; }\n", ct);
            var original = await File.ReadAllTextAsync(sourceSite, ct);
            var marker = $"include \"@pdt/php/{site.Id}@\";";
            var configured = original.Replace(marker, "include " + RuntimePaths.NginxQuote(outer) + ";\n    include " + RuntimePaths.NginxQuote(Path.Combine(snippets, "direct", "*.conf")) + ";\n    " + marker, StringComparison.Ordinal);
            await File.WriteAllTextAsync(sourceSite, configured, ct);
            await manager.StartServiceAsync("nginx", ct);
            await AssertRoute("/nested-test", "nested-ok"); await AssertRoute("/direct-glob", "direct-ok");
            Pass("Actual startup and HTTP requests accept absolute direct file/glob includes, nested relative/glob includes, and nested absolute includes.");
            var bootstrap = Path.Combine(generated, "bootstrap.conf");
            var savedBootstrap = await File.ReadAllBytesAsync(bootstrap, ct);
            var pid = manager.Snapshot.Services.Single(service => service.Id == "nginx").ProcessId;
            try
            {
                await File.WriteAllTextAsync(leaf, "not_a_valid_nginx_directive;\n", ct);
                await Reject(() => manager.SaveSiteAsync(site, ct));
                await AssertRoute("/nested-test", "nested-ok");
                var currentBootstrap = await File.ReadAllBytesAsync(bootstrap, ct);
                Require(savedBootstrap.SequenceEqual(currentBootstrap) && manager.Snapshot.Services.Single(service => service.Id == "nginx").ProcessId == pid, "Broken nested content changed working routing or process.");
                Pass("Broken nested configuration fails actual nginx -t without publishing a bootstrap or stopping the working instance.");
            }
            finally { await File.WriteAllTextAsync(leaf, leafText, CancellationToken.None); }
            try
            {
                await File.WriteAllTextAsync(sourceSite, configured.Replace(RuntimePaths.NginxQuote(outer), "\"relative-direct.conf\"", StringComparison.Ordinal), ct);
                await Reject(() => manager.SaveSiteAsync(site, ct));
                await AssertRoute("/nested-test", "nested-ok");
                Require(manager.Snapshot.Services.Single(service => service.Id == "nginx").ProcessId == pid, "Relative direct include rejection restarted the service.");
                Pass("Relative includes written directly in sites-available are rejected while the working route remains available.");
            }
            finally { await File.WriteAllTextAsync(sourceSite, configured, CancellationToken.None); }
            Require(await File.ReadAllTextAsync(outer, ct) == "include nested/middle.conf;\n" && await File.ReadAllTextAsync(sourceSite, ct) == configured, "User-owned include sources changed.");
            Require(!Directory.EnumerateFiles(generated, ".validation-*.conf").Any(), "A temporary validation bootstrap leaked.");
            Pass("Sources are preserved and temporary validation wrappers are removed.");
            await manager.StopAllAsync(ct);
            record.Add(""); record.Add("Result: **PASS**. Owned services stopped; unrelated listeners were not touched.");
        }
        catch (Exception error) { record.Add(""); record.Add("Result: **FAIL**.\n```\n" + error + "\n```"); throw; }
        finally { await manager.DisposeAsync(); Directory.CreateDirectory(Path.GetDirectoryName(report)!); await File.WriteAllLinesAsync(report, record, CancellationToken.None); }

        string Argument(string name) { var index = Array.IndexOf(args, name); if (index < 0 || index + 1 >= args.Length || !Path.IsPathFullyQualified(args[index + 1])) throw new ArgumentException("Provide an absolute " + name); return Path.GetFullPath(args[index + 1]); }
        void Pass(string message) { Console.WriteLine("PASS: " + message); record.Add("- PASS: " + message); }
        async Task AssertRoute(string path, string expected)
        {
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:8080" + path); request.Headers.Host = siteHost;
            using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
            Require(await response.Content.ReadAsStringAsync(ct) == expected && response.Headers.GetValues("X-Include-Test").Single() == "absolute", "Nested include route/header did not load.");
        }
    }
    private const string siteHost = "includes.test";
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> operation) { try { await operation(); } catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { return; } throw new InvalidOperationException("Invalid include configuration was accepted."); }
}
