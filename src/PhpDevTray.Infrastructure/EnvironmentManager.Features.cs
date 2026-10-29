using System.Collections.Concurrent;
using PhpDevTray.Core;
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
        string? newlyCreatedDirectory = null;
        var committed = false;
        try
        {
            AppSettings proposed;
            switch (downloaded.Kind)
            {
                case ServiceKind.Nginx:
                    newlyCreatedDirectory = await _catalog.ImportNginxAsync(downloaded.ExecutablePath, cancellationToken);
                    proposed = _settings with { NginxDirectory = newlyCreatedDirectory };
                    break;
                case ServiceKind.Mailpit:
                    proposed = _settings with { MailpitExecutable = await _catalog.ImportMailpitAsync(downloaded.ExecutablePath, cancellationToken) };
                    break;
                default:
                    throw new InvalidOperationException("Unsupported runtime kind.");
            }
            await CommitSettingsAsync(proposed, cancellationToken);
            committed = true;
            progress?.Report(new("Installed"));
            Log("Information", $"Installed verified release {releaseId}");
        }
        finally
        {
            if (!committed && newlyCreatedDirectory is not null)
            {
                var managedRoot = Path.GetFullPath(Path.Combine(_dataDirectory, "runtimes")) + Path.DirectorySeparatorChar;
                var candidate = Path.GetFullPath(newlyCreatedDirectory);
                if (!candidate.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Refusing to clean an installation outside managed runtime storage.");
                if (Directory.Exists(candidate)) Directory.Delete(candidate, true);
            }
        }
    }, cancellationToken);

    private void EnsureNewInstallation(ServiceKind kind, string? channel)
    {
        if (kind == ServiceKind.Nginx && _settings.NginxDirectory is not null)
            throw new InvalidOperationException("Nginx is already installed. A validated update/rollback workflow is not available yet.");
        if (kind == ServiceKind.Mailpit && _settings.MailpitExecutable is not null)
            throw new InvalidOperationException("Mailpit is already installed. A validated update/rollback workflow is not available yet.");
    }

    public async Task<IReadOnlyList<PhpExtensionInfo>> GetPhpExtensionsAsync(string channel, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operations.WaitAsync(cancellationToken);
        try
        {
            var installation = _settings.PhpInstallations.SingleOrDefault(p => p.Channel == channel)
                ?? throw new InvalidOperationException($"PHP {channel} is not installed.");
            return await _catalog.GetPhpExtensionsAsync(installation, _settings, cancellationToken);
        }
        finally { _operations.Release(); }
    }

    public Task SetPhpExtensionEnabledAsync(string channel, string extension, bool enabled,
        CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
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
        var nginxWasRunning = _supervisor.IsRunning("nginx") && _settings.Sites.Any(s => s.Enabled && s.PhpChannel == channel);
        _definitions.TryGetValue(id, out var previousPhpDefinition);
        _definitions.TryGetValue("nginx", out var previousNginxDefinition);
        var transitionStarted = false;
        try
        {
            var candidate = await _catalog.BuildPhpAsync(proposedInstallation, proposed, cancellationToken);
            if (phpWasRunning)
            {
                transitionStarted = true;
                if (nginxWasRunning) await _supervisor.StopAsync("nginx", TimeSpan.FromSeconds(20), cancellationToken);
                await _supervisor.StopAsync(id, TimeSpan.FromSeconds(5), cancellationToken);
                await _supervisor.StartAsync(candidate, cancellationToken);
                _definitions[id] = candidate;
                if (nginxWasRunning && previousNginxDefinition is not null)
                    await _supervisor.StartAsync(previousNginxDefinition, cancellationToken);
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
                    if (nginxWasRunning) await _supervisor.StopAsync("nginx", TimeSpan.FromSeconds(5), CancellationToken.None);
                    await _supervisor.StopAsync(id, TimeSpan.FromSeconds(5), CancellationToken.None);
                }
                var recovered = await _catalog.BuildPhpAsync(installation, previous, CancellationToken.None);
                if (transitionStarted)
                {
                    await _supervisor.StartAsync(previousPhpDefinition ?? recovered, CancellationToken.None);
                    _definitions[id] = previousPhpDefinition ?? recovered;
                    if (nginxWasRunning && previousNginxDefinition is not null)
                        await _supervisor.StartAsync(previousNginxDefinition, CancellationToken.None);
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
