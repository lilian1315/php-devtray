using System.Net;
using System.Net.Sockets;
using PhpDevTray.Infrastructure.Processes;
using PhpDevTray.Core;
using System.Text.Json;
using PhpDevTray.Infrastructure;

namespace PhpDevTray.Checks;

internal static class RealCaddyChecks
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("Expected --real-caddy <caddy.exe> <first frankenphp.exe> <second frankenphp.exe>.");
        var root = Directory.CreateTempSubdirectory("PhpDevTrayCaddy-").FullName;
        try
        {
            await using var manager = Bootstrapper.Create(root);
            await manager.InitializeAsync();
            var runtimes = new List<object>();
            var sites = new List<WebSite>();
            for (var i = 1; i <= 2; i++)
            {
                var host = $"site{i}.localhost";
                var site = Directory.CreateDirectory(Path.Combine(root, host)).FullName;
                await File.WriteAllTextAsync(Path.Combine(site, "index.php"), $"<?php echo 'routed-{i}';");
                await using var probe = new WindowsProcessSupervisor();
                var executable = Path.GetFullPath(args[i + 1]);
                var result = await probe.RunAsync(new ProcessCommand { ExecutablePath = executable, WorkingDirectory = Path.GetDirectoryName(executable),
                    Arguments = ["version"] }, TimeSpan.FromSeconds(15));
                var versions = System.Text.RegularExpressions.Regex.Match(result.StandardOutput, @"FrankenPHP (\d+\.\d+\.\d+) PHP (\d+\.\d+\.\d+)");
                if (result.ExitCode != 0 || !versions.Success) throw new InvalidDataException("The runtime did not report its FrankenPHP and PHP versions.");
                runtimes.Add(new { id = $"version{i}", executable, port = 9080 + i, phpVersion = versions.Groups[2].Value, version = versions.Groups[1].Value });
                sites.Add(new WebSite(host, site, $"franken-version{i}"));
            }
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"),
                JsonSerializer.Serialize(new { web = new { executable = Path.GetFullPath(args[1]), runtimes, sites } }));
            await manager.RefreshAsync();
            await manager.StartAllAsync();
            using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
            handler.ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(IPAddress.Loopback, context.DnsEndPoint.Port, token); }
                catch { socket.Dispose(); throw; }
                return new NetworkStream(socket, ownsSocket: true);
            };
            using var client = new HttpClient(handler);
            for (var i = 1; i <= 2; i++)
            {
                var url = $"https://site{i}.localhost/";
                using var redirect = await client.GetAsync($"http://site{i}.localhost/");
                if (redirect.Headers.Location?.AbsoluteUri != url || await client.GetStringAsync(url) != $"routed-{i}")
                    throw new InvalidOperationException($"Incorrect redirect or PHP response for {url}");
            }
            await manager.StopAllAsync();
            Console.WriteLine("PASS: two FrankenPHP backends served PHP through Caddy with local HTTPS and HTTP redirects.");
        }
        finally
        {
            var certificate = Path.Combine(root, "data", "caddy", "pki", "authorities", "local", "root.crt");
            if (File.Exists(certificate))
            {
                await using var supervisor = new WindowsProcessSupervisor();
                var cleanup = await supervisor.RunAsync(new ProcessCommand { ExecutablePath = Path.GetFullPath(args[1]),
                    Arguments = ["untrust", "--cert", certificate] }, TimeSpan.FromSeconds(30));
                if (cleanup.ExitCode != 0) throw new InvalidOperationException($"Could not untrust the test CA at {certificate}. {cleanup.StandardError}");
            }
            Directory.Delete(root, true);
        }
    }
}
