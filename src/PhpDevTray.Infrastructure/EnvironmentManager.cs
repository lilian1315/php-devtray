using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using PhpDevTray.Core;
using PhpDevTray.Infrastructure.Processes;
using PhpDevTray.Infrastructure.Configuration;
using PhpDevTray.Infrastructure.Runtimes;

namespace PhpDevTray.Infrastructure;

public static class Bootstrapper
{
    public static IEnvironmentManager CreateDefault() => Create(AppDeployment.DefaultDataDirectory);

    public static IEnvironmentManager Create(string dataDirectory, IProcessSupervisor? supervisor = null)
    {
        supervisor ??= new WindowsProcessSupervisor();
        return new EnvironmentManager(dataDirectory, supervisor, new RuntimeCatalog(dataDirectory, supervisor));
    }
}

public sealed partial class EnvironmentManager : IEnvironmentManager
{
    private readonly string _dataDirectory;
    private readonly IProcessSupervisor _supervisor;
    private readonly RuntimeCatalog _catalog;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly Dictionary<string, ManagedServiceDefinition> _definitions = new(StringComparer.Ordinal);
    private SiteConfigurationWatcher? _siteWatcher;
    private CaddyStack? _caddy;
    private IReadOnlyDictionary<string, string?> _siteRoots = new Dictionary<string, string?>();
    private AppSettings _settings = new();
    private EnvironmentSnapshot _snapshot;
    private bool _initialized;
    private bool _disposed;
    private bool _busy;
    private string? _lastError;
    private readonly ActivityLogReader _activityReader;
    public OperationTracker Operations { get; } = new();
    public NotificationCenter Notifications { get; } = new();
    public ActivityBuffer Activity { get; } = new();

    public EnvironmentManager(string dataDirectory, IProcessSupervisor supervisor, RuntimeCatalog catalog)
    {
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _supervisor = supervisor;
        _catalog = catalog;
        _snapshot = new EnvironmentSnapshot { DataDirectory = _dataDirectory };
        _activityReader = new ActivityLogReader(Path.Combine(_dataDirectory, "logs"), Activity);
        Operations.Finished += (_, operation) =>
        {
            if (operation.State == OperationState.Failed) Notifications.Publish("Operation failed", operation.Error ?? operation.Name, NotificationSeverity.Error);
            else if (!operation.Quiet && operation.State == OperationState.Succeeded) Notifications.Publish("Completed", operation.Name, NotificationSeverity.Success);
        };
        _supervisor.ServiceChanged += OnServiceChanged;
    }

