using System.Collections.Concurrent;
using PhpDevTray.Core;
using PhpDevTray.Infrastructure.Runtimes;
using PhpDevTray.Infrastructure.Releases;

namespace PhpDevTray.Infrastructure;

public sealed partial class EnvironmentManager
{
    private RuntimeReleaseManager? _releases;
    private readonly ConcurrentDictionary<string, RuntimeRelease> _knownReleases = new(StringComparer.Ordinal);
    private RuntimeReleaseManager Releases => _releases ??= new RuntimeReleaseManager(_dataDirectory);

    public async Task<IReadOnlyList<RuntimeRelease>> GetAvailableReleasesAsync(ServiceKind kind, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var operation = Operations.Begin($"Fetching {kind} releases", cancellation.Cancel, quiet: true);
        try
        {
            var releases = await Releases.GetReleasesAsync(kind, cancellation.Token);
            foreach (var release in releases) _knownReleases[release.Id] = release;
            operation.Complete(); return releases;
        }
        catch (Exception error) { operation.Fail(error); throw; }
    }

    public Task InstallReleaseAsync(string releaseId, IProgress<RuntimeInstallProgress>? progress = null,
        CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        if (_knownReleases.TryGetValue(releaseId, out var known)) EnsureNewInstallation(known.Kind, known.Channel);
        var reporting = new InstallProgressReporter(update =>
        {
            Operations.ReportCurrent(update.Phase, update.TotalBytes is > 0 ? update.BytesReceived / (double)update.TotalBytes.Value : null);
            progress?.Report(update);
        });
        await using var downloaded = await Releases.DownloadAsync(releaseId, reporting, cancellationToken);
        EnsureNewInstallation(downloaded.Kind, null);
        reporting.Report(new("Validating and registering runtime"));
        if (downloaded.Kind == ServiceKind.Php)
        {
            // DownloadAsync has just resolved this exact ID again from trusted publisher metadata.
            var identity = System.Text.RegularExpressions.Regex.Match(releaseId, @"^php:(\d+\.\d+\.\d+)(?:-\d+)?:x64:nts$");
            if (!identity.Success) throw new InvalidDataException("The trusted PHP release identifier has an unsupported patch identity.");
            await ImportAndActivatePhpCoreAsync(downloaded.ExecutablePath, cancellationToken, identity.Groups[1].Value);
            reporting.Report(new("Installed and activated"));
            Log("Information", $"Installed verified release {releaseId}");
            return;
        }
        if (downloaded.Kind is ServiceKind.Caddy or ServiceKind.FrankenPhp)
        {
            await ImportWebRuntimeAsync(downloaded.Kind, downloaded.ExecutablePath, cancellationToken, releaseId.Split(':')[1]);
            reporting.Report(new("Installed"));
            return;
        }
        var executable = await _catalog.ImportMailpitAsync(downloaded.ExecutablePath, cancellationToken);
        await CommitSettingsAsync(_settings with { MailpitExecutable = executable }, cancellationToken);
        reporting.Report(new("Installed"));
    }, cancellationToken);

    private void EnsureNewInstallation(ServiceKind kind, string? channel)
    {
        if (kind == ServiceKind.Mailpit && _settings.MailpitExecutable is not null)
            throw new InvalidOperationException("Mailpit is already installed. A validated update/rollback workflow is not available yet.");
    }

    private sealed class InstallProgressReporter(Action<RuntimeInstallProgress> report) : IProgress<RuntimeInstallProgress>
    { public void Report(RuntimeInstallProgress value) => report(value); }

    public async Task<IReadOnlyList<PhpExtensionInfo>> GetPhpExtensionsAsync(string channel, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operations.WaitAsync(cancellationToken);
        try
        {
            if (channel.StartsWith("franken-", StringComparison.Ordinal))
                return await _catalog.GetPhpExtensionsAsync(RuntimeCatalog.FrankenInstallation(FindFranken(channel)), _settings, cancellationToken);
            var installation = _settings.PhpInstallations.SingleOrDefault(p => p.Channel == channel)
                ?? throw new InvalidOperationException($"PHP {channel} is not installed.");
            return await _catalog.GetPhpExtensionsAsync(installation, _settings, cancellationToken);
        }
        finally { _operations.Release(); }
    }

    public Task SetPhpExtensionEnabledAsync(string channel, string extension, bool enabled,
        CancellationToken cancellationToken = default)
        => ApplyPhpExtensionChangesAsync(channel, [new(extension, enabled)], cancellationToken);

    public Task ApplyPhpExtensionChangesAsync(string channel, IReadOnlyList<PhpExtensionChange> changes,
        CancellationToken cancellationToken = default)
    {
        var requested = changes.ToArray();
        if (requested.Length == 0) return Task.CompletedTask;
        return ExecuteAsync(async () =>
        {
            if (channel.StartsWith("franken-", StringComparison.Ordinal))
            {
                await ApplyFrankenExtensionChangesAsync(channel, requested, cancellationToken);
                return;
            }
            var installation = _settings.PhpInstallations.SingleOrDefault(p => p.Channel == channel)
                ?? throw new InvalidOperationException($"PHP {channel} is not installed.");
            var (candidate, definition) = await _catalog.PreparePhpExtensionCandidateAsync(installation, _settings, requested, cancellationToken);
            if (candidate.EnabledExtensions.SequenceEqual(installation.EnabledExtensions, StringComparer.OrdinalIgnoreCase)) return;
            var proposed = _settings with
            {
                PhpInstallations = _settings.PhpInstallations.Select(p => p.Channel == channel ? candidate : p).ToArray()
            };
            await ActivatePhpConfigurationCoreAsync(candidate, definition, proposed, cancellationToken);
            Log("Information", $"Applied {requested.Length} extension changes to PHP {channel}");
        }, cancellationToken);
    }
}
