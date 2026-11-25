using PhpDevTray.Core;
using PhpDevTray.Infrastructure.Runtimes;

namespace PhpDevTray.Infrastructure;

public sealed partial class EnvironmentManager
{
    private FrankenRuntime FindFranken(string id) => _caddy?.Runtimes.SingleOrDefault(r => "franken-" + r.Id == id)
        ?? throw new InvalidOperationException($"FrankenPHP {id} is not installed.");

    private async Task SetFrankenExtensionAsync(string id, string extension, bool enabled, CancellationToken token)
    {
        var runtime = FindFranken(id);
        var installation = await _catalog.PrepareExtensionChangeAsync(RuntimeCatalog.FrankenInstallation(runtime), _settings, extension, enabled, token);
        if (installation.EnabledExtensions.SequenceEqual(runtime.EnabledExtensions)) return;
        var previous = _caddy!;
        var proposed = previous with { Runtimes = previous.Runtimes.Select(r => r.Id == runtime.Id
            ? r with { EnabledExtensions = installation.EnabledExtensions } : r).ToArray() };
        var wasRunning = _supervisor.IsRunning(id);
        var proxyRunning = _supervisor.IsRunning("caddy");
        var candidate = (await proposed.PrepareAsync(_dataDirectory, _supervisor, _settings, token, id)).Single();
        var old = wasRunning ? (await previous.PrepareAsync(_dataDirectory, _supervisor, _settings, token, id)).Single() : null;
        var proxy = proxyRunning ? (await previous.PrepareAsync(_dataDirectory, _supervisor, _settings, token)).Single(s => s.Id == "caddy") : null;
        var transition = false;
        try
        {
            if (wasRunning)
            {
                transition = true;
                if (proxy is not null) await _supervisor.StopAsync("caddy", TimeSpan.FromSeconds(20), token);
                await _supervisor.StopAsync(id, TimeSpan.FromSeconds(5), token);
                await _supervisor.StartAsync(candidate, token);
                if (proxy is not null) await _supervisor.StartAsync(proxy, token);
            }
            await SaveWebStackAsync(proposed, token);
        }
        catch (Exception failure)
        {
            try
            {
                if (transition)
                {
                    if (proxy is not null) await _supervisor.StopAsync("caddy", TimeSpan.FromSeconds(5), CancellationToken.None);
                    await _supervisor.StopAsync(id, TimeSpan.FromSeconds(5), CancellationToken.None);
                    await _supervisor.StartAsync(old!, CancellationToken.None);
                    if (proxy is not null) await _supervisor.StartAsync(proxy, CancellationToken.None);
                }
            }
            catch (Exception recovery) { throw new AggregateException("Extension change and recovery failed.", failure, recovery); }
            throw;
        }
    }
}
