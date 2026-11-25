using System.Net;
using System.Net.Sockets;
using PhpDevTray.Core;
using PhpDevTray.Infrastructure;
using PhpDevTray.Infrastructure.Processes;
using PhpDevTray.Infrastructure.Runtimes;

namespace PhpDevTray.Checks;

internal static class WebSetupChecks
{
    // All manager operations use real Windows jobs. Only the test listener and TLS are changed to avoid changing system trust.
    private sealed class LoopbackSupervisor(string root) : IProcessSupervisor
    {
        private readonly WindowsProcessSupervisor _inner = new();
        public Dictionary<string, int> Starts { get; } = new(StringComparer.Ordinal);
        public bool FailNextProxyStart { get; set; }
        public string? FailNextRuntimeStart { get; set; }
        public int Port { get; } = GetFreePort();
        public event EventHandler<ServiceSnapshot>? ServiceChanged
        {
            add => _inner.ServiceChanged += value;
            remove => _inner.ServiceChanged -= value;
        }
        public IReadOnlyList<ServiceSnapshot> Snapshots => _inner.Snapshots;
        public bool IsRunning(string id) => _inner.IsRunning(id);
        public Task StopAsync(string id, TimeSpan timeout, CancellationToken ct = default) => _inner.StopAsync(id, timeout, ct);
        public Task StopAllAsync(TimeSpan timeout, CancellationToken ct = default) => _inner.StopAllAsync(timeout, ct);
        public Task<ProcessResult> RunAsync(ProcessCommand command, TimeSpan timeout, CancellationToken ct = default) => _inner.RunAsync(command, timeout, ct);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
        public async Task StartAsync(ManagedServiceDefinition service, CancellationToken ct = default)
        {
            Starts[service.Id] = Starts.GetValueOrDefault(service.Id) + 1;
            if (service.Id == FailNextRuntimeStart)
            {
                FailNextRuntimeStart = null;
                throw new IOException("Test PHP start failure");
            }
            if (service.Kind == ServiceKind.Caddy)
            {
                if (FailNextProxyStart) { FailNextProxyStart = false; throw new IOException("Test proxy start failure"); }
                var config = await File.ReadAllTextAsync(service.Command.Arguments.Last(), ct);
                config = System.Text.RegularExpressions.Regex.Replace(config, @"http://[\w.-]+ \{\s*bind 127\.0\.0\.1\s*redir [^\n]+\s*\}", "");
                config = System.Text.RegularExpressions.Regex.Replace(config, @"https://([\w.-]+) \{", $"http://${{1}}:{Port} {{").Replace(" tls internal\n", "");
                config = config.Replace("http://:80 {", $"http://:{Port} {{");
                var path = Path.Combine(root, "Caddyfile");
                await File.WriteAllTextAsync(path, config, ct);
                service = service with { Command = service.Command with { Arguments = ["run", "--config", path] },
                    HealthEndpoints = [new(HealthCheckKind.Tcp, "127.0.0.1", Port)] };
            }
            await _inner.StartAsync(service, ct);
        }

        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    public static async Task RunAsync(string ntsExecutable, string? caddyExecutable = null, string? frankenExecutable = null)
    {
        var root = Directory.CreateTempSubdirectory("PhpDevTrayWebSetup-").FullName;
        try
        {
            await using var supervisor = new LoopbackSupervisor(root);
            var catalog = new RuntimeCatalog(root, supervisor);
            await using var manager = new EnvironmentManager(root, supervisor, catalog);
            await manager.InitializeAsync();
            foreach (var kind in new[] { ServiceKind.Caddy, ServiceKind.FrankenPhp })
            {
                var local = kind == ServiceKind.Caddy ? caddyExecutable : frankenExecutable;
                if (local is not null)
                {
                    await manager.RegisterWebRuntimeAsync(kind, local);
                    continue;
                }
                var releases = await manager.GetAvailableReleasesAsync(kind);
                if (kind == ServiceKind.FrankenPhp)
                {
                    foreach (var item in releases) Console.WriteLine(item.DisplayName);
                    if (releases.Select(r => r.Channel).Distinct().Count() != releases.Count) throw new InvalidDataException("Duplicate PHP minor channel in the FrankenPHP catalog.");
                    if (releases.Any(r => !System.Text.RegularExpressions.Regex.IsMatch(r.DisplayName, @"· PHP \d+\.\d+\.\d+")))
                        throw new InvalidDataException("The Windows release catalog is missing embedded PHP versions.");
                }
                var release = releases.FirstOrDefault(r => r.Kind == ServiceKind.FrankenPhp && r.Version == "1.13.6") ?? releases.First();
                await manager.InstallReleaseAsync(release.Id);
                Console.WriteLine($"Installed {release.DisplayName}");
            }
            var runtime = manager.Snapshot.Services.Single(s => s.Kind == ServiceKind.FrankenPhp);
            await manager.StartServiceAsync(runtime.Id);
            await manager.RestartServiceAsync(runtime.Id);
            await manager.StopServiceAsync(runtime.Id);
            if (supervisor.IsRunning(runtime.Id)) throw new InvalidOperationException("FrankenPHP did not stop.");
            var documentRoot = Directory.CreateDirectory(Path.Combine(root, "public site")).FullName;
            await File.WriteAllTextAsync(Path.Combine(documentRoot, "index.php"), "<?php echo PHP_SAPI;");
            var site = new WebSite("app.localhost", documentRoot, runtime.Id);
            await manager.SaveSiteAsync(site);
            var source = CaddyStack.SiteFile(root, site);
            var generated = await File.ReadAllTextAsync(source);
            if (generated.Contains("pdt-handler-") || generated.Contains("php_server") || generated.Contains("php_fastcgi") || generated.Contains("{args[") || !generated.Contains(documentRoot.Replace('\\', '/')) || !generated.Contains("# The * matcher"))
                throw new InvalidOperationException("The site file must contain a literal document root and explanatory comments.");
            await File.AppendAllTextAsync(source, "header X-Site-Custom yes\n");
            var original = await File.ReadAllTextAsync(source);
            var changedRoot = Directory.CreateDirectory(Path.Combine(root, "changed root é")).FullName;
            await File.WriteAllTextAsync(source, original.Replace(documentRoot.Replace('\\', '/'), changedRoot.Replace('\\', '/')));
            await WaitForRootAsync(changedRoot);
            if (manager.Snapshot.SiteRoots.GetValueOrDefault(site.Hostname) != changedRoot)
                throw new InvalidOperationException("Refresh did not read the changed root from the FrankenPHP Caddyfile.");
            await File.AppendAllTextAsync(source, "\ninvalid_site_directive\n");
            await WaitForRootAsync(null);
            if (manager.Snapshot.SiteRoots.GetValueOrDefault(site.Hostname) is not null)
                throw new InvalidOperationException("An invalid Caddyfile showed an old root.");
            await File.WriteAllTextAsync(source, original);
            await WaitForRootAsync(documentRoot);
            var replacement = source + ".save";
            await File.WriteAllTextAsync(replacement, original.Replace(documentRoot.Replace('\\', '/'), changedRoot.Replace('\\', '/')));
            File.Move(replacement, source, true);
            await WaitForRootAsync(changedRoot);
            File.Delete(source);
            await WaitForRootAsync(null);
            await File.WriteAllTextAsync(source, original);
            await WaitForRootAsync(documentRoot);
            var settings = new AppSettings();
            await manager.StartAllAsync();
            await ValidateOwnershipAsync(true);
            await File.WriteAllTextAsync(Path.Combine(documentRoot, "extensions.php"), "<?php echo (extension_loaded('openssl')?'1':'0').(extension_loaded('mbstring')?'1':'0');");
            var inventory = await manager.GetPhpExtensionsAsync(runtime.Id);
            var builtin = inventory.First(e => e.IsBuiltIn);
            try { await manager.SetPhpExtensionEnabledAsync(runtime.Id, builtin.Name, false); throw new Exception("Built-in toggle accepted."); }
            catch (InvalidOperationException error) when (error.Message.Contains("built into")) { }
            if (!inventory.Any(e => e.Name == "openssl" && e.IsAvailable) || !inventory.Any(e => e.IsBuiltIn))
                throw new InvalidOperationException("Incomplete FrankenPHP extension inventory.");
            var proxyStarts = supervisor.Starts["caddy"];
            var frankenStarts = supervisor.Starts[runtime.Id];
            await manager.ApplyPhpExtensionChangesAsync(runtime.Id, [new("openssl", true), new("mbstring", true)]);
            await AssertExtensionsAsync("11");
            if (supervisor.Starts[runtime.Id] != frankenStarts + 1 || supervisor.Starts["caddy"] != proxyStarts)
                throw new InvalidOperationException("A FrankenPHP batch must restart only its PHP process once.");
            await File.AppendAllTextAsync(source, "\ninvalid_site_directive\n");
            try { await manager.ApplyPhpExtensionChangesAsync(runtime.Id, [new("openssl", false), new("mbstring", false)]); throw new InvalidOperationException("Expected Caddyfile validation failure."); }
            catch (InvalidDataException error) when (error.Message.Contains("invalid_site_directive", StringComparison.Ordinal)) { }
            await File.WriteAllTextAsync(source, original);
            if (supervisor.Starts[runtime.Id] != frankenStarts + 1) throw new InvalidOperationException("An invalid Caddyfile restarted FrankenPHP.");
            supervisor.FailNextRuntimeStart = runtime.Id;
            try { await manager.ApplyPhpExtensionChangesAsync(runtime.Id, [new("openssl", false), new("mbstring", false)]); throw new InvalidOperationException("Expected extension activation failure."); }
            catch (IOException error) when (error.Message == "Test PHP start failure") { }
            await AssertExtensionsAsync("11");
            await manager.ApplyPhpExtensionChangesAsync(runtime.Id, [new("openssl", false), new("mbstring", false)]);
            await AssertExtensionsAsync("00");
            await manager.StopAllAsync();
            await manager.SetPhpExtensionEnabledAsync(runtime.Id, "openssl", true);
            if (supervisor.IsRunning(runtime.Id)) throw new InvalidOperationException("Stopped FrankenPHP was started by a toggle.");
            await manager.StartAllAsync();
            await AssertExtensionsAsync("10");
            Console.WriteLine("PASS: FrankenPHP modules load in HTTP, bundled DLLs toggle, failed activation recovers and stopped runtimes remain stopped.");

            await manager.RegisterPhpAsync(ntsExecutable);
            settings = await catalog.LoadAsync();
            var php = settings.PhpInstallations.Single();
            supervisor.FailNextProxyStart = true;
            try { await manager.SaveSiteAsync(site with { RuntimeId = php.Id }); throw new InvalidOperationException("Expected a rejected switch."); }
            catch (IOException error) when (error.Message == "Test proxy start failure") { }
            if (manager.Snapshot.Sites.Single().RuntimeId != runtime.Id) throw new InvalidOperationException("Failed switch was saved.");
            await ValidateOwnershipAsync(true);
            await manager.SaveSiteAsync(site with { RuntimeId = php.Id });
            await ValidateOwnershipAsync(false);
            var phpStarts = supervisor.Starts[php.Id];
            proxyStarts = supervisor.Starts["caddy"];
            await manager.ApplyPhpExtensionChangesAsync(php.Channel, [new("openssl", true), new("mbstring", true)]);
            await AssertExtensionsAsync("11");
            if (supervisor.Starts[php.Id] != phpStarts + 1 || supervisor.Starts["caddy"] != proxyStarts)
                throw new InvalidOperationException("A PHP NTS batch must restart only its PHP process once.");
            try { await manager.ApplyPhpExtensionChangesAsync(php.Channel, [new("openssl", false), new("unknownfixturemodule", true)]); throw new InvalidOperationException("Invalid batch was accepted."); }
            catch (InvalidOperationException error) when (error.Message.Contains("unknownfixturemodule", StringComparison.Ordinal)) { }
            if (supervisor.Starts[php.Id] != phpStarts + 1) throw new InvalidOperationException("An invalid batch restarted PHP NTS.");
            supervisor.FailNextRuntimeStart = php.Id;
            try { await manager.ApplyPhpExtensionChangesAsync(php.Channel, [new("openssl", false), new("mbstring", false)]); throw new InvalidOperationException("Expected PHP NTS activation failure."); }
            catch (IOException error) when (error.Message == "Test PHP start failure") { }
            await AssertExtensionsAsync("11");
            Console.WriteLine("PASS: PHP NTS batch restarts PHP once, leaves Caddy running, rejects invalid combinations and restores the preceding settings after failed startup.");
            await manager.RefreshAsync();
            if (manager.Snapshot.SiteRoots.GetValueOrDefault(site.Hostname) != documentRoot) throw new InvalidOperationException("Could not read the NTS site root.");
            await File.WriteAllTextAsync(source, original.Replace(documentRoot.Replace('\\', '/'), changedRoot.Replace('\\', '/')));
            await WaitForRootAsync(changedRoot);
            await File.WriteAllTextAsync(source, original);
            await WaitForRootAsync(documentRoot);
            const string ntsHostname = "nts.localhost";
            phpStarts = supervisor.Starts[php.Id];
            await manager.RenameSiteAsync(site.Hostname, ntsHostname);
            await AssertExtensionsAsync("11", ntsHostname);
            await manager.RenameSiteAsync(ntsHostname, site.Hostname);
            if (supervisor.Starts[php.Id] != phpStarts) throw new InvalidOperationException("Renaming an NTS site restarted PHP.");
            await manager.SaveSiteAsync(site);
            await ValidateOwnershipAsync(true);
            if (await File.ReadAllTextAsync(source) != original) throw new InvalidOperationException("Edited site source changed.");
            const string newHostname = "renamed.localhost";
            supervisor.FailNextProxyStart = true;
            try { await manager.RenameSiteAsync(site.Hostname, newHostname); throw new InvalidOperationException("Expected hostname activation failure."); }
            catch (IOException error) when (error.Message == "Test proxy start failure") { }
            await ValidateOwnershipAsync(true);
            if ((await catalog.LoadAsync()).Web.Sites.Single().Hostname != site.Hostname) throw new InvalidOperationException("Failed rename was persisted.");
            await manager.RenameSiteAsync(site.Hostname, newHostname);
            var renamed = (await catalog.LoadAsync()).Web.Sites.Single();
            if (renamed.Hostname != newHostname || renamed.RuntimeId != site.RuntimeId || renamed.DocumentRoot != site.DocumentRoot
                || !renamed.Enabled || CaddyStack.SiteFile(root, renamed) != source || await File.ReadAllTextAsync(source) != original
                || manager.Snapshot.SiteRoots.GetValueOrDefault(newHostname) != documentRoot || manager.Snapshot.SiteRoots.ContainsKey(site.Hostname))
                throw new InvalidOperationException("Rename must preserve the site configuration, runtime, and root.");
            using (var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }))
            {
                client.DefaultRequestHeaders.Host = newHostname;
                using var response = await client.GetAsync($"http://127.0.0.1:{supervisor.Port}/");
                if (!response.IsSuccessStatusCode || !response.Headers.TryGetValues("X-Site-Custom", out var header) || header.Single() != "yes")
                    throw new InvalidOperationException("The renamed hostname did not serve the custom Caddy directives.");
                client.DefaultRequestHeaders.Host = site.Hostname;
                using var oldResponse = await client.GetAsync($"http://127.0.0.1:{supervisor.Port}/");
                if (oldResponse.Headers.Contains("X-Site-Custom")) throw new InvalidOperationException("The previous hostname still matched the site.");
            }
            await File.WriteAllTextAsync(source, original.Replace(documentRoot.Replace('\\', '/'), changedRoot.Replace('\\', '/')));
            await WaitForRootAsync(changedRoot, newHostname);
            await manager.SaveSiteAsync(site with { Enabled = false });
            if (manager.Snapshot.Sites.Select(item => CaddyStack.SiteFile(root, item)).Distinct().Count() != 2)
                throw new InvalidOperationException("Reusing the previous hostname shared its old Caddyfile.");
            try { await manager.RenameSiteAsync(newHostname, site.Hostname); throw new InvalidOperationException("Expected duplicate hostname rejection."); }
            catch (InvalidOperationException error) when (error.Message.Contains("already uses", StringComparison.Ordinal)) { }
            Console.WriteLine("PASS: hostname rename preserves custom directives, changes live routing, keeps watching the same file, resists collisions and restores the previous route after a failed restart.");
            Console.WriteLine("PASS: automatic root refresh handles writes, invalid content, atomic replacement, deletion and recreation without manual refresh.");
            Console.WriteLine("PASS: real Caddy and FrankenPHP validated their assigned site imports and preserved custom directives.");

