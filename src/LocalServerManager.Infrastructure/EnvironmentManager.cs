using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalServerManager.Core;
using LocalServerManager.Infrastructure.Processes;
using LocalServerManager.Infrastructure.Runtimes;

namespace LocalServerManager.Infrastructure;

public static class Bootstrapper
{
    public static IEnvironmentManager CreateDefault() => Create(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalServerManager"));

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
    private AppSettings _settings = new();
    private EnvironmentSnapshot _snapshot;
    private bool _initialized;
    private bool _disposed;
    private bool _busy;
    private string? _lastError;

    public EnvironmentManager(string dataDirectory, IProcessSupervisor supervisor, RuntimeCatalog catalog)
    {
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _supervisor = supervisor;
        _catalog = catalog;
        _snapshot = new EnvironmentSnapshot { DataDirectory = _dataDirectory };
        _supervisor.ServiceChanged += OnServiceChanged;
    }

    public event EventHandler? Changed;
    public EnvironmentSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public Task InitializeAsync(CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        if (_initialized) return;
        _settings = await _catalog.LoadAsync(cancellationToken);
        foreach (var site in _settings.Sites) await _catalog.EnsureSiteSourceAsync(site, cancellationToken);
        _initialized = true;
        await _catalog.UpdateCliSelectionAsync(_settings, cancellationToken);
        if (_settings.StartServicesOnLaunch) await StartAllCoreAsync(cancellationToken);
        Log("Information", "Application initialized");
    }, cancellationToken);

    public Task RefreshAsync(CancellationToken cancellationToken = default) => ExecuteAsync(
        () => Task.CompletedTask, cancellationToken);

    public Task StartAllAsync(CancellationToken cancellationToken = default) => ExecuteAsync(
        () => StartAllCoreAsync(cancellationToken), cancellationToken);

