using System.Text;
using PhpDevTray.Core;
using PhpDevTray.Infrastructure.Configuration;
using PhpDevTray.Infrastructure.Storage;

namespace PhpDevTray.Infrastructure.Runtimes;

internal sealed partial record CaddyStack(string Executable, FrankenRuntime[] Runtimes)
{
    public WebSite[] Sites { get; init; } = [];

    internal static string SiteFile(string root, WebSite site)
    {
        _ = RuntimePaths.SafeHostname(site.CaddyfileKey ?? site.Hostname);
        return Path.Combine(root, "user", "caddy", "sites", site.CaddyfileName);
    }

    internal static async Task EnsureSiteFileAsync(string root, WebSite site, CancellationToken token)
    {
        var path = SiteFile(root, site);
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var host = RuntimePaths.SafeHostname(site.Hostname);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var contents = $"# Site {host}\n" +
                "# This file contains the directives for this site.\n" +
                "# The selected FrankenPHP instance loads it for FrankenPHP sites.\n" +
                "# Caddy loads it directly for PHP NTS sites.\n\n" +
                "# Serve files from the document root chosen when the site was created.\n" +
                "# The * matcher applies this root to every request.\n" +
                $"root * {QuotePath(site.DocumentRoot)}\n\n" +
                "# PHP DevTray adds the PHP handler for the runtime selected in Sites.\n" +
                "# Add site directives below, for example to set a response header.\n" +
                "# header X-Project \"My project\"\n" +
                "# Restart the site services after editing this file.\n";
            await File.WriteAllTextAsync(temporary, contents, token);
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path)) { }
        }
        finally { File.Delete(temporary); }
    }

    internal static async Task<CaddyStack> LoadAsync(string root, CancellationToken token)
    {
        var settingsPath = Path.Combine(root, "settings.json");
        var settings = await AtomicJsonStore.ReadAsync<AppSettings>(settingsPath, token) ?? new();
        var web = settings.Web ?? throw new InvalidDataException("The web configuration must be an object.");
        var stack = new CaddyStack(web.Executable, web.Runtimes) { Sites = web.Sites };
        stack.Validate();
        foreach (var site in stack.Sites) await EnsureSiteFileAsync(root, site, token);
        return stack;
    }

    internal WebConfiguration Configuration => new(Executable, Runtimes, Sites);

    internal void Validate()
    {
        var ports = new HashSet<int> { 80, 443 };
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var caddyfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var runtime in Runtimes)
        {
            if (!Version.TryParse(runtime.Version, out _) || !Version.TryParse(runtime.PhpVersion, out _))
                throw new InvalidDataException("A FrankenPHP installation must specify its FrankenPHP and PHP versions.");
            RuntimePaths.ValidatePort(runtime.Port);
            if (!ids.Add(RuntimePaths.SafeIdentifier(runtime.Id)) || !ports.Add(runtime.Port))
                throw new InvalidDataException("Each FrankenPHP instance needs a unique ID and a dedicated backend port.");
        }
        foreach (var site in Sites)
        {
            if (!hosts.Add(RuntimePaths.SafeHostname(site.Hostname))) throw new InvalidDataException($"Duplicate hostname {site.Hostname}.");
            if (!caddyfiles.Add(RuntimePaths.SafeHostname(site.CaddyfileKey ?? site.Hostname)))
                throw new InvalidDataException($"The Caddyfile for {site.Hostname} is already used by another site.");
            _ = QuotePath(site.DocumentRoot);
        }
    }

    private static string QuotePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(['"', '{', '}', '`', '\r', '\n', '\0']) >= 0)
            throw new InvalidDataException($"Unsupported Caddyfile path: {path}");
        return "\"" + Path.GetFullPath(path).Replace('\\', '/') + "\"";
    }

    private static string SiteDirectives(string root, WebSite site, PhpInstallation? php)
    {
        var handler = php is null ? "php_server" : $"php_fastcgi 127.0.0.1:{php.Port}\n file_server";
        return $"import {QuotePath(SiteFile(root, site))}\n {handler}";
    }

    internal async Task<List<ManagedServiceDefinition>> PrepareAsync(string root, IProcessSupervisor supervisor, AppSettings settings, CancellationToken token,
        string? onlyRuntime = null, string? configurationGeneration = null, bool allowIdleProxy = false)
    {
        var services = new List<ManagedServiceDefinition>();
        var storage = QuotePath(Path.Combine(root, "data", "caddy"));
        var edge = new StringBuilder($"{{\n admin off\n persist_config off\n auto_https disable_redirects\n storage file_system {storage}\n}}\n");
        var enabled = Sites.Where(s => s.Enabled && (onlyRuntime is null || s.RuntimeId == onlyRuntime)).ToArray();
        if (onlyRuntime is null && enabled.Length == 0)
        {
            if (!allowIdleProxy) throw new InvalidOperationException("Add or enable a site before starting Caddy.");
            edge.AppendLine("http://:80 {\n bind 127.0.0.1\n respond 404\n}");
        }
        foreach (var site in enabled)
        {
            var host = RuntimePaths.SafeHostname(site.Hostname);
            edge.AppendLine($"http://{host} {{\n bind 127.0.0.1\n redir https://{host}{{uri}} permanent\n}}");
            var php = settings.PhpInstallations.FirstOrDefault(p => p.Id == site.RuntimeId);
            if (php is not null)
            {
                edge.AppendLine($"https://{host} {{\n bind 127.0.0.1\n tls internal\n {SiteDirectives(root, site, php)}\n}}");
            }
            else if (!Runtimes.Any(r => "franken-" + r.Id == site.RuntimeId))
                throw new InvalidDataException($"Unknown runtime {site.RuntimeId} for {host}.");
        }
        foreach (var runtime in Runtimes)
        {
            var assigned = enabled.Where(s => s.RuntimeId == "franken-" + runtime.Id).ToArray();
            if (assigned.Length == 0 && onlyRuntime != "franken-" + runtime.Id) continue;
            var backend = new StringBuilder("{\n admin off\n persist_config off\n auto_https off\n skip_install_trust\n frankenphp\n}\n");
            if (assigned.Length == 0) backend.AppendLine($"http://127.0.0.1:{runtime.Port} {{\n bind 127.0.0.1\n respond 404\n}}");
            foreach (var site in assigned)
            {
                var host = RuntimePaths.SafeHostname(site.Hostname);
                backend.AppendLine($"http://{host}:{runtime.Port} {{\n bind 127.0.0.1\n {SiteDirectives(root, site, null)}\n}}");
                edge.AppendLine($"https://{host} {{\n bind 127.0.0.1\n tls internal\n reverse_proxy 127.0.0.1:{runtime.Port}\n}}");
            }
            services.Add(await Prepare("franken-" + runtime.Id, runtime.Label, ServiceKind.FrankenPhp, runtime.Executable, backend, [runtime.Port]));
        }
        if (onlyRuntime is null) services.Add(await Prepare("caddy", "Caddy", ServiceKind.Caddy, Executable, edge, enabled.Length == 0 ? [80] : [80, 443]));
        return services;

        async Task<ManagedServiceDefinition> Prepare(string id, string name, ServiceKind kind, string executable, StringBuilder config, int[] ports)
        {
            if (!Path.IsPathFullyQualified(executable)) throw new InvalidDataException($"Configure an absolute executable path for {name}.");
            RuntimePaths.VerifyExecutable(executable);
            var directory = Path.Combine(root, "data", "state", "caddy", id);
            var path = Path.Combine(directory, configurationGeneration ?? "", "Caddyfile");
            await AtomicJsonStore.WriteTextAsync(path, config.ToString(), token);
            IReadOnlyDictionary<string, string> environment = new Dictionary<string, string>();
            if (kind == ServiceKind.FrankenPhp)
                environment = await new RuntimeCatalog(root, supervisor).FrankenEnvironmentAsync(
                    Runtimes.Single(runtime => "franken-" + runtime.Id == id), settings, token);
            var command = new ProcessCommand { EnvironmentVariables = environment, ExecutablePath = executable, WorkingDirectory = Path.GetDirectoryName(executable),
                Arguments = ["run", "--config", path] };
            var result = await supervisor.RunAsync(command with { Arguments = ["validate", "--config", path] }, TimeSpan.FromSeconds(30), token);
            if (result.ExitCode != 0) throw new InvalidDataException($"{name} configuration failed: {result.StandardError}");
            return new() { Id = id, DisplayName = name, Kind = kind, Command = command,
                LogDirectory = Path.Combine(root, "logs", id), HealthEndpoints = ports.Select(p => new HealthEndpoint(HealthCheckKind.Tcp, "127.0.0.1", p)).ToArray() };
        }
    }
}