            await File.WriteAllTextAsync(Path.Combine(changedRoot, "index.php"), "<?php echo PHP_SAPI;");
            var ntsSite = manager.Snapshot.Sites.Single(item => item.Hostname == site.Hostname) with { RuntimeId = php.Id, Enabled = true };
            await manager.SaveSiteAsync(ntsSite);
            await CheckRouteAsync(ntsSite.Hostname, "cgi-fcgi");
            var runningPhpStarts = supervisor.Starts[php.Id];
            await manager.SaveSiteAsync(renamed with { Enabled = false });
            await CheckRouteAsync(renamed.Hostname, null);
            await CheckRouteAsync(ntsSite.Hostname, "cgi-fcgi");
            if (!supervisor.IsRunning(runtime.Id) || supervisor.Starts[php.Id] != runningPhpStarts)
                throw new InvalidOperationException("Disabling a site changed the state of its PHP services unnecessarily.");

            await manager.SaveSiteAsync(ntsSite with { Enabled = false });
            if (!supervisor.IsRunning("caddy") || (await catalog.LoadAsync()).Web.Sites.Any(item => item.Enabled))
                throw new InvalidOperationException("Disabling the last site must leave Caddy ready to serve future sites.");
            await CheckRouteAsync(ntsSite.Hostname, null, expect404: true);
            supervisor.FailNextProxyStart = true;
            try { await manager.SaveSiteAsync(renamed); throw new InvalidOperationException("Expected activation failure."); }
            catch (IOException error) when (error.Message == "Test proxy start failure") { }
            await CheckRouteAsync(renamed.Hostname, null, expect404: true);
            if ((await catalog.LoadAsync()).Web.Sites.Any(item => item.Enabled))
                throw new InvalidOperationException("Failed activation changed saved site state.");
            await manager.SaveSiteAsync(renamed);
            await CheckRouteAsync(renamed.Hostname, "frankenphp");
            await manager.StopServiceAsync("caddy");
            var stoppedProxyStarts = supervisor.Starts["caddy"];
            await manager.SaveSiteAsync(renamed with { Enabled = false });
            await manager.SaveSiteAsync(renamed);
            if (supervisor.Starts["caddy"] != stoppedProxyStarts || supervisor.IsRunning("caddy") || !(await catalog.LoadAsync()).Web.Sites.Single(item => item.Hostname == renamed.Hostname).Enabled)
                throw new InvalidOperationException("Changing activation with Caddy stopped must save the setting without starting Caddy.");
            await manager.StartServiceAsync("caddy");
            await CheckRouteAsync(renamed.Hostname, "frankenphp");
            Console.WriteLine("PASS: site activation applies live, preserves other sites, handles the last route, recovers after failed activation, and saves changes with Caddy stopped.");

