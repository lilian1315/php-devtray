using PhpDevTray.Core;
using PhpDevTray.Infrastructure;
using PhpDevTray.Infrastructure.Runtimes;

namespace PhpDevTray.Checks;

internal static class ApplicationChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PhpDevTrayChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var supervisor = new RecordingSupervisor();
            var catalog = new RuntimeCatalog(root, supervisor);
            var initial = await catalog.LoadAsync();
            Check(initial.SmtpPort == 39327 && initial.SmtpPort < 49152,
                "Default SMTP must use the uncommon port 39327, outside Windows' default dynamic/reserved range.");
            Check(initial.PhpInstallations.Count == 0, "New data directory must contain no invented runtimes.");
            Check(!initial.PathIntegrationEnabled, "PATH integration must be opt-in.");
            var saved = initial with { LastSection = "Logs" };
            await catalog.SaveAsync(saved);
            var loaded = await catalog.LoadAsync();
            Check(loaded.LastSection == "Logs", "Settings must survive reload.");
            Console.WriteLine("PASS: settings roundtrip preserves navigation without enabling PATH.");

            await using var manager = new EnvironmentManager(root, supervisor, catalog);
            await manager.InitializeAsync();
            Check(manager.Snapshot.Services.All(s => s.Status == ServiceStatus.NotInstalled), "Empty profile must show not installed, not running.");
            Check(supervisor.Started.Count == 0, "Initialization must not start unrequested services.");
            var executable = Path.Combine(root, "frankenphp.exe");
            File.Copy(Environment.ProcessPath!, executable);
            await File.WriteAllTextAsync(Path.Combine(root, "php.ini-development"), "; fixture");
            var stack = new CaddyStack(executable,
                [new("test", executable, 9085, "8.5.13", "1.13.6")]) { Sites = [new("app.localhost", root, "franken-test")] };
            await PhpDevTray.Infrastructure.Storage.AtomicJsonStore.WriteAsync(Path.Combine(root, "settings.json"), (await catalog.LoadAsync()) with { Web = stack.Configuration });
            await manager.RefreshAsync();
            await manager.StartAllAsync();
            Check(supervisor.Started.SequenceEqual(["franken-test", "caddy"]), "Start All must start the backend before Caddy.");
            var configs = Path.Combine(root, "data", "state", "caddy");
            var edge = await File.ReadAllTextAsync(Path.Combine(configs, "caddy", "Caddyfile"));
            var backend = await File.ReadAllTextAsync(Path.Combine(configs, "franken-test", "Caddyfile"));
            Check(edge.Contains("tls internal") && !edge.Contains("skip_install_trust"), "Caddy must use local CA trust installation.");
            Check(backend.Contains("auto_https off") && backend.Contains("skip_install_trust"), "Only the HTTP backend must skip trust installation.");
            Console.WriteLine("PASS: Caddy is the default proxy and only FrankenPHP skips trust installation.");
            await manager.SetThemeAsync(ThemePreference.Dark);
            Check(manager.Snapshot.Theme == ThemePreference.Dark && (await catalog.LoadAsync()).Theme == ThemePreference.Dark,
                "Appearance must be applied in snapshots and persist without touching runtime/CLI state.");
            await manager.StopAllAsync();
            Check(supervisor.StopAllCalls == 1, "Stop All must delegate the bounded, dependency-ordered supervisor shutdown.");
            Check(manager.Notifications.History.Count == 0, "Routine initialization, service and appearance changes must not produce success notifications.");
            var failed = false;
            try { await manager.SetDefaultPhpAsync("9.9"); }
            catch (InvalidOperationException) { failed = true; }
            Check(failed && manager.Snapshot.LastError is not null, "Invalid default must fail with actionable state.");
            Check(manager.Notifications.History.Count == 1 && manager.Notifications.History[0].Severity == NotificationSeverity.Error,
                "Routine operations must still notify on failure.");
            Console.WriteLine("PASS: coordinator respects explicit startup, ordered shutdown, and invalid-default errors.");
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Unsafe site input was accepted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RecordingSupervisor : IProcessSupervisor
    {
        public event EventHandler<ServiceSnapshot>? ServiceChanged { add { } remove { } }
        public IReadOnlyList<ServiceSnapshot> Snapshots => [];
        public List<string> Started { get; } = [];
        public List<string> Stopped { get; } = [];
        public int StopAllCalls { get; private set; }
        public bool IsRunning(string id) => false;
        public Task StartAsync(ManagedServiceDefinition definition, CancellationToken cancellationToken = default) { Started.Add(definition.Id); return Task.CompletedTask; }
        public Task StopAsync(string id, TimeSpan timeout, CancellationToken cancellationToken = default) { Stopped.Add(id); return Task.CompletedTask; }
        public Task StopAllAsync(TimeSpan timeout, CancellationToken cancellationToken = default) { StopAllCalls++; return Task.CompletedTask; }
        public Task<ProcessResult> RunAsync(ProcessCommand command, TimeSpan timeout, CancellationToken cancellationToken = default)
            => command.Arguments.Contains("-m") ? Task.FromResult(new ProcessResult(0, "[PHP Modules]\nCore\n[Zend Modules]\n", ""))
                : command.Arguments.Contains("--ini") || command.Arguments.FirstOrDefault() == "validate" ? Task.FromResult(new ProcessResult(0, "", ""))
                : throw new InvalidOperationException("Unexpected command in the recording supervisor.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
