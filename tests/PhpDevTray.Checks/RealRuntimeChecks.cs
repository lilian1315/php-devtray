using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PhpDevTray.Core;
using PhpDevTray.Infrastructure;
using PhpDevTray.Infrastructure.Processes;
using PhpDevTray.Infrastructure.Runtimes;

namespace PhpDevTray.Checks;

/// <summary>Explicit opt-in integration with verified upstream binaries. Never writes user PATH or hosts.</summary>
internal static class RealRuntimeChecks
{
    public static async Task RunAsync(string[] args)
    {
        var profile = RequiredArgument(args, "--profile");
        var cliBundle = RequiredArgument(args, "--cli-bundle");
        var report = RequiredArgument(args, "--report");
        foreach (var path in new[] { profile, cliBundle, report })
            Require(Path.IsPathFullyQualified(path), "Integration profile, CLI bundle, and report paths must be absolute.");
        Require(File.Exists(Path.Combine(cliBundle, "php.exe")), "Publish the PHP launcher before real-runtime integration.");
        var marker = Path.Combine(profile, ".real-runtime-check-profile");
        if (Directory.Exists(profile) && Directory.EnumerateFileSystemEntries(profile).Any() && !File.Exists(marker))
            throw new InvalidOperationException("Refusing to use a nonempty directory without the isolated-integration marker.");
        Directory.CreateDirectory(profile);
        await File.WriteAllTextAsync(marker, "PHP DevTray isolated integration profile\n");
        Require(IsPortFree(8080), "Nginx integration needs free loopback port 8080; unrelated listeners are never stopped.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var ct = timeout.Token;
        var lines = new List<string>
        {
            "# Actual runtime integration", "", $"Run: {DateTimeOffset.UtcNow:O}", "",
            "Verified upstream downloads, isolated application profile, loopback-only listeners. No user PATH or hosts modifications.", ""
        };
        await using var supervisor = new WindowsProcessSupervisor();
        var catalog = new RuntimeCatalog(profile, supervisor, cliBundle);
        if (!File.Exists(Path.Combine(profile, "settings.json")))
        {
            var smtpPort = FreePort();
            int httpPort;
            do { httpPort = FreePort(); } while (httpPort == smtpPort);
            await catalog.SaveAsync(new AppSettings { SmtpPort = smtpPort, MailpitHttpPort = httpPort }, ct);
        }
        var manager = new EnvironmentManager(profile, supervisor, catalog);
        Process? independentCli = null;
        try
        {
            await manager.InitializeAsync(ct);
            var availablePhp = await manager.GetAvailableReleasesAsync(ServiceKind.Php, ct);
            var selectedPhp = new[] { "8.4", "8.5" }.Select(channel => availablePhp.FirstOrDefault(r => r.Channel == channel)).ToArray();
            Require(selectedPhp.All(r => r is not null), "The official catalog must provide both PHP 8.4 and 8.5 for this integration scenario.");
            foreach (var php in selectedPhp.Cast<RuntimeRelease>())
            {
                if (!manager.Snapshot.PhpInstallations.Any(p => p.Channel == php.Channel))
                    await manager.InstallReleaseAsync(php.Id, new ConsoleInstallProgress(), ct);
            }
            foreach (var kind in new[] { ServiceKind.Nginx, ServiceKind.Mailpit })
            {
                if (manager.Snapshot.Services.Any(s => s.Kind == kind && s.Status != ServiceStatus.NotInstalled)) continue;
                var release = (await manager.GetAvailableReleasesAsync(kind, ct)).FirstOrDefault()
                    ?? throw new InvalidOperationException($"No verified official {kind} release is available.");
                await manager.InstallReleaseAsync(release.Id, new ConsoleInstallProgress(), ct);
            }
            Pass("Downloaded, verified, and registered actual PHP 8.4, PHP 8.5, Nginx, and Mailpit releases.");
            foreach (var php in manager.Snapshot.PhpInstallations) lines.Add($"- Installed PHP {php.Version} ({php.Channel}).");

            var projects = Path.Combine(profile, "projects with spaces é");
            var alphaRoot = Path.Combine(projects, "alpha");
            var betaRoot = Path.Combine(projects, "beta");
            Directory.CreateDirectory(alphaRoot);
            Directory.CreateDirectory(betaRoot);
            const string infoScript = "<?php header('Content-Type: application/json'); echo json_encode(['version'=>PHP_VERSION,'openssl'=>extension_loaded('openssl')]);";
            foreach (var root in new[] { alphaRoot, betaRoot })
            {
                await File.WriteAllTextAsync(Path.Combine(root, "index.php"), infoScript, ct);
                await File.WriteAllTextAsync(Path.Combine(root, "image.jpg"), "<?php echo 'UNEXPECTED_IMAGE_EXECUTION'; ?>", ct);
            }
            var alpha = new SiteDefinition { Id = "integration-alpha", Hostname = "alpha.integration.test", DocumentRoot = alphaRoot, PhpChannel = "8.4" };
            var beta = new SiteDefinition { Id = "integration-beta", Hostname = "beta.integration.test", DocumentRoot = betaRoot, PhpChannel = "8.5" };
            await manager.SaveSiteAsync(alpha, ct);
            await manager.SaveSiteAsync(beta, ct);
            await manager.SetDefaultPhpAsync("8.4", ct);
            await manager.StartAllAsync(ct);
            foreach (var service in manager.Snapshot.Services.Where(s => s.Kind != ServiceKind.Php))
                lines.Add($"- Installed {service.DisplayName} {service.Version}.");
            await AssertPhpAsync(alpha, "8.4", null);
            await AssertPhpAsync(beta, "8.5", null);
            Pass("Two real PHP channels execute through separate Nginx sites, including document roots with spaces and non-ASCII characters.");

            var extensions = await manager.GetPhpExtensionsAsync("8.4", ct);
            var openssl = extensions.Single(e => e.Name.Equals("openssl", StringComparison.OrdinalIgnoreCase));
            Require(openssl.IsAvailable && !openssl.IsBuiltIn, "Expected bundled dynamic OpenSSL extension in PHP 8.4.");
            await manager.SetPhpExtensionEnabledAsync("8.4", "openssl", true, ct);
            await AssertPhpAsync(alpha, "8.4", true);
            await AssertPhpAsync(beta, "8.5", null);
            await manager.SetPhpExtensionEnabledAsync("8.4", "openssl", false, ct);
            await AssertPhpAsync(alpha, "8.4", false);
            Pass("OpenSSL enable/disable is reflected by actual PHP requests while the other channel remains available.");

            await manager.SetPhpExtensionEnabledAsync("8.4", "opcache", true, ct);
            var enabled = await manager.GetPhpExtensionsAsync("8.4", ct);
            Require(enabled.Any(e => e.Name == "opcache" && e.IsEnabled), "OPcache must load through zend_extension.");
            await manager.SetPhpExtensionEnabledAsync("8.4", "opcache", false, ct);
            Pass("Bundled OPcache uses Zend-extension loading and can be disabled again.");

            alpha = alpha with { PhpChannel = "8.5" };
            await manager.SaveSiteAsync(alpha, ct);
            await AssertPhpAsync(alpha, "8.5", null);
            Require(manager.Snapshot.DefaultPhpChannel == "8.4", "Site switch must not change the CLI default.");
            Pass("Per-site PHP switching changes actual request routing independently of the CLI default.");

            var sourcePath = Path.Combine(profile, "user", "nginx", "sites-available", alpha.Id + ".conf");
            var originalSource = await File.ReadAllTextAsync(sourcePath, ct);
            try
            {
                await File.WriteAllTextAsync(sourcePath, originalSource + "\nnot_a_real_nginx_directive;\n", ct);
                var rejected = false;
                try { await manager.SetSiteEnabledAsync(beta.Id, false, ct); }
                catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { rejected = true; }
                Require(rejected, "Nginx must reject invalid candidate configuration.");
                await AssertPhpAsync(alpha, "8.5", null);
                Require(manager.Snapshot.Sites.Single(s => s.Id == beta.Id).Enabled, "Failed candidate must not commit the site change.");
            }
            finally { await File.WriteAllTextAsync(sourcePath, originalSource, CancellationToken.None); }
            Pass("An invalid user-edited Nginx candidate leaves actual working request routing and site metadata intact.");

            foreach (var route in new[] { "/missing.php", "/image.jpg/missing.php", "/index.php/path-info" })
            {
                using var response = await RequestAsync(alpha, route);
                Require(response.StatusCode == HttpStatusCode.NotFound, $"Unsafe/nonexistent script route {route} must return404.");
                Require(!(await response.Content.ReadAsStringAsync(ct)).Contains("UNEXPECTED_IMAGE_EXECUTION", StringComparison.Ordinal), "Image upload content must never be guessed as a script.");
            }
            Pass("Actual Nginx/PHP rejects nonexistent scripts and path-guessing URLs.");

            var subject = "PDT PHP mail " + Guid.NewGuid().ToString("N");
            var mailCode = "<?php header('Content-Type: text/plain'); echo mail('capture@example.test', '" + subject + "', 'integration body', 'From: developer@example.test') ? 'captured' : 'failed';";
            await File.WriteAllTextAsync(Path.Combine(alphaRoot, "send.php"), mailCode, ct);
            using (var response = await RequestAsync(alpha, "/send.php"))
            {
                Require(response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync(ct)) == "captured", "PHP mail() must be accepted by the managed Mailpit listener.");
            }
            var smtpSubject = "PDT SMTP " + Guid.NewGuid().ToString("N");
            await SendSmtpAsync(manager.Snapshot.SmtpPort, smtpSubject, ct);
            await WaitUntilAsync(async () =>
            {
                var messages = await manager.GetMessagesAsync(ct);
                return messages.Any(m => m.Subject == subject) && messages.Any(m => m.Subject == smtpSubject);
            }, TimeSpan.FromSeconds(15), ct);
            var capturedMessage = (await manager.GetMessagesAsync(ct)).Single(m => m.Subject == subject);
            Require((await manager.GetMessageTextAsync(capturedMessage.Id, ct)).Contains("integration body", StringComparison.Ordinal), "Mailpit API must provide the captured actual message body.");
            Pass("Actual PHP mail() and framework-style SMTP messages are captured and readable through Mailpit's API.");

            var cliReleaseFlag = Path.Combine(profile, "independent-cli-release.txt");
            if (File.Exists(cliReleaseFlag)) File.Delete(cliReleaseFlag);
            var cliStart = new ProcessStartInfo(Path.Combine(profile, "bin", "php.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            cliStart.ArgumentList.Add("-r");
            var phpFlag = cliReleaseFlag.Replace('\\', '/').Replace("'", "\\'", StringComparison.Ordinal);
            cliStart.ArgumentList.Add("$deadline=microtime(true)+60; while(!file_exists('" + phpFlag + "') && microtime(true)<$deadline) { usleep(100000); } echo PHP_VERSION; exit(7);");
            independentCli = Process.Start(cliStart) ?? throw new InvalidOperationException("Cannot launch the independent PHP CLI.");

            var requestMarker = Path.Combine(alphaRoot, "slow-started.txt");
            if (File.Exists(requestMarker)) File.Delete(requestMarker);
            await File.WriteAllTextAsync(Path.Combine(alphaRoot, "slow.php"), "<?php file_put_contents(__DIR__.'/slow-started.txt','started'); usleep(1200000); echo 'drained';", ct);
            var slowRequest = RequestAsync(alpha, "/slow.php");
            await WaitUntilAsync(() => Task.FromResult(File.Exists(requestMarker)), TimeSpan.FromSeconds(10), ct);
            await manager.StopAllAsync(ct);
            using (var drained = await slowRequest)
                Require(drained.IsSuccessStatusCode && (await drained.Content.ReadAsStringAsync(ct)) == "drained", "Nginx shutdown must let the active PHP request complete before stopping PHP.");
            Require(manager.Snapshot.Services.All(s => s.Status == ServiceStatus.Stopped), "Stop All must leave all actual service processes stopped.");
            Pass("Stop All drains an in-flight PHP request before stopping the actual PHP and Mailpit processes.");

            await manager.StartServiceAsync("mailpit", ct);
            Require((await manager.GetMessagesAsync(ct)).Any(m => m.Subject == subject), "Mailpit capture must persist after service restart.");
            await manager.DisposeAsync();
            Require(!independentCli.HasExited, "The independent CLI must remain alive after the manager has exited.");
            await File.WriteAllTextAsync(cliReleaseFlag, "manager disposed", ct);
            await independentCli.WaitForExitAsync(ct);
            var cliOutput = await independentCli.StandardOutput.ReadToEndAsync(ct);
            var cliError = await independentCli.StandardError.ReadToEndAsync(ct);
            Require(independentCli.ExitCode == 7 && cliOutput.StartsWith("8.4.", StringComparison.Ordinal), "The independent CLI must survive manager exit and preserve version/exit code. " + cliError);
            Pass("Mail persists after restart; independent CLI survives manager exit and forwards the selected version and exit code.");
            lines.Add("");
            lines.Add("Result: **PASS**. No PATH/hosts changes and no managed services left running.");
        }
        catch (Exception error)
        {
            lines.Add("");
            lines.Add("Result: **FAIL**.");
            lines.Add("```");
            lines.Add(error.ToString());
            lines.Add("```");
            throw;
        }
        finally
        {
            await manager.DisposeAsync();
            if (independentCli is { HasExited: false }) independentCli.Kill(true);
            independentCli?.Dispose();
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            await File.WriteAllLinesAsync(report, lines, CancellationToken.None);
        }
        return;

        void Pass(string text) { Console.WriteLine("PASS: " + text); lines.Add("- PASS: " + text); }
        async Task<HttpResponseMessage> RequestAsync(SiteDefinition site, string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{site.Port}{path}");
            request.Headers.Host = site.Hostname;
            // A shared client is unnecessary for these explicit integration probes; dispose its handler with the response body read.
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        }
        async Task AssertPhpAsync(SiteDefinition site, string channel, bool? opensslEnabled)
        {
            using var response = await RequestAsync(site, "/index.php");
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            Require(json.RootElement.GetProperty("version").GetString()!.StartsWith(channel + ".", StringComparison.Ordinal), $"{site.Hostname} must execute PHP {channel}.");
            if (opensslEnabled.HasValue) Require(json.RootElement.GetProperty("openssl").GetBoolean() == opensslEnabled, "Actual OpenSSL state differs from the selected extension state.");
        }
    }

    private static string RequiredArgument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 == args.Length) throw new ArgumentException($"Required integration argument: {name}");
        return Path.GetFullPath(args[index + 1]);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static bool IsPortFree(int port) { try { var listener = new TcpListener(IPAddress.Loopback, port); listener.Server.ExclusiveAddressUse = true; listener.Start(); listener.Stop(); return true; } catch (SocketException) { return false; } }
    private static async Task WaitUntilAsync(Func<Task<bool>> check, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        while (!await check()) { if (watch.Elapsed > timeout) throw new TimeoutException("Integration condition did not become ready."); await Task.Delay(100, cancellationToken); }
    }
    private static async Task SendSmtpAsync(int port, string subject, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, ct);
        using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
        using var writer = new StreamWriter(client.GetStream(), Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
        await ExpectAsync("220");
        foreach (var command in new[] { "HELO local.integration.test", "MAIL FROM:<developer@example.test>", "RCPT TO:<capture@example.test>" }) { await writer.WriteLineAsync(command.AsMemory(), ct); await ExpectAsync("250"); }
        await writer.WriteLineAsync("DATA".AsMemory(), ct); await ExpectAsync("354");
        await writer.WriteLineAsync(($"From: developer@example.test\r\nTo: capture@example.test\r\nSubject: {subject}\r\n\r\nframework-style SMTP body\r\n.").AsMemory(), ct); await ExpectAsync("250");
        await writer.WriteLineAsync("QUIT".AsMemory(), ct); await ExpectAsync("221");
        async Task ExpectAsync(string code) { var line = await reader.ReadLineAsync(ct); Require(line?.StartsWith(code, StringComparison.Ordinal) == true, "SMTP expected " + code + ": " + line); }
    }
    private sealed class ConsoleInstallProgress : IProgress<RuntimeInstallProgress>
    {
        private string? _phase;
        public void Report(RuntimeInstallProgress value) { if (_phase == value.Phase) return; _phase = value.Phase; Console.WriteLine("INSTALL: " + value.Phase); }
    }
}
