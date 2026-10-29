using PhpDevTray.Core;

namespace PhpDevTray.Infrastructure;

public sealed partial class EnvironmentManager
{
    public Task ActivatePhpVersionAsync(string channel, string version, CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        var active = _settings.PhpInstallations.SingleOrDefault(item => item.Channel == channel)
            ?? throw new InvalidOperationException($"PHP {channel} is not installed.");
        if (active.Version == version) throw new InvalidOperationException($"PHP {version} is already active.");
        var retained = _settings.RetainedPhpVersions.SingleOrDefault(item => item.Channel == channel && item.Version == version)
            ?? throw new InvalidOperationException($"PHP {version} is not retained for channel {channel}.");
        _catalog.ValidateRetainedPhpVersion(retained);
        await ApplyPhpInstallationCoreAsync(active with { Version = retained.Version, Directory = retained.Directory }, cancellationToken);
        Log("Information", $"Activated retained PHP {version} in channel {channel}");
    }, cancellationToken);

    private async Task ImportAndActivatePhpCoreAsync(string executable, CancellationToken cancellationToken, string? expectedVersion = null)
    {
        var imported = await _catalog.ImportPhpAsync(executable, _settings, cancellationToken);
        var committed = false;
        var recoveryIncomplete = false;
        try
        {
            if (expectedVersion is not null && imported.Version != expectedVersion)
                throw new InvalidDataException($"The verified archive reported PHP {imported.Version}, but the selected release requires {expectedVersion}.");
            await ApplyPhpInstallationCoreAsync(imported, cancellationToken);
            committed = true;
        }
        catch (AggregateException)
        {
            recoveryIncomplete = true;
            throw;
        }
        finally
        {
            if (!committed)
            {
                // ImportPhpAsync always returns a fresh directory, never an active/retained/pre-existing one.
                // A recovery failure can leave a process using the candidate; keep its files for diagnosis in that case.
                var usingCandidate = _supervisor.Snapshots.Any(snapshot => snapshot.Id == imported.Id &&
                    snapshot.Version == imported.Version && snapshot.Status is ServiceStatus.Running or ServiceStatus.Starting or ServiceStatus.Stopping);
                if (!recoveryIncomplete && !usingCandidate) _catalog.DeleteUnreferencedPhpDirectory(imported.Directory, _settings);
            }
        }
    }

    private async Task ApplyPhpInstallationCoreAsync(PhpInstallation candidateInstallation, CancellationToken cancellationToken)
    {
        var previous = _settings;
        var oldInstallation = previous.PhpInstallations.SingleOrDefault(item => item.Channel == candidateInstallation.Channel);
        if (oldInstallation?.Version == candidateInstallation.Version)
            throw new InvalidOperationException($"PHP {candidateInstallation.Version} is already active. Same-patch rebuild replacement is not supported.");
        var retained = previous.RetainedPhpVersions.Where(item => item.Channel != candidateInstallation.Channel ||
            (item.Version != candidateInstallation.Version && item.Version != oldInstallation?.Version)).ToList();
        if (oldInstallation is not null)
            retained.Add(new PhpRuntimeVersion { Channel = oldInstallation.Channel, Version = oldInstallation.Version, Directory = oldInstallation.Directory });
        var proposed = previous with
        {
            PhpInstallations = [.. previous.PhpInstallations.Where(item => item.Channel != candidateInstallation.Channel), candidateInstallation],
            RetainedPhpVersions = retained,
            DefaultPhpChannel = previous.DefaultPhpChannel ?? candidateInstallation.Channel
        };
        // Validate the complete replacement and configured module set before touching live services or published INI files.
        var candidate = await _catalog.BuildPhpCandidateAsync(candidateInstallation, proposed, cancellationToken);
        var id = candidate.Id;
        var phpWasRunning = _supervisor.IsRunning(id);
        var nginxWasRunning = phpWasRunning && _supervisor.IsRunning("nginx") &&
            previous.Sites.Any(site => site.Enabled && site.PhpChannel == candidateInstallation.Channel);
        _definitions.TryGetValue(id, out var previousPhpDefinition);
        _definitions.TryGetValue("nginx", out var previousNginxDefinition);
        if (phpWasRunning && previousPhpDefinition is null || nginxWasRunning && previousNginxDefinition is null)
            throw new InvalidOperationException("The running service has no recoverable managed definition. Stop it before activating another PHP patch.");

        var sourceDirectory = Path.Combine(_dataDirectory, "user", "php", candidateInstallation.Channel, "conf.d");
        var publishedFiles = new[] { Path.Combine(sourceDirectory, "10-extensions.ini"), Path.Combine(sourceDirectory, "90-managed.ini"),
            Path.Combine(_dataDirectory, "bin", "launcher.json") };
        var previousBytes = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in publishedFiles) previousBytes[path] = File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
        var customPath = Path.Combine(sourceDirectory, "20-custom.ini");
        var validatedCustom = await File.ReadAllBytesAsync(Path.Combine(candidate.Command.EnvironmentVariables["PHP_INI_SCAN_DIR"], "20-custom.ini"), cancellationToken);
        var nginxTransitionStarted = false;
        var phpTransitionStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (phpWasRunning)
            {
                if (nginxWasRunning)
                {
                    nginxTransitionStarted = true;
                    await _supervisor.StopAsync("nginx", TimeSpan.FromSeconds(20), cancellationToken);
                }
                phpTransitionStarted = true;
                await _supervisor.StopAsync(id, TimeSpan.FromSeconds(5), cancellationToken);
                await _supervisor.StartAsync(candidate, cancellationToken);
                _definitions[id] = candidate;
                if (nginxWasRunning) await _supervisor.StartAsync(previousNginxDefinition!, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var currentCustom = await File.ReadAllBytesAsync(customPath, cancellationToken);
            if (!validatedCustom.AsSpan().SequenceEqual(currentCustom))
                throw new InvalidOperationException("The channel's user INI changed during PHP activation. Retry to validate the new contents.");
            // Service health has passed (or the channel was stopped); now publish only manager-owned configuration and state.
            await _catalog.PublishPhpCandidateAsync(candidateInstallation, candidate, proposed, cancellationToken);
            await _catalog.SaveAsync(proposed, cancellationToken);
            _settings = proposed;
            if (!phpWasRunning) _definitions.Remove(id);
        }
        catch (Exception activationError)
        {
            var recoveryErrors = new List<Exception>();
            async Task RecoverAsync(Func<Task> action) { try { await action(); } catch (Exception error) { recoveryErrors.Add(error); } }
            if (nginxTransitionStarted) await RecoverAsync(() => _supervisor.StopAsync("nginx", TimeSpan.FromSeconds(5), CancellationToken.None));
            if (phpTransitionStarted) await RecoverAsync(() => _supervisor.StopAsync(id, TimeSpan.FromSeconds(5), CancellationToken.None));
            foreach (var saved in previousBytes) await RecoverAsync(() => RestorePublishedFileAsync(saved.Key, saved.Value));
            _settings = previous;
            if (previousPhpDefinition is null) _definitions.Remove(id); else _definitions[id] = previousPhpDefinition;
            if (phpTransitionStarted && phpWasRunning)
                await RecoverAsync(() => _supervisor.StartAsync(previousPhpDefinition!, CancellationToken.None));
            if (nginxTransitionStarted && nginxWasRunning)
                await RecoverAsync(() => _supervisor.StartAsync(previousNginxDefinition!, CancellationToken.None));
            if (recoveryErrors.Count > 0)
                throw new AggregateException("PHP activation failed and recovery was incomplete. Inspect service state and the application log.", [activationError, .. recoveryErrors]);
            throw;
        }
    }

    private static async Task RestorePublishedFileAsync(string path, byte[]? bytes)
    {
        if (bytes is null) { if (File.Exists(path)) File.Delete(path); return; }
        if (File.Exists(path))
        {
            var current = await File.ReadAllBytesAsync(path);
            if (bytes.AsSpan().SequenceEqual(current)) return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".recovery-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, CancellationToken.None);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