            async Task CheckRouteAsync(string host, string? expectedSapi, bool expect404 = false)
            {
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                client.DefaultRequestHeaders.Host = host;
                using var response = await client.GetAsync($"http://127.0.0.1:{supervisor.Port}/");
                var body = (await response.Content.ReadAsStringAsync()).Trim();
                if (expectedSapi is null ? body.Length != 0 || (expect404 && response.StatusCode != HttpStatusCode.NotFound)
                    : !response.IsSuccessStatusCode || body != expectedSapi)
                    throw new InvalidOperationException($"Unexpected response for {host}: {response.StatusCode} {body}");
            }

            async Task WaitForRootAsync(string? expected, string? hostname = null)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (manager.Snapshot.SiteRoots.GetValueOrDefault(hostname ?? site.Hostname) != expected)
                {
                    if (DateTime.UtcNow >= deadline) throw new InvalidOperationException($"Automatic root refresh did not reach {expected ?? "(unknown)"}.");
                    await Task.Delay(50);
                }
            }

            async Task AssertExtensionsAsync(string expected, string? hostname = null)
            {
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                client.DefaultRequestHeaders.Host = hostname ?? site.Hostname;
                var actual = await client.GetStringAsync($"http://127.0.0.1:{supervisor.Port}/extensions.php");
                if (actual != expected) throw new InvalidOperationException($"Expected extension state {expected}, got {actual}.");
            }

