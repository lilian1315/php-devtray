using System.Text.RegularExpressions;
using PhpDevTray.Core;
using PhpDevTray.Infrastructure.Configuration;
using PhpDevTray.Infrastructure.Runtimes;
using PhpDevTray.Infrastructure.Storage;

namespace PhpDevTray.Infrastructure;

public sealed partial class EnvironmentManager
{
    public Task SaveSiteAsync(WebSite site, CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        var host = RuntimePaths.SafeHostname(site.Hostname.Trim());
        if (!Path.IsPathFullyQualified(site.DocumentRoot)) throw new ArgumentException("Choose an absolute document root.");
        site = site with { Hostname = host, DocumentRoot = Path.GetFullPath(site.DocumentRoot) };
        var stack = _caddy ?? throw new InvalidOperationException("Initialize the environment first.");
        if (!_settings.PhpInstallations.Any(p => p.Id == site.RuntimeId) && !stack.Runtimes.Any(r => "franken-" + r.Id == site.RuntimeId))
            throw new InvalidOperationException("Select an installed runtime.");
        var existing = stack.Sites.FirstOrDefault(s => s.Hostname.Equals(host, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !string.Equals(existing.DocumentRoot, site.DocumentRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The document root is fixed when the site is created.");
        if (existing is null && !Directory.Exists(site.DocumentRoot)) throw new DirectoryNotFoundException(site.DocumentRoot);
        var proposed = stack with { Sites = [.. stack.Sites.Where(s => !s.Hostname.Equals(host, StringComparison.OrdinalIgnoreCase)), site] };
        proposed.Validate();
        await CaddyStack.EnsureSiteFileAsync(_dataDirectory, site, cancellationToken);
        var previous = stack.Sites.FirstOrDefault(s => s.Hostname.Equals(host, StringComparison.OrdinalIgnoreCase));
        var oldId = previous?.RuntimeId ?? site.RuntimeId;
        bool switching = previous is { Enabled: true } && site.Enabled && oldId != site.RuntimeId;
        bool editingStandalone = !_supervisor.IsRunning("caddy") && (IsRunningFranken(oldId) || IsRunningFranken(site.RuntimeId));
        if (switching || editingStandalone)
            await SwitchRunningSiteAsync(stack, proposed, oldId, site.RuntimeId, cancellationToken);
        else
            await SaveWebStackAsync(proposed, cancellationToken);

        await RefreshSiteRootsAsync(cancellationToken);

        bool IsRunningFranken(string id) => id.StartsWith("franken-", StringComparison.Ordinal) && _supervisor.IsRunning(id);
    }, cancellationToken);

    private async Task SwitchRunningSiteAsync(CaddyStack previous, CaddyStack proposed, string oldId, string newId, CancellationToken token)
    {
        var proxyRunning = _supervisor.IsRunning("caddy");
        var affected = new[] { oldId, newId }.Where(id => id.StartsWith("franken-", StringComparison.Ordinal)).ToArray();
        var running = affected.Where(_supervisor.IsRunning).ToHashSet();
        if (!proxyRunning && running.Count == 0)
        {
            await SaveWebStackAsync(proposed, token);
            return;
        }
        var started = new Stack<string>();
        async Task<List<ManagedServiceDefinition>> Prepare(CaddyStack stack, CancellationToken ct)
        {
            if (proxyRunning)
                return (await stack.PrepareAsync(_dataDirectory, _supervisor, _settings, ct))
                    .Where(def => def.Id == "caddy" || affected.Contains(def.Id)).ToList();
            var definitions = new List<ManagedServiceDefinition>();
            foreach (var id in running)
                definitions.AddRange(await stack.PrepareAsync(_dataDirectory, _supervisor, _settings, ct, id));
            return definitions;
        }

        var candidate = await Prepare(proposed, token);
        try
        {
            if (proxyRunning) await _supervisor.StopAsync("caddy", TimeSpan.FromSeconds(20), token);
            foreach (var id in running) await _supervisor.StopAsync(id, TimeSpan.FromSeconds(5), token);
            if (proxyRunning && newId.StartsWith("php:", StringComparison.Ordinal) && !_supervisor.IsRunning(newId))
            {
                await StartCoreAsync(newId, token);
                started.Push(newId);
            }
            foreach (var service in candidate)
            {
                await _supervisor.StartAsync(service, token);
                started.Push(service.Id);
            }
            await SaveWebStackAsync(proposed, token);
        }
        catch (Exception failure)
        {
            try
            {
                foreach (var id in started) await _supervisor.StopAsync(id, TimeSpan.FromSeconds(5), CancellationToken.None);
                var restored = await Prepare(previous, CancellationToken.None);
                foreach (var id in running.Where(id => restored.All(service => service.Id != id)))
                    await StartCoreAsync(id, CancellationToken.None);
                foreach (var service in restored)
                    if (service.Id == "caddy" || running.Contains(service.Id)) await _supervisor.StartAsync(service, CancellationToken.None);
            }
            catch (Exception recovery) { throw new AggregateException("Runtime switch and recovery failed.", failure, recovery); }
            throw;
        }
    }

    private async Task SaveWebStackAsync(CaddyStack stack, CancellationToken token)
    {
        stack.Validate();
        var proposed = _settings with { Web = stack.Configuration };
        await _catalog.SaveAsync(proposed, token);
        _settings = proposed;
        _caddy = stack;
    }

    public Task RegisterWebRuntimeAsync(ServiceKind kind, string executable, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => ImportWebRuntimeAsync(kind, executable, cancellationToken), cancellationToken);

    private async Task ImportWebRuntimeAsync(ServiceKind kind, string executable, CancellationToken token, string? expected = null)
    {
        var stack = _caddy ?? throw new InvalidOperationException("Initialize the environment first.");
        var name = kind switch { ServiceKind.Caddy => "caddy", ServiceKind.FrankenPhp => "frankenphp", _ => throw new ArgumentException("Unsupported web runtime.") };
        executable = Path.GetFullPath(executable);
        if (!Path.GetFileName(executable).Equals(name + ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Select {name}.exe from an extracted Windows distribution.");
        RuntimePaths.VerifyExecutable(executable, true);
        var parent = Path.GetDirectoryName(executable)!;
        var directory = Path.Combine(_dataDirectory, "runtimes", name, Guid.NewGuid().ToString("N"));
        var target = Path.Combine(directory, name + ".exe");
        try
        {
            if (kind == ServiceKind.FrankenPhp) await RuntimeCatalog.CopyDistributionAsync(parent, directory, token);
            else
            {
                Directory.CreateDirectory(directory);
                File.Copy(executable, target);
            }
            if (kind == ServiceKind.FrankenPhp) await _catalog.CheckPhpCliAsync(directory, token);
            var probe = await _supervisor.RunAsync(new ProcessCommand { ExecutablePath = target, WorkingDirectory = directory, Arguments = ["version"] },
                TimeSpan.FromSeconds(15), token);
            var match = Regex.Match(probe.StandardOutput.Trim(), kind == ServiceKind.Caddy ? @"^v(\d+\.\d+\.\d+)\b" : @"^FrankenPHP v?(\d+\.\d+\.\d+)\s+PHP (\d+\.\d+\.\d+\S*)");
            if (probe.ExitCode != 0 || !match.Success || expected is not null && expected != match.Groups[1].Value)
                throw new InvalidDataException($"Could not verify the Windows {name} version. {probe.StandardError}");
            var version = match.Groups[1].Value;
            var id = "v" + version.Replace('.', '-');
            if (kind == ServiceKind.Caddy && File.Exists(stack.Executable) || kind == ServiceKind.FrankenPhp && stack.Runtimes.Any(r => r.Id == id))
                throw new InvalidOperationException($"{name} is already installed for this selection.");
            var proposed = stack with { Executable = target };
            if (kind == ServiceKind.FrankenPhp)
            {
                var port = RuntimeCatalog.AllocatePort(11000, _settings, p => stack.Runtimes.All(r => r.Port != p) && RuntimePaths.IsPortAvailable(p));
                proposed = stack with { Runtimes = [.. stack.Runtimes, new FrankenRuntime(id, target, port, match.Groups[2].Value, version)] };
            }
            await SaveWebStackAsync(proposed, token);
        }
        catch
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            throw;
        }
    }
}
