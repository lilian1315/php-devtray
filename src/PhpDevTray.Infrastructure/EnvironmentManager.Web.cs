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
        var fileKey = existing is null && stack.Sites.Any(other => (other.CaddyfileKey ?? other.Hostname).Equals(host, StringComparison.OrdinalIgnoreCase))
            ? Guid.NewGuid().ToString("N") : existing?.CaddyfileKey;
        site = site with { CaddyfileKey = fileKey };
        var proposed = stack with { Sites = existing is null ? [.. stack.Sites, site]
            : stack.Sites.Select(item => item == existing ? site : item).ToArray() };
        proposed.Validate();
        await CaddyStack.EnsureSiteFileAsync(_dataDirectory, site, cancellationToken);
        var oldId = existing?.RuntimeId ?? site.RuntimeId;
        var activationChanged = (existing?.Enabled ?? false) != site.Enabled;
        if (activationChanged || site.Enabled && oldId != site.RuntimeId)
            await ApplySiteRoutingAsync(proposed, oldId, site, cancellationToken);
        else
            await SaveWebStackAsync(proposed, cancellationToken);

        if (existing is null || oldId != site.RuntimeId)
            await RefreshSiteRootsAsync(CancellationToken.None);
    }, cancellationToken);

    public Task RenameSiteAsync(string currentHostname, string newHostname, CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        var stack = _caddy ?? throw new InvalidOperationException("Initialize the environment first.");
        var oldHost = RuntimePaths.SafeHostname(currentHostname.Trim());
        var host = RuntimePaths.SafeHostname(newHostname.Trim());
        var site = stack.Sites.SingleOrDefault(item => item.Hostname.Equals(oldHost, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"The site {oldHost} no longer exists. Refresh Sites and try again.");
        if (host == oldHost) return;
        if (stack.Sites.Any(item => item.Hostname.Equals(host, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A site already uses {host}. Choose a different hostname.");
        if (!File.Exists(CaddyStack.SiteFile(_dataDirectory, site)))
            throw new FileNotFoundException($"Restore {site.CaddyfileName} before renaming {oldHost}.");

        var renamed = site with { Hostname = host, CaddyfileKey = site.CaddyfileKey ?? oldHost };
        var proposed = stack with { Sites = stack.Sites.Select(item => item == site ? renamed : item).ToArray() };
        proposed.Validate();
        if (site.Enabled)
            await ApplySiteRoutingAsync(proposed, site.RuntimeId, renamed, cancellationToken);
        else
            await SaveWebStackAsync(proposed, cancellationToken);

        var roots = new Dictionary<string, string?>(_siteRoots, StringComparer.OrdinalIgnoreCase);
        if (roots.Remove(oldHost, out var root)) roots[host] = root;
        _siteRoots = roots;
        Log("Information", $"Renamed site {oldHost} to {host}");
    }, cancellationToken);

    private async Task StartWebDefinitionAsync(ManagedServiceDefinition service, CancellationToken token)
    {
        await _supervisor.StartAsync(service, token);
        _definitions[service.Id] = service;
    }

    private async Task ApplySiteRoutingAsync(CaddyStack proposed, string oldId, WebSite site, CancellationToken token)
    {
        var proxyRunning = _supervisor.IsRunning("caddy");
        var affected = new[] { oldId, site.RuntimeId }.Distinct().Where(id => id.StartsWith("franken-", StringComparison.Ordinal)).ToArray();
        var running = affected.Where(_supervisor.IsRunning).ToArray();
        if (!proxyRunning && running.Length == 0)
        {
            await SaveWebStackAsync(proposed, token);
            return;
        }
        var previousIds = proxyRunning ? running.Append("caddy") : running;
        var previousDefinitions = previousIds.Select(id => _definitions.GetValueOrDefault(id)
            ?? throw new InvalidOperationException($"No recoverable configuration for {id}. Restart the service and try again.")).ToArray();
        var generation = $"site-{Guid.NewGuid():N}";
        var prepared = proxyRunning
            ? (await proposed.PrepareAsync(_dataDirectory, _supervisor, _settings, token,
                configurationGeneration: generation, allowIdleProxy: true))
            : [];
        var candidate = prepared.Where(def => running.Contains(def.Id) || site.Enabled && def.Id == site.RuntimeId).ToList();
        // A running backend still needs an empty route table if its last site was disabled or moved.
        foreach (var id in running.Where(id => candidate.All(def => def.Id != id)))
            candidate.AddRange(await proposed.PrepareAsync(_dataDirectory, _supervisor, _settings, token, id, generation));
        if (proxyRunning)
            candidate.Add(prepared.Single(def => def.Id == "caddy"));

        var started = new Stack<string>();
        try
        {
            if (proxyRunning) await _supervisor.StopAsync("caddy", TimeSpan.FromSeconds(20), token);
            foreach (var id in running) await _supervisor.StopAsync(id, TimeSpan.FromSeconds(5), token);
            if (proxyRunning && site.Enabled && site.RuntimeId.StartsWith("php:", StringComparison.Ordinal) && !_supervisor.IsRunning(site.RuntimeId))
            {
                await StartCoreAsync(site.RuntimeId, token);
                started.Push(site.RuntimeId);
            }
            foreach (var service in candidate)
            {
                await StartWebDefinitionAsync(service, token);
                started.Push(service.Id);
            }
            await SaveWebStackAsync(proposed, token);
        }
        catch (Exception failure)
        {
            try
            {
                foreach (var id in started) await _supervisor.StopAsync(id, TimeSpan.FromSeconds(5), CancellationToken.None);
                foreach (var service in previousDefinitions)
                    if (!_supervisor.IsRunning(service.Id)) await StartWebDefinitionAsync(service, CancellationToken.None);
            }
            catch (Exception recovery) { throw new AggregateException("The site change failed and recovery was incomplete. Check Activity for details.", failure, recovery); }
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