    private async Task StartAllCoreAsync(CancellationToken cancellationToken)
    {
        if (_settings.MailpitExecutable is not null) await StartCoreAsync("mailpit", cancellationToken);
        foreach (var runtime in _settings.PhpInstallations)
            await StartCoreAsync($"php:{runtime.Channel}", cancellationToken);
        if (_settings.NginxDirectory is not null) await StartCoreAsync("nginx", cancellationToken);
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
        if (id == "nginx")
        {
            foreach (var channel in _settings.Sites.Where(s => s.Enabled).Select(s => s.PhpChannel).Distinct())
                await StartCoreAsync($"php:{channel}", cancellationToken);
            definition = await _catalog.BuildNginxAsync(_settings, cancellationToken);
        }
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
        await _supervisor.StopAsync(id, TimeSpan.FromSeconds(id == "nginx" ? 20 : 5), cancellationToken);
        Log("Information", $"Stopped {id}");
    }, cancellationToken);

    public Task RestartServiceAsync(string id, CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        var dependentNginx = id.StartsWith("php:", StringComparison.Ordinal) && _supervisor.IsRunning("nginx") &&
            _settings.Sites.Any(s => s.Enabled && s.PhpChannel == id[4..]);
        if (dependentNginx) await _supervisor.StopAsync("nginx", TimeSpan.FromSeconds(20), cancellationToken);
        await _supervisor.StopAsync(id, TimeSpan.FromSeconds(5), cancellationToken);
        await StartCoreAsync(id, cancellationToken);
        if (dependentNginx) await StartCoreAsync("nginx", cancellationToken);
    }, cancellationToken);

    public Task RegisterPhpAsync(string phpExecutable, CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        await ImportAndActivatePhpCoreAsync(phpExecutable, cancellationToken);
        Log("Information", "Imported and activated PHP distribution");
    }, cancellationToken);

    public Task RegisterNginxAsync(string nginxExecutable, CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        if (_supervisor.IsRunning("nginx")) throw new InvalidOperationException("Stop Nginx before importing a distribution.");
        var directory = await _catalog.ImportNginxAsync(nginxExecutable, cancellationToken);
        await CommitSettingsAsync(_settings with { NginxDirectory = directory }, cancellationToken);
        Log("Information", "Imported Nginx distribution");
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

    public Task SaveSiteAsync(SiteDefinition site, CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        ValidateSite(site);
        if (!_settings.PhpInstallations.Any(p => p.Channel == site.PhpChannel))
            throw new InvalidOperationException("Select an installed PHP channel for this site.");
        await _catalog.EnsureSiteSourceAsync(site, cancellationToken);
        var proposed = _settings with { Sites = [.. _settings.Sites.Where(s => s.Id != site.Id), site] };
        await ApplySiteSettingsAsync(proposed, cancellationToken);
    }, cancellationToken);

    public Task SetSiteEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        var site = _settings.Sites.FirstOrDefault(s => s.Id == id)
            ?? throw new InvalidOperationException("Site no longer exists.");
        await ApplySiteSettingsAsync(_settings with
        {
            Sites = _settings.Sites.Select(s => s.Id == id ? site with { Enabled = enabled } : s).ToArray()
        }, cancellationToken);
    }, cancellationToken);

    private async Task ApplySiteSettingsAsync(AppSettings proposed, CancellationToken cancellationToken)
    {
        if (_settings.NginxDirectory is null)
        {
            await CommitSettingsAsync(proposed, cancellationToken);
            return;
        }
        var previous = _settings;
        var wasRunning = _supervisor.IsRunning("nginx");
        _definitions.TryGetValue("nginx", out var oldDefinition);
        var bootstrap = Path.Combine(_dataDirectory, "user", "nginx", "generated", "bootstrap.conf");
        var oldBootstrap = File.Exists(bootstrap) ? await File.ReadAllBytesAsync(bootstrap, cancellationToken) : null;
        var startedDependencies = new List<string>();
        var bootstrapPublished = false;
        var nginxTransitionStarted = false;
        try
        {
            var candidate = await _catalog.BuildNginxAsync(proposed, cancellationToken);
            bootstrapPublished = true;
            if (wasRunning)
            {
                foreach (var channel in proposed.Sites.Where(s => s.Enabled).Select(s => s.PhpChannel).Distinct())
                {
                    var id = $"php:{channel}";
                    if (_supervisor.IsRunning(id)) continue;
                    await StartCoreAsync(id, cancellationToken);
                    startedDependencies.Add(id);
                }
                nginxTransitionStarted = true;
                await _supervisor.StopAsync("nginx", TimeSpan.FromSeconds(20), cancellationToken);
                await _supervisor.StartAsync(candidate, cancellationToken);
                _definitions["nginx"] = candidate;
            }
            await CommitSettingsAsync(proposed, cancellationToken);
        }
        catch (Exception operationError)
        {
            try
            {
                if (nginxTransitionStarted) await _supervisor.StopAsync("nginx", TimeSpan.FromSeconds(5), CancellationToken.None);
                if (bootstrapPublished && oldBootstrap is not null)
                {
                    var temporary = bootstrap + ".rollback";
                    await File.WriteAllBytesAsync(temporary, oldBootstrap, CancellationToken.None);
                    File.Move(temporary, bootstrap, true);
                }
                else if (bootstrapPublished && File.Exists(bootstrap)) File.Delete(bootstrap);
                if (nginxTransitionStarted && oldDefinition is not null)
                {
                    await _supervisor.StartAsync(oldDefinition, CancellationToken.None);
                    _definitions["nginx"] = oldDefinition;
                }
                foreach (var dependency in startedDependencies)
                    await _supervisor.StopAsync(dependency, TimeSpan.FromSeconds(5), CancellationToken.None);
                _settings = previous;
            }
            catch (Exception recoveryError)
            {
                throw new AggregateException("Site activation failed and recovery also failed. Inspect the application log.", operationError, recoveryError);
            }
            throw;
        }
    }

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

    private async Task ExecuteAsync(Func<Task> action, CancellationToken cancellationToken, bool clearError = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operations.WaitAsync(cancellationToken);
        try
        {
            _busy = true;
            if (clearError) _lastError = null;
            Publish();
            await action();
        }
        catch (Exception error)
        {
            _lastError = error.Message;
            Log("Error", error.ToString());
            throw;
        }
        finally
        {
            _busy = false;
            Publish();
            _operations.Release();
        }
    }

    private void OnServiceChanged(object? sender, ServiceSnapshot snapshot) => Publish();

    private void Publish()
    {
        var observed = _supervisor.Snapshots.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var services = new List<ServiceSnapshot>
        {
            GetService("nginx", "Nginx", ServiceKind.Nginx, _settings.NginxDirectory is not null),
            GetService("mailpit", "Mail Trap", ServiceKind.Mailpit, _settings.MailpitExecutable is not null)
        };
        foreach (var php in _settings.PhpInstallations)
        {
            services.Add(GetService($"php:{php.Channel}", $"PHP {php.Channel}", ServiceKind.Php, true) with
            {
                Version = php.Version,
                Endpoint = $"127.0.0.1:{php.Port}",
                IsDefault = php.Channel == _settings.DefaultPhpChannel,
                SiteCount = _settings.Sites.Count(s => s.PhpChannel == php.Channel)
            });
        }
        Volatile.Write(ref _snapshot, new EnvironmentSnapshot
        {
            Theme = _settings.Theme,
            DataDirectory = _dataDirectory,
            Services = services,
            PhpInstallations = _settings.PhpInstallations,
            RetainedPhpVersions = _settings.RetainedPhpVersions,
            Sites = _settings.Sites,
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

    public static void ValidateSite(SiteDefinition site)
    {
        if (!Regex.IsMatch(site.Id, "^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Site ID must contain 1–64 letters, numbers, underscores, or hyphens.");
        if (!Regex.IsMatch(site.Hostname, "^[a-zA-Z0-9](?:[a-zA-Z0-9.-]{0,251}[a-zA-Z0-9])?$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Enter a hostname such as app.test without a scheme or path.");
        if (site.Port is < 1 or > 65535) throw new ArgumentException("Site port must be between 1 and 65535.");
        if (!Path.IsPathFullyQualified(site.DocumentRoot) || !Directory.Exists(site.DocumentRoot))
            throw new ArgumentException("Select an existing absolute document-root directory.");
        if (site.DocumentRoot.IndexOfAny(['\r', '\n', '\0', '"', '$']) >= 0)
            throw new ArgumentException("Document root contains characters that cannot be represented safely in Nginx configuration.");
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
        await _operations.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            try { await StopAllCoreAsync(CancellationToken.None); }
            finally
            {
                _supervisor.ServiceChanged -= OnServiceChanged;
                await _supervisor.DisposeAsync();
            }
        }
        finally { _operations.Release(); }
    }
}