            async Task ValidateOwnershipAsync(bool franken)
            {
                var active = await CaddyStack.LoadAsync(root, default);
                var definitions = await active.PrepareAsync(root, supervisor, settings, default);
                foreach (var service in definitions)
                {
                    var command = service.Command with { Arguments = ["adapt", "--config", service.Command.Arguments.Last()] };
                    var adapted = await supervisor.RunAsync(command, TimeSpan.FromSeconds(30));
                    if (adapted.ExitCode != 0 || adapted.StandardOutput.Contains("X-Site-Custom", StringComparison.OrdinalIgnoreCase)
                        != (franken ? service.Kind == ServiceKind.FrankenPhp : service.Kind == ServiceKind.Caddy))
                        throw new InvalidOperationException($"Unexpected Caddyfile ownership for {service.Id}: {adapted.StandardError}");
                }
                if (definitions.Count != (franken ? 2 : 1)) throw new InvalidOperationException("Unexpected backend count.");

                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                client.DefaultRequestHeaders.Host = "app.localhost";
                using var response = await client.GetAsync($"http://127.0.0.1:{supervisor.Port}/");
                var sapi = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode || sapi.Trim() != (franken ? "frankenphp" : "cgi-fcgi")
                    || response.Headers.GetValues("X-Site-Custom").Single() != "yes")
                    throw new InvalidOperationException($"Unexpected PHP response: {response.StatusCode} {sapi}");
                Console.WriteLine($"PASS: {site.Hostname} served by {sapi} with the custom site header.");

            }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); throw; }
        finally { Directory.Delete(root, true); }
    }
}
