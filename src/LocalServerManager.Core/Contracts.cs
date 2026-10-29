namespace LocalServerManager.Core;

public interface IProcessSupervisor : IAsyncDisposable
{
    event EventHandler<ServiceSnapshot>? ServiceChanged;
    IReadOnlyList<ServiceSnapshot> Snapshots { get; }
    bool IsRunning(string id);
    Task StartAsync(ManagedServiceDefinition definition, CancellationToken cancellationToken = default);
    Task StopAsync(string id, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task StopAllAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<ProcessResult> RunAsync(ProcessCommand command, TimeSpan timeout, CancellationToken cancellationToken = default);
}

public interface IEnvironmentManager : IAsyncDisposable
{
    event EventHandler? Changed;
    EnvironmentSnapshot Snapshot { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task RefreshAsync(CancellationToken cancellationToken = default);
    Task StartAllAsync(CancellationToken cancellationToken = default);
    Task StopAllAsync(CancellationToken cancellationToken = default);
    Task StartServiceAsync(string id, CancellationToken cancellationToken = default);
    Task StopServiceAsync(string id, CancellationToken cancellationToken = default);
    Task RestartServiceAsync(string id, CancellationToken cancellationToken = default);
    Task RegisterPhpAsync(string phpExecutable, CancellationToken cancellationToken = default);
    Task ActivatePhpVersionAsync(string channel, string version, CancellationToken cancellationToken = default);
    Task RegisterNginxAsync(string nginxExecutable, CancellationToken cancellationToken = default);
    Task RegisterMailpitAsync(string mailpitExecutable, CancellationToken cancellationToken = default);
    Task SetDefaultPhpAsync(string channel, CancellationToken cancellationToken = default);
    Task SetPathIntegrationAsync(bool enabled, CancellationToken cancellationToken = default);
    Task SaveSiteAsync(SiteDefinition site, CancellationToken cancellationToken = default);
    Task SetSiteEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default);
    Task SetLastSectionAsync(string section, CancellationToken cancellationToken = default);
    Task SetThemeAsync(ThemePreference theme, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MailMessageSummary>> GetMessagesAsync(CancellationToken cancellationToken = default);
    Task<string> GetMessageTextAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetLogLinesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RuntimeRelease>> GetAvailableReleasesAsync(ServiceKind kind, CancellationToken cancellationToken = default);
    Task InstallReleaseAsync(string releaseId, IProgress<RuntimeInstallProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PhpExtensionInfo>> GetPhpExtensionsAsync(string channel, CancellationToken cancellationToken = default);
    Task SetPhpExtensionEnabledAsync(string channel, string extension, bool enabled, CancellationToken cancellationToken = default);
}

public sealed record MailMessageSummary(string Id, string Subject, string From, string To, DateTimeOffset Created);
