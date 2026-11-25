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
            var validSite = new SiteDefinition { Id = "app", Hostname = "app.test", DocumentRoot = root, PhpChannel = "8.4" };
            EnvironmentManager.ValidateSite(validSite);
            Reject(() => EnvironmentManager.ValidateSite(validSite with { Id = "../escape" }));
            Reject(() => EnvironmentManager.ValidateSite(validSite with { Hostname = "app.test; include evil;" }));
            Reject(() => EnvironmentManager.ValidateSite(validSite with { Port = 65536 }));
            Reject(() => EnvironmentManager.ValidateSite(validSite with { DocumentRoot = "relative/path" }));
            Console.WriteLine("PASS: site input validation rejects traversal and Nginx directive injection.");

            await using var supervisor = new RecordingSupervisor();
            var catalog = new RuntimeCatalog(root, supervisor);
            var initial = await catalog.LoadAsync();
            Check(initial.SmtpPort == 39327 && initial.SmtpPort < 49152,
                "Default SMTP must use the uncommon port 39327, outside Windows' default dynamic/reserved range.");
            Check(initial.PhpInstallations.Count == 0, "New data directory must contain no invented runtimes.");
            Check(!initial.PathIntegrationEnabled, "PATH integration must be opt-in.");
            var saved = initial with { LastSection = "Logs", Sites = [validSite] };
            await catalog.SaveAsync(saved);
            var loaded = await catalog.LoadAsync();
            Check(loaded.LastSection == "Logs" && loaded.Sites.Count == 1, "Settings must survive reload.");
            Console.WriteLine("PASS: settings roundtrip preserves sites and navigation without enabling PATH.");

            await using var manager = new EnvironmentManager(root, supervisor, catalog);
            await manager.InitializeAsync();
            Check(manager.Snapshot.Services.All(s => s.Status == ServiceStatus.NotInstalled), "Empty profile must show not installed, not running.");
            Check(supervisor.Started.Count == 0, "Initialization must not start unrequested services.");
            await manager.SetThemeAsync(ThemePreference.Dark);
            Check(manager.Snapshot.Theme == ThemePreference.Dark && (await catalog.LoadAsync()).Theme == ThemePreference.Dark,
                "Appearance must be applied in snapshots and persist without touching runtime/CLI state.");
            await manager.StopAllAsync();
            Check(supervisor.StopAllCalls == 1, "Stop All must delegate the bounded, dependency-ordered supervisor shutdown.");
            var failed = false;
            try { await manager.SetDefaultPhpAsync("9.9"); }
            catch (InvalidOperationException) { failed = true; }
            Check(failed && manager.Snapshot.LastError is not null, "Invalid default must fail with actionable state.");
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
            => throw new InvalidOperationException("This check must not execute a real runtime or touch user PATH.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
