using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PhpDevTray.Core;
using PhpDevTray.Infrastructure.Processes;

namespace PhpDevTray.Checks;

internal static class ProcessChecks
{
    public static async Task<bool> TryHandleChildModeAsync(string[] args)
    {
        if (args.Length == 0 || !args[0].StartsWith("--process-check-", StringComparison.Ordinal)) return false;
        Console.OutputEncoding = new UTF8Encoding(false);
        switch (args[0])
        {
            case "--process-check-echo":
                Console.Write(JsonSerializer.Serialize(new EchoOutput(args.Skip(1).ToArray(), Environment.GetEnvironmentVariable("PDT_PROCESS_CHECK"))));
                Console.Error.Write("stderr-check");
                break;
            case "--process-check-wait":
                await PublishFixtureFileAsync(args[1], Environment.ProcessId.ToString());
                await Task.Delay(Timeout.Infinite);
                break;
            case "--process-check-tree":
                using (var descendant = Process.Start(ToStartInfo(CurrentCommand("--process-check-wait", args[1] + ".child")))!)
                {
                    await PublishFixtureFileAsync(args[1], JsonSerializer.Serialize(new TreeIds(Environment.ProcessId, descendant.Id)));
                    await Task.Delay(Timeout.Infinite);
                }
                break;
            case "--process-check-owner":
                await using (var owner = new WindowsProcessSupervisor())
                {
                    await owner.StartAsync(Definition("owner-tree", CurrentCommand("--process-check-tree", args[1])));
                    await PublishFixtureFileAsync(args[1] + ".ready", "ready");
                    await Task.Delay(Timeout.Infinite);
                }
                break;
            case "--process-check-endpoint":
                await RunEndpointAsync(args[1], int.Parse(args[2]));
                break;
            default:
                throw new InvalidOperationException("Unknown process check child mode.");
        }
        return true;
    }

    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Process checks must run on Windows.");
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PhpDevTray.ProcessChecks." + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            await using var supervisor = new WindowsProcessSupervisor();
            var arguments = new[] { "", "with spaces", "é漢字", "a\"b", "ends with slash\\", "two\\\"quoted", "& echo not-a-shell-command" };
            var echo = CurrentCommand(["--process-check-echo", .. arguments]) with
            {
                EnvironmentVariables = new Dictionary<string, string> { ["PDT_PROCESS_CHECK"] = "environment value é" }
            };
            var echoed = await supervisor.RunAsync(echo, TimeSpan.FromSeconds(10));
            var payload = JsonSerializer.Deserialize<EchoOutput>(echoed.StandardOutput)!;
            Assert(echoed.ExitCode == 0 && payload.Arguments.SequenceEqual(arguments), "Windows argument quoting roundtrip failed.");
            Assert(payload.Environment == "environment value é" && echoed.StandardError == "stderr-check", "Environment or redirected streams were lost.");
            Console.WriteLine("PASS: suspended command launch, Unicode/empty/quoted argument roundtrip, environment, stdout/stderr.");

            var timeoutFile = Path.Combine(root, "timeout.pid");
            await ExpectTimeoutAsync(() => supervisor.RunAsync(CurrentCommand("--process-check-wait", timeoutFile), TimeSpan.FromSeconds(1)));
            if (File.Exists(timeoutFile)) await AssertGoneAsync(int.Parse(await File.ReadAllTextAsync(timeoutFile)));
            Console.WriteLine("PASS: timed-out commands are terminated.");

            var firstTree = Path.Combine(root, "tree.json");
            await supervisor.StartAsync(Definition("tree", CurrentCommand("--process-check-tree", firstTree)));
            await WaitForFileAsync(firstTree);
            var tree = JsonSerializer.Deserialize<TreeIds>(await File.ReadAllTextAsync(firstTree))!;
            await WaitForFileAsync(firstTree + ".child");
            var siblingFile = Path.Combine(root, "sibling.pid");
            await supervisor.StartAsync(Definition("sibling", CurrentCommand("--process-check-wait", siblingFile)));
            await supervisor.StopAsync("tree", TimeSpan.FromSeconds(3));
            await AssertGoneAsync(tree.Parent);
            await AssertGoneAsync(tree.Child);
            Assert(supervisor.IsRunning("sibling"), "Stopping one service terminated an unrelated service.");
            await supervisor.StopAsync("sibling", TimeSpan.FromSeconds(3));
            Console.WriteLine("PASS: per-service Job containment kills descendants and preserves sibling services.");

            using (var occupied = new TcpListener(IPAddress.Loopback, 0))
            {
                occupied.Start();
                var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
                var sentinel = Path.Combine(root, "occupied.pid");
                try
                {
                    await supervisor.StartAsync(Definition("occupied", CurrentCommand("--process-check-wait", sentinel)) with
                    {
                        HealthEndpoints = [new(HealthCheckKind.Tcp, "127.0.0.1", port)]
                    });
                    throw new InvalidOperationException("Occupied-port startup was incorrectly accepted.");
                }
                catch (InvalidOperationException exception) when (exception.Message.Contains("unavailable", StringComparison.Ordinal)) { }
                Assert(!File.Exists(sentinel), "Port-conflict startup ran the executable before rejecting the conflict.");
            }
            Console.WriteLine("PASS: port conflicts are rejected before process execution.");

