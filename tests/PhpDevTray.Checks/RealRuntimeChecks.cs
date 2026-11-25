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
            foreach (var kind in new[] { ServiceKind.Mailpit })
            {
                if (manager.Snapshot.Services.Any(s => s.Kind == kind && s.Status != ServiceStatus.NotInstalled)) continue;
                var release = (await manager.GetAvailableReleasesAsync(kind, ct)).FirstOrDefault()
                    ?? throw new InvalidOperationException($"No verified official {kind} release is available.");
                await manager.InstallReleaseAsync(release.Id, new ConsoleInstallProgress(), ct);
            }
            Pass("Downloaded, verified, and registered actual PHP 8.4, PHP 8.5, and Mailpit releases.");
            foreach (var php in manager.Snapshot.PhpInstallations) lines.Add($"- Installed PHP {php.Version} ({php.Channel}).");

            await manager.SetDefaultPhpAsync("8.4", ct);
            await manager.StartAllAsync(ct);
            foreach (var service in manager.Snapshot.Services.Where(s => s.Kind != ServiceKind.Php))
                lines.Add($"- Installed {service.DisplayName} {service.Version}.");

            var extensions = await manager.GetPhpExtensionsAsync("8.4", ct);
            var openssl = extensions.Single(e => e.Name.Equals("openssl", StringComparison.OrdinalIgnoreCase));
            Require(openssl.IsAvailable && !openssl.IsBuiltIn, "Expected bundled dynamic OpenSSL extension in PHP 8.4.");
            await manager.SetPhpExtensionEnabledAsync("8.4", "openssl", true, ct);
            await manager.SetPhpExtensionEnabledAsync("8.4", "openssl", false, ct);

            await manager.SetPhpExtensionEnabledAsync("8.4", "opcache", true, ct);
            var enabled = await manager.GetPhpExtensionsAsync("8.4", ct);
            Require(enabled.Any(e => e.Name == "opcache" && e.IsEnabled), "OPcache must load through zend_extension.");
            await manager.SetPhpExtensionEnabledAsync("8.4", "opcache", false, ct);
            Pass("Bundled OPcache uses Zend-extension loading and can be disabled again.");

            var smtpSubject = "PDT SMTP " + Guid.NewGuid().ToString("N");
            await SendSmtpAsync(manager.Snapshot.SmtpPort, smtpSubject, ct);
            await WaitUntilAsync(async () =>
            {
                var messages = await manager.GetMessagesAsync(ct);
                return messages.Any(m => m.Subject == smtpSubject);
            }, TimeSpan.FromSeconds(15), ct);
            var capturedMessage = (await manager.GetMessagesAsync(ct)).Single(m => m.Subject == smtpSubject);
            Require((await manager.GetMessageTextAsync(capturedMessage.Id, ct)).Contains("framework-style SMTP body", StringComparison.Ordinal), "Mailpit API must provide the captured actual message body.");
            Pass("SMTP messages are captured and readable through Mailpit's API.");

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

            await manager.StopAllAsync(ct);
            Require(manager.Snapshot.Services.All(s => s.Status is ServiceStatus.Stopped or ServiceStatus.NotInstalled), "Stop All must leave all actual service processes stopped.");

            await manager.StartServiceAsync("mailpit", ct);
            Require((await manager.GetMessagesAsync(ct)).Any(m => m.Subject == smtpSubject), "Mailpit capture must persist after service restart.");
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
