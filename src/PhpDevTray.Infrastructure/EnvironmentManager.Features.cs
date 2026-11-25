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
        var releases = await Releases.GetReleasesAsync(kind, cancellationToken);
        foreach (var release in releases) _knownReleases[release.Id] = release;
        return releases;
    }

    public Task InstallReleaseAsync(string releaseId, IProgress<RuntimeInstallProgress>? progress = null,
        CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        if (_knownReleases.TryGetValue(releaseId, out var known)) EnsureNewInstallation(known.Kind, known.Channel);
        await using var downloaded = await Releases.DownloadAsync(releaseId, progress, cancellationToken);
        EnsureNewInstallation(downloaded.Kind, null);
        progress?.Report(new("Validating and registering runtime"));
        if (downloaded.Kind == ServiceKind.Php)
        {
            // DownloadAsync has just resolved this exact ID again from trusted publisher metadata.
            var identity = System.Text.RegularExpressions.Regex.Match(releaseId, @"^php:(\d+\.\d+\.\d+)(?:-\d+)?:x64:nts$");
            if (!identity.Success) throw new InvalidDataException("The trusted PHP release identifier has an unsupported patch identity.");
            await ImportAndActivatePhpCoreAsync(downloaded.ExecutablePath, cancellationToken, identity.Groups[1].Value);
            progress?.Report(new("Installed and activated"));
            Log("Information", $"Installed verified release {releaseId}");
            return;
        }
        if (downloaded.Kind is ServiceKind.Caddy or ServiceKind.FrankenPhp)
        {
            await ImportWebRuntimeAsync(downloaded.Kind, downloaded.ExecutablePath, cancellationToken, releaseId.Split(':')[1]);
            progress?.Report(new("Installed"));
            return;
        }
        var executable = await _catalog.ImportMailpitAsync(downloaded.ExecutablePath, cancellationToken);
        await CommitSettingsAsync(_settings with { MailpitExecutable = executable }, cancellationToken);
        progress?.Report(new("Installed"));
    }, cancellationToken);

    private void EnsureNewInstallation(ServiceKind kind, string? channel)
    {
        if (kind == ServiceKind.Mailpit && _settings.MailpitExecutable is not null)
            throw new InvalidOperationException("Mailpit is already installed. A validated update/rollback workflow is not available yet.");
    }

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
        CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        if (channel.StartsWith("franken-", StringComparison.Ordinal))
        {
            await SetFrankenExtensionAsync(channel, extension, enabled, cancellationToken);
            return;
        }
        var installation = _settings.PhpInstallations.SingleOrDefault(p => p.Channel == channel)
            ?? throw new InvalidOperationException($"PHP {channel} is not installed.");
        var proposedInstallation = await _catalog.PrepareExtensionChangeAsync(installation, _settings, extension, enabled, cancellationToken);
        if (proposedInstallation.EnabledExtensions.SequenceEqual(installation.EnabledExtensions, StringComparer.OrdinalIgnoreCase)) return;
        var previous = _settings;
        var proposed = _settings with
        {
            PhpInstallations = _settings.PhpInstallations.Select(p => p.Channel == channel ? proposedInstallation : p).ToArray()
        };
        var id = $"php:{channel}";
        var phpWasRunning = _supervisor.IsRunning(id);
        _definitions.TryGetValue(id, out var previousPhpDefinition);
        var transitionStarted = false;
        try
        {
            var candidate = await _catalog.BuildPhpAsync(proposedInstallation, proposed, cancellationToken);
            if (phpWasRunning)
            {
                transitionStarted = true;
                await _supervisor.StopAsync(id, TimeSpan.FromSeconds(5), cancellationToken);
                await _supervisor.StartAsync(candidate, cancellationToken);
                _definitions[id] = candidate;
            }
            await CommitSettingsAsync(proposed, cancellationToken);
            Log("Information", $"{(enabled ? "Enabled" : "Disabled")} PHP {channel} extension {extension}");
        }
        catch (Exception changeError)
        {
            try
            {
                if (transitionStarted)
                {
                    await _supervisor.StopAsync(id, TimeSpan.FromSeconds(5), CancellationToken.None);
                }
                var recovered = await _catalog.BuildPhpAsync(installation, previous, CancellationToken.None);
                if (transitionStarted)
                {
                    await _supervisor.StartAsync(previousPhpDefinition ?? recovered, CancellationToken.None);
                    _definitions[id] = previousPhpDefinition ?? recovered;
                }
                _settings = previous;
            }
            catch (Exception recoveryError)
            {
                throw new AggregateException("Extension activation failed and recovery also failed. Inspect the application log.", changeError, recoveryError);
            }
            throw;
        }
    }, cancellationToken);
}