            foreach (var (name, kind) in new[] { ("fastcgi", HealthCheckKind.FastCgi), ("smtp", HealthCheckKind.Smtp), ("http", HealthCheckKind.Http) })
            {
                var port = GetFreePort();
                await supervisor.StartAsync(Definition(name, CurrentCommand("--process-check-endpoint", name, port.ToString())) with
                {
                    HealthEndpoints = [new(kind, "127.0.0.1", port)]
                });
                Assert(supervisor.IsRunning(name), $"Owned {name} protocol readiness was not accepted.");
                await supervisor.StopAsync(name, TimeSpan.FromSeconds(3));
            }
            Console.WriteLine("PASS: owned FastCGI, SMTP and HTTP protocol endpoints become ready.");

            var foreignFile = Path.Combine(root, "foreign.pid");
            var foreignPort = GetFreePort();
            var pendingStart = supervisor.StartAsync(Definition("foreign", CurrentCommand("--process-check-wait", foreignFile)) with
            {
                HealthEndpoints = [new(HealthCheckKind.Tcp, "127.0.0.1", foreignPort)], StartupTimeout = TimeSpan.FromSeconds(2)
            });
            await WaitForFileAsync(foreignFile);
            using (var foreignListener = new TcpListener(IPAddress.Loopback, foreignPort))
            {
                foreignListener.Start();
                await ExpectTimeoutAsync(() => pendingStart);
                Assert(foreignListener.Server.IsBound, "A foreign listener was stopped by the supervisor.");
            }
            await AssertGoneAsync(int.Parse(await File.ReadAllTextAsync(foreignFile)));
            Console.WriteLine("PASS: a listener acquired after preflight cannot impersonate the owned service.");

            var crashFile = Path.Combine(root, "owner-tree.json");
            using (var owner = Process.Start(ToStartInfo(CurrentCommand("--process-check-owner", crashFile)))!)
            {
                var stdout = owner.StandardOutput.ReadToEndAsync();
                var stderr = owner.StandardError.ReadToEndAsync();
                try
                {
                    await WaitForFileAsync(crashFile + ".ready");
                    await WaitForFileAsync(crashFile + ".child");
                    var owned = JsonSerializer.Deserialize<TreeIds>(await File.ReadAllTextAsync(crashFile))!;
                    owner.Kill(entireProcessTree: false);
                    await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    await AssertGoneAsync(owned.Parent);
                    await AssertGoneAsync(owned.Child);
                }
                finally
                {
                    if (!owner.HasExited) { owner.Kill(entireProcessTree: true); await owner.WaitForExitAsync(); }
                    await Task.WhenAll(stdout, stderr);
                }
            }
            Console.WriteLine("PASS: forced owner termination closes Job handles and kills owned descendants.");
        }
        finally
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (root.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("PhpDevTray.ProcessChecks.", StringComparison.Ordinal))
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }
    }

    private static ProcessCommand CurrentCommand(params string[] arguments)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No current executable path.");
        IReadOnlyList<string> forwarded = arguments;
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            forwarded = [Assembly.GetEntryAssembly()!.Location, .. arguments];
        return new ProcessCommand { ExecutablePath = executable, WorkingDirectory = AppContext.BaseDirectory, Arguments = forwarded };
    }

    private static ManagedServiceDefinition Definition(string id, ProcessCommand command) => new()
    {
        Id = id, DisplayName = id, Kind = ServiceKind.Php, Command = command, StartupTimeout = TimeSpan.FromSeconds(8)
    };

    private static ProcessStartInfo ToStartInfo(ProcessCommand command)
    {
        var info = new ProcessStartInfo(command.ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = command.WorkingDirectory
        };
        foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private static async Task RunEndpointAsync(string kind, int port)
    {
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            if (kind == "fastcgi")
            {
                var header = new byte[8];
                await stream.ReadExactlyAsync(header);
                await stream.ReadExactlyAsync(new byte[(header[4] << 8) + header[5] + header[6]]);
                await stream.WriteAsync(new byte[] { 1, 10, 0, 0, 0, 0, 0, 0 });
            }
            else if (kind == "smtp") await stream.WriteAsync("220 process-check SMTP\r\n"u8.ToArray());
            else
            {
                var request = new byte[4096];
                var received = await stream.ReadAsync(request);
                if (received == 0) continue;
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"u8.ToArray());
            }
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task PublishFixtureFileAsync(string path, string content)
    {
        // File existence is the readiness signal. Publish only a complete, closed file;
        // WriteAllTextAsync creates the destination before its final write/close finishes.
        var temporaryPath = path + ".pending." + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content);
            File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static async Task WaitForFileAsync(string path)
    {
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException($"Fixture did not create {path}.");
            await Task.Delay(30);
        }
    }

    private static async Task AssertGoneAsync(int id)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try { using var process = Process.GetProcessById(id); if (process.HasExited) return; }
            catch (ArgumentException) { return; }
            await Task.Delay(30);
        }
        throw new InvalidOperationException($"Owned process {id} survived cleanup.");
    }

    private static async Task ExpectTimeoutAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (TimeoutException) { return; }
        throw new InvalidOperationException("Expected operation timeout.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record EchoOutput(string[] Arguments, string? Environment);
    private sealed record TreeIds(int Parent, int Child);
}
