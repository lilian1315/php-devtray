using PhpDevTray.Core;
using PhpDevTray.Infrastructure.Runtimes;

namespace PhpDevTray.Infrastructure;

public sealed partial class EnvironmentManager
{
    private FrankenRuntime FindFranken(string id) => _caddy?.Runtimes.SingleOrDefault(r => "franken-" + r.Id == id)
        ?? throw new InvalidOperationException($"FrankenPHP {id} is not installed.");

    private async Task ApplyFrankenExtensionChangesAsync(string id, IReadOnlyList<PhpExtensionChange> changes, CancellationToken token)
    {
        var runtime = FindFranken(id);
        var (installation, environment) = await _catalog.PrepareFrankenExtensionCandidateAsync(RuntimeCatalog.FrankenInstallation(runtime), _settings, changes, token);
        if (installation.EnabledExtensions.SequenceEqual(runtime.EnabledExtensions)) return;
        var stack = _caddy!;
        var proposed = stack with { Runtimes = stack.Runtimes.Select(r => r.Id == runtime.Id
            ? r with { EnabledExtensions = installation.EnabledExtensions } : r).ToArray() };
        if (!_supervisor.IsRunning(id))
        {
            await SaveWebStackAsync(proposed, token);
            return;
        }
        if (!_definitions.TryGetValue(id, out var previous))
            throw new InvalidOperationException("The running FrankenPHP instance has no recoverable definition. Stop it before applying extension changes.");

        // Routing and the backend port stay the same. Replace only the backend's PHP configuration.
        var candidate = previous with { Command = previous.Command with { EnvironmentVariables = environment } };
        var validation = await _supervisor.RunAsync(candidate.Command with
        {
            Arguments = ["validate", "--config", candidate.Command.Arguments.Last()]
        }, TimeSpan.FromSeconds(30), token);
        if (validation.ExitCode != 0)
            throw new InvalidDataException($"{runtime.Label} configuration failed: {validation.StandardError}");
        try
        {
            await _supervisor.StopAsync(id, TimeSpan.FromSeconds(5), token);
            await StartWebDefinitionAsync(candidate, token);
            await SaveWebStackAsync(proposed, token);
        }
        catch (Exception failure)
        {
            try
            {
                await _supervisor.StopAsync(id, TimeSpan.FromSeconds(5), CancellationToken.None);
                await StartWebDefinitionAsync(previous, CancellationToken.None);
            }
            catch (Exception recovery) { throw new AggregateException("Extension change and recovery failed.", failure, recovery); }
            throw;
        }
        Log("Information", $"Applied {changes.Count} extension changes to {runtime.Label}");
    }
}