    public event EventHandler? Changed;
    public EnvironmentSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public Task InitializeAsync(CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        if (_initialized) return;
        await _activityReader.StartAsync();
        _caddy = await CaddyStack.LoadAsync(_dataDirectory, cancellationToken);
        _settings = await _catalog.LoadAsync(cancellationToken);
        await RefreshSiteRootsAsync(cancellationToken);
        _siteWatcher = new SiteConfigurationWatcher(Path.Combine(_dataDirectory, "user", "caddy", "sites"),
            RefreshChangedSitesAsync, error => Log("Error", "Site configuration refresh failed. " + error.Message));
        _initialized = true;
        await _catalog.UpdateCliSelectionAsync(_settings, cancellationToken);
        if (_settings.StartServicesOnLaunch) await StartAllCoreAsync(cancellationToken);
        Log("Information", "Application initialized");
    }, cancellationToken);

    public Task RefreshAsync(CancellationToken cancellationToken = default) => ExecuteAsync(
        async () =>
        {
            if (!_supervisor.Snapshots.Any(s => s.Status == ServiceStatus.Running))
            {
                _caddy = await CaddyStack.LoadAsync(_dataDirectory, cancellationToken);
                _settings = await _catalog.LoadAsync(cancellationToken);
            }
            await RefreshSiteRootsAsync(cancellationToken);
        }, cancellationToken);

    public Task StartAllAsync(CancellationToken cancellationToken = default) => ExecuteAsync(
        () => StartAllCoreAsync(cancellationToken), cancellationToken);

    private async Task StartAllCoreAsync(CancellationToken cancellationToken)
    {
        if (_settings.MailpitExecutable is not null) await StartCoreAsync("mailpit", cancellationToken);
        if (_caddy is not null && _caddy.Sites.Any(site => site.Enabled)) await StartCoreAsync("caddy", cancellationToken);
    }

    public Task StopAllAsync(CancellationToken cancellationToken = default) => ExecuteAsync(
        () => StopAllCoreAsync(cancellationToken), cancellationToken);

    private async Task StopAllCoreAsync(CancellationToken cancellationToken)
    {
        await _supervisor.StopAllAsync(TimeSpan.FromSeconds(30), cancellationToken);
    }

    public Task StartServiceAsync(string id, CancellationToken cancellationToken = default) => ExecuteAsync(
        () => StartCoreAsync(id, cancellationToken), cancellationToken);

    private async Task StartCoreAsync(string id, CancellationToken cancellationToken)
    {
        if (_supervisor.IsRunning(id)) return;
        ManagedServiceDefinition definition;
        if (id == "caddy" && _caddy is not null)
        {
            var started = new Stack<string>();
            try
            {
                var prepared = await _caddy.PrepareAsync(_dataDirectory, _supervisor, _settings, cancellationToken);
                foreach (var idNts in _caddy.Sites.Where(s => s.Enabled && s.RuntimeId.StartsWith("php:", StringComparison.Ordinal)).Select(s => s.RuntimeId).Distinct())
                {
                    if (_supervisor.IsRunning(idNts)) continue;
                    await StartCoreAsync(idNts, cancellationToken);
                    started.Push(idNts);
                }
                foreach (var service in prepared)
                {
                    if (_supervisor.IsRunning(service.Id)) continue;
                    await StartWebDefinitionAsync(service, cancellationToken);
                    started.Push(service.Id);
                }
            }
            catch
            {
                foreach (var service in started) await _supervisor.StopAsync(service, TimeSpan.FromSeconds(5), CancellationToken.None);
                throw;
            }
            return;
        }
        if (id.StartsWith("franken-", StringComparison.Ordinal) && _caddy?.Runtimes.Any(r => "franken-" + r.Id == id) == true)
            definition = (await _caddy.PrepareAsync(_dataDirectory, _supervisor, _settings, cancellationToken, id)).Single();
        else if (id == "mailpit") definition = await _catalog.BuildMailpitAsync(_settings, cancellationToken);
        else if (id.StartsWith("php:", StringComparison.Ordinal))
        {
            var runtime = _settings.PhpInstallations.FirstOrDefault(p => p.Channel == id[4..])
                ?? throw new InvalidOperationException($"PHP channel {id[4..]} is not installed.");
            definition = await _catalog.BuildPhpAsync(runtime, _settings, cancellationToken);
        }
        else throw new ArgumentException("Unknown service.", nameof(id));
        _definitions[id] = definition;
        await _supervisor.StartAsync(definition, cancellationToken);
        Log("Information", $"Started {definition.DisplayName}");
    }

    public Task StopServiceAsync(string id, CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        await StopCoreAsync(id, cancellationToken);
        Log("Information", $"Stopped {id}");
    }, cancellationToken);

    public Task RestartServiceAsync(string id, CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        await StopCoreAsync(id, cancellationToken);
        await StartCoreAsync(id, cancellationToken);
    }, cancellationToken);

    private async Task StopCoreAsync(string id, CancellationToken token)
    {
        await _supervisor.StopAsync(id, TimeSpan.FromSeconds(id == "caddy" ? 20 : 5), token);
        if (id == "caddy")
            foreach (var backend in _supervisor.Snapshots.Where(s => s.Kind == ServiceKind.FrankenPhp && _supervisor.IsRunning(s.Id)))
                await _supervisor.StopAsync(backend.Id, TimeSpan.FromSeconds(5), token);
    }

    public Task RegisterPhpAsync(string phpExecutable, CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        await ImportAndActivatePhpCoreAsync(phpExecutable, cancellationToken);
        Log("Information", "Imported and activated PHP distribution");
    }, cancellationToken);

    public Task RegisterMailpitAsync(string mailpitExecutable, CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        if (_supervisor.IsRunning("mailpit")) throw new InvalidOperationException("Stop Mail Trap before importing a distribution.");
        var executable = await _catalog.ImportMailpitAsync(mailpitExecutable, cancellationToken);
        await CommitSettingsAsync(_settings with { MailpitExecutable = executable }, cancellationToken);
        Log("Information", "Imported Mailpit");
    }, cancellationToken);

    public Task SetDefaultPhpAsync(string channel, CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        if (!_settings.PhpInstallations.Any(p => p.Channel == channel))
            throw new InvalidOperationException("The selected PHP channel is not installed.");
        await CommitSettingsAsync(_settings with { DefaultPhpChannel = channel }, cancellationToken);
    }, cancellationToken);

    public Task SetPathIntegrationAsync(bool enabled, CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        if (enabled && _settings.DefaultPhpChannel is null)
            throw new InvalidOperationException("Import PHP and select a default before enabling PATH integration.");
        var previous = _settings;
        await _catalog.UpdateCliSelectionAsync(_settings, cancellationToken);
        await _catalog.SetPathIntegrationAsync(enabled, cancellationToken);
        try { await CommitSettingsAsync(_settings with { PathIntegrationEnabled = enabled }, cancellationToken); }
        catch
        {
            await _catalog.SetPathIntegrationAsync(previous.PathIntegrationEnabled, CancellationToken.None);
            throw;
        }
    }, cancellationToken);

    public Task SetLastSectionAsync(string section, CancellationToken cancellationToken = default) => ExecuteAsync(
        async () =>
        {
            if (section == _settings.LastSection) return;
            var proposed = _settings with { LastSection = section };
            await _catalog.SaveAsync(proposed, cancellationToken);
            _settings = proposed;
        }, cancellationToken, clearError: false);

    public Task SetThemeAsync(ThemePreference theme, CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        if (!Enum.IsDefined(theme)) throw new ArgumentOutOfRangeException(nameof(theme));
        var proposed = _settings with { Theme = theme };
        await _catalog.SaveAsync(proposed, cancellationToken);
        _settings = proposed;
    }, cancellationToken);

    public Task<IReadOnlyList<MailMessageSummary>> GetMessagesAsync(CancellationToken cancellationToken = default)
        => _catalog.GetMessagesAsync(_settings, cancellationToken);

    public Task<string> GetMessageTextAsync(string id, CancellationToken cancellationToken = default)
        => _catalog.GetMessageTextAsync(id, _settings, cancellationToken);

    public Task<IReadOnlyList<string>> GetLogLinesAsync(CancellationToken cancellationToken = default)
        => _catalog.GetLogLinesAsync(cancellationToken);

    private async Task CommitSettingsAsync(AppSettings proposed, CancellationToken cancellationToken)
    {
        var previous = _settings;
        var cliChanged = proposed.DefaultPhpChannel != previous.DefaultPhpChannel ||
            !ReferenceEquals(proposed.PhpInstallations, previous.PhpInstallations) || proposed.SmtpPort != previous.SmtpPort;
        if (cliChanged) await _catalog.UpdateCliSelectionAsync(proposed, cancellationToken);
        try { await _catalog.SaveAsync(proposed, cancellationToken); }
        catch
        {
            if (cliChanged) await _catalog.UpdateCliSelectionAsync(previous, CancellationToken.None);
            throw;
        }
        _settings = proposed;
    }

    private async Task ExecuteAsync(Func<Task> action, CancellationToken cancellationToken, bool clearError = true,
        [System.Runtime.CompilerServices.CallerMemberName] string operationName = "")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var operation = Operations.Begin(OperationName(operationName), quiet: operationName is not
            (nameof(InstallReleaseAsync) or nameof(RegisterPhpAsync) or nameof(RegisterMailpitAsync) or nameof(RegisterWebRuntimeAsync)
            or nameof(ActivatePhpVersionAsync) or nameof(ApplyPhpExtensionChangesAsync)));
        try { await _operations.WaitAsync(cancellationToken); }
        catch (Exception error) { operation.Fail(error); throw; }
        try
        {
            _busy = true;
            if (clearError) _lastError = null;
            Publish();
            await action();
            operation.Complete();
        }
        catch (Exception error)
        {
            _lastError = error.Message;
            Log("Error", error.ToString());
            operation.Fail(error);
            throw;
        }
        finally
        {
            _busy = false;
            Publish();
            _operations.Release();
        }
    }

    private static string OperationName(string method) => Regex.Replace(method.Replace("Async", ""), "(?<!^)([A-Z])", " $1");

    private async Task RefreshSiteRootsAsync(CancellationToken token)
    {
        _siteRoots = _caddy is null ? new Dictionary<string, string?>()
            : await _caddy.ReadSiteRootsAsync(_dataDirectory, _supervisor, _settings, token);
    }

    private async Task RefreshChangedSitesAsync(IReadOnlySet<string> files, CancellationToken token)
    {
        await _operations.WaitAsync(token);
        try
        {
            if (_disposed || _caddy is null) return;
            var sites = _caddy.Sites.Where(site => files.Contains("*") ||
                files.Contains(site.CaddyfileName)).ToArray();
            if (sites.Length == 0) return;
            var values = await (_caddy with { Sites = sites }).ReadSiteRootsAsync(_dataDirectory, _supervisor, _settings, token);
            var updated = new Dictionary<string, string?>(_siteRoots, StringComparer.OrdinalIgnoreCase);
            foreach (var item in values) updated[item.Key] = item.Value;
            _siteRoots = updated;
            Publish();
        }
        finally { _operations.Release(); }
    }

    private void OnServiceChanged(object? sender, ServiceSnapshot snapshot) => Publish();

    private void Publish()
    {
        var observed = _supervisor.Snapshots.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var services = new List<ServiceSnapshot>
        {
            GetService("mailpit", "Mail Trap", ServiceKind.Mailpit, _settings.MailpitExecutable is not null)
        };
        if (_caddy is not null)
        {
            services.Add(GetService("caddy", "Caddy", ServiceKind.Caddy, File.Exists(_caddy.Executable)));
            foreach (var runtime in _caddy.Runtimes)
                services.Add(GetService("franken-" + runtime.Id, runtime.Label, ServiceKind.FrankenPhp, File.Exists(runtime.Executable)) with
                { DisplayName = runtime.Label, Version = runtime.Version, Endpoint = $"127.0.0.1:{runtime.Port}" });
        }
        foreach (var php in _settings.PhpInstallations)
        {
            services.Add(GetService($"php:{php.Channel}", $"PHP NTS {php.Channel}", ServiceKind.Php, true) with
            {
                DisplayName = $"PHP NTS {php.Version}", Version = php.Version,
                Endpoint = $"127.0.0.1:{php.Port}",
                IsDefault = php.Channel == _settings.DefaultPhpChannel,
            });
        }
        Volatile.Write(ref _snapshot, new EnvironmentSnapshot
        {
            Theme = _settings.Theme,
            DataDirectory = _dataDirectory,
            Sites = _caddy?.Sites ?? [],
            SiteRoots = _siteRoots,
            Services = services,
            PhpInstallations = _settings.PhpInstallations,
            RetainedPhpVersions = _settings.RetainedPhpVersions,
            DefaultPhpChannel = _settings.DefaultPhpChannel,
            PathIntegrationEnabled = _settings.PathIntegrationEnabled,
            LastError = _lastError,
            LastSection = _settings.LastSection,
            IsBusy = _busy,
            SmtpPort = _settings.SmtpPort,
            MailpitHttpPort = _settings.MailpitHttpPort
        });
        Changed?.Invoke(this, EventArgs.Empty);
        return;

        ServiceSnapshot GetService(string id, string name, ServiceKind kind, bool installed) =>
            observed.GetValueOrDefault(id) ?? new ServiceSnapshot
            {
                Id = id, DisplayName = name, Kind = kind,
                Status = installed ? ServiceStatus.Stopped : ServiceStatus.NotInstalled,
                Detail = installed ? "Ready to start" : "Import an extracted runtime to get started"
            };
    }

    private void Log(string level, string message)
    {
        try
        {
            var directory = Path.Combine(_dataDirectory, "logs", "application");
            Directory.CreateDirectory(directory);
            var line = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, level, message });
            File.AppendAllText(Path.Combine(directory, $"{DateTime.UtcNow:yyyy-MM-dd}.jsonl"), line + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (_siteWatcher is not null) await _siteWatcher.DisposeAsync();
        await _operations.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            try { await StopAllCoreAsync(CancellationToken.None); }
            finally
            {
                _supervisor.ServiceChanged -= OnServiceChanged;
                try { await _supervisor.DisposeAsync(); }
                finally { await _activityReader.DisposeAsync(); }
            }
        }
        finally { _operations.Release(); }
    }
}
