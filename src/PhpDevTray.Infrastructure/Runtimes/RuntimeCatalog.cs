using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PhpDevTray.Core;
using PhpDevTray.Infrastructure.Configuration;
using PhpDevTray.Infrastructure.Storage;
using Microsoft.Win32;

namespace PhpDevTray.Infrastructure.Runtimes;

/// <summary>Initial local-runtime catalog. Imports extracted distributions; it does not download or auto-update software.</summary>
public sealed partial class RuntimeCatalog
{
    private readonly string _root;
    private readonly IProcessSupervisor _supervisor;
    private readonly string _settingsPath;
    private readonly string? _launcherBundleDirectory;
    private static readonly HttpClient MailClient = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(10) };

    public RuntimeCatalog(string dataDirectory, IProcessSupervisor supervisor, string? launcherBundleDirectory = null)
    {
        _root = Path.GetFullPath(dataDirectory);
        _supervisor = supervisor;
        _settingsPath = Path.Combine(_root, "settings.json");
        _launcherBundleDirectory = launcherBundleDirectory is null ? null : Path.GetFullPath(launcherBundleDirectory);
        foreach (var relative in new[] { "bin", "runtimes/php", "runtimes/nginx", "runtimes/mailpit", "user/php",
            "user/nginx/sites-available", "user/nginx/generated/generations", "data/mailpit", "data/state", "logs/application",
            "logs/nginx", "logs/php", "logs/mailpit", "downloads", "backups" })
            Directory.CreateDirectory(Path.Combine(_root, relative));
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        AppSettings settings;
        try { settings = await AtomicJsonStore.ReadAsync<AppSettings>(_settingsPath, cancellationToken) ?? new(); }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Settings are invalid. The original file was preserved. Restore {_settingsPath}.bak or repair {_settingsPath}.", exception);
        }
        ValidateSettings(settings);
        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ValidateSettings(settings);
        await AtomicJsonStore.WriteAsync(_settingsPath, settings, cancellationToken);
    }

    public async Task<PhpInstallation> ImportPhpAsync(string phpExecutable, AppSettings settings, CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(phpExecutable);
        if (!string.Equals(Path.GetFileName(source), "php.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select php.exe from an extracted official Windows x64 NTS distribution.");
        RuntimePaths.VerifyExecutable(source, true);
        var sourceDirectory = Path.GetDirectoryName(source)!;
        RuntimePaths.VerifyExecutable(Path.Combine(sourceDirectory, "php-cgi.exe"), true);
        if (!File.Exists(Path.Combine(sourceDirectory, "php.ini-development")))
            throw new InvalidDataException("Select a complete PHP distribution containing php.ini-development.");
        var result = await RunCheckedAsync(source, ["-n", "-v"], sourceDirectory, cancellationToken);
        var output = result.StandardOutput + result.StandardError;
        var match = PhpVersion().Match(output);
        if (!match.Success) throw new InvalidDataException("PHP did not report a recognized version. Verify its Visual C++ runtime prerequisite.");
        if (!output.Contains("NTS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This initial implementation supports Non Thread Safe (NTS) PHP builds only.");
        var version = match.Groups[1].Value;
        var channel = version[..version.LastIndexOf('.')];
        var active = settings.PhpInstallations.SingleOrDefault(p => p.Channel == channel);
        if (active?.Version == version)
            throw new InvalidOperationException($"PHP {version} is already active. Same-patch Windows rebuild replacement is not supported.");
        var port = active?.Port ?? AllocatePort(9000 + int.Parse(channel.Replace(".", "")), settings);
        // A fresh directory prevents failed imports from deleting, replacing, or reusing previously trusted binaries.
        var destination = Path.Combine(_root, "runtimes", "php", version + "-" + Guid.NewGuid().ToString("N"));
        await CopyDistributionAsync(sourceDirectory, destination, cancellationToken);
        var installation = (active ?? new PhpInstallation
        {
            Id = $"php:{channel}", Version = version, Channel = channel, Directory = destination, Port = port
        }) with { Version = version, Directory = destination };
        try
        {
            _ = await BuildPhpCandidateAsync(installation, settings, cancellationToken);
            return installation;
        }
        catch
        {
            DeleteUnreferencedPhpDirectory(destination, settings);
            throw;
        }
    }

    public async Task<string> ImportNginxAsync(string executable, CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(executable);
        if (!string.Equals(Path.GetFileName(source), "nginx.exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Select nginx.exe from the extracted official distribution.");
        RuntimePaths.VerifyExecutable(source);
        var result = await RunCheckedAsync(source, ["-v"], Path.GetDirectoryName(source)!, cancellationToken);
        var match = NginxVersion().Match(result.StandardOutput + result.StandardError);
        if (!match.Success) throw new InvalidDataException("The selected executable did not report an Nginx version.");
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(source)!, "conf", "mime.types")))
            throw new InvalidDataException("The complete Nginx distribution, including conf/mime.types, is required.");
        var destination = Path.Combine(_root, "runtimes", "nginx", match.Groups[1].Value);
        await CopyDistributionAsync(Path.GetDirectoryName(source)!, destination, cancellationToken);
        return destination;
    }

    public async Task<string> ImportMailpitAsync(string executable, CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(executable);
        RuntimePaths.VerifyExecutable(source, true);
        var result = await RunCheckedAsync(source, ["version"], Path.GetDirectoryName(source)!, cancellationToken);
        var output = result.StandardOutput + result.StandardError;
        var match = MailpitVersion().Match(output);
        if (!match.Success || !output.Contains("mailpit", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The executable did not report a Mailpit version.");
        var directory = Path.Combine(_root, "runtimes", "mailpit", match.Groups[1].Value);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "mailpit.exe");
        if (!File.Exists(destination))
        {
            var temporary = destination + ".tmp";
            await using (var input = File.OpenRead(source))
            await using (var outputStream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(outputStream, cancellationToken);
            File.Move(temporary, destination, false);
        }
        return destination;
    }

    public async Task<ManagedServiceDefinition> BuildPhpAsync(PhpInstallation installation, AppSettings settings, CancellationToken cancellationToken = default)
        => await BuildPhpCoreAsync(installation, settings, publish: true, cancellationToken);

    private async Task<ManagedServiceDefinition> BuildPhpCoreAsync(PhpInstallation installation, AppSettings settings, bool publish, CancellationToken cancellationToken)
    {
        RuntimePaths.VerifyExecutable(Path.Combine(installation.Directory, "php.exe"), true);
        RuntimePaths.VerifyExecutable(Path.Combine(installation.Directory, "php-cgi.exe"), true);
        RuntimePaths.ValidatePort(installation.Port);
        var configuration = await PreparePhpConfigurationAsync(installation, settings, cancellationToken, publish: publish);
        return CreatePhpDefinition(installation, configuration);
    }

    private ManagedServiceDefinition CreatePhpDefinition(PhpInstallation installation, PhpConfiguration configuration)
        => new ManagedServiceDefinition
        {
            Id = $"php:{installation.Channel}", DisplayName = $"PHP {installation.Channel}", Kind = ServiceKind.Php,
            Version = installation.Version, LogDirectory = Path.Combine(_root, "logs", "php", installation.Channel),
            Command = new ProcessCommand
            {
                ExecutablePath = Path.Combine(installation.Directory, "php-cgi.exe"), WorkingDirectory = installation.Directory,
                Arguments = ["-c", configuration.PrimaryIni, "-d", "cgi.fix_pathinfo=0", "-b", $"127.0.0.1:{installation.Port}"],
                EnvironmentVariables = PhpEnvironment(configuration.ScanDirectory)
            },
            HealthEndpoints = [new(HealthCheckKind.FastCgi, "127.0.0.1", installation.Port)]
        };

    /// <summary>Creates editable site source if missing; existing user bytes are never replaced.</summary>
    public async Task<string> EnsureSiteSourceAsync(SiteDefinition site, CancellationToken cancellationToken = default)
    {
        RuntimePaths.SafeIdentifier(site.Id);
        var source = Path.Combine(_root, "user", "nginx", "sites-available", site.Id + ".conf");
        if (File.Exists(source)) return source;
        RuntimePaths.ValidatePort(site.Port);
        var hostname = RuntimePaths.SafeHostname(site.Hostname);
        if (!Path.IsPathFullyQualified(site.DocumentRoot)) throw new ArgumentException("The site document root must be absolute.");
        var root = RuntimePaths.NginxQuote(site.DocumentRoot);
        var marker = $"include \"@pdt/php/{site.Id}@\";";
        var initial = $"# User-owned site source: {site.Id}. Managed PHP include must remain.\nserver {{\n    listen 127.0.0.1:{site.Port};\n" +
            $"    server_name {hostname};\n    root {root};\n    index index.php index.html;\n" +
            "    location / { try_files $uri $uri/ /index.php?$query_string; }\n    " + marker + "\n}\n";
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        var temporary = source + ".create-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, initial, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Move(temporary, source, false); }
            catch (IOException) when (File.Exists(source)) { /* Preserve a file created concurrently by its owner. */ }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return source;
    }

    public async Task<ManagedServiceDefinition> BuildNginxAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.NginxDirectory)) throw new InvalidOperationException("Import Nginx first.");
        foreach (var site in settings.Sites) await EnsureSiteSourceAsync(site, cancellationToken);
        var executable = Path.Combine(settings.NginxDirectory, "nginx.exe");
        RuntimePaths.VerifyExecutable(executable);
        var prefix = Path.Combine(_root, "data", "nginx");
        foreach (var directory in new[] { prefix, Path.Combine(prefix, "logs"), Path.Combine(prefix, "temp"), Path.Combine(_root, "logs", "nginx") }) Directory.CreateDirectory(directory);
        var generated = Path.Combine(_root, "user", "nginx", "generated");
        var sourceRoot = Path.Combine(_root, "user", "nginx", "nginx.conf");
        const string sitesMarker = "include \"@pdt/sites@\";";
        if (!File.Exists(sourceRoot))
        {
            var initial = "# User-owned source. Use Apply/Start in the manager to validate and activate.\nworker_processes 1;\n" +
                "events { worker_connections 512; }\nhttp {\n    include \"@pdt/mime@\";\n    default_type application/octet-stream;\n" +
                "    sendfile on;\n    keepalive_timeout 15;\n    include \"@pdt/sites@\";\n}\n";
            await File.WriteAllTextAsync(sourceRoot, initial, cancellationToken);
        }
        var sourceText = await File.ReadAllTextAsync(sourceRoot, cancellationToken);
        if (Count(sourceText, sitesMarker) != 1 || Count(sourceText, "include \"@pdt/mime@\";") != 1)
            throw new InvalidDataException($"{sourceRoot} must contain exactly one @pdt/sites@ and @pdt/mime@ include marker. It was not overwritten.");
        ValidateSourceIncludes(sourceText, ["@pdt/sites@", "@pdt/mime@"], sourceRoot);
        if (ReadDirectives(sourceText).Any(d => d[0].Value is "pid" or "error_log"))
            throw new InvalidDataException("The root source must not override the manager's fixed pid or error_log directives.");
        var generation = Path.Combine(generated, "generations", DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(generation);
        var capturedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [sourceRoot] = Hash(sourceText) };
        try
        {
            var mimeFile = Path.Combine(generation, "mime.types");
            File.Copy(Path.Combine(settings.NginxDirectory, "conf", "mime.types"), mimeFile);
            var paramsFile = Path.Combine(generation, "fastcgi.params");
            await File.WriteAllTextAsync(paramsFile, FastCgiParameters, cancellationToken);
            var siteIncludes = new StringBuilder();
            foreach (var site in settings.Sites.Where(s => s.Enabled))
            {
                ValidateSite(site, settings);
                var installation = settings.PhpInstallations.Single(p => p.Channel == site.PhpChannel);
                var sourceSite = await EnsureSiteSourceAsync(site, cancellationToken);
                var marker = $"include \"@pdt/php/{site.Id}@\";";
                var text = await File.ReadAllTextAsync(sourceSite, cancellationToken);
                capturedHashes[sourceSite] = Hash(text);
                if (Count(text, marker) != 1 || Regex.IsMatch(text, @"\bfastcgi_pass\b", RegexOptions.CultureInvariant))
                    throw new InvalidDataException($"Site {site.Id} needs reconciliation: keep exactly one managed PHP marker and no competing fastcgi_pass. The source was preserved.");
                ValidateSourceIncludes(text, [$"@pdt/php/{site.Id}@"], sourceSite, allowAbsoluteFiles: true);
                ValidateLoopbackListeners(text, sourceSite);
                ValidateSiteSourceMatches(text, site, sourceSite);
                var phpInclude = Path.Combine(generation, site.Id + ".php.conf");
                var phpText = "location ~* \\.php$ {\n    try_files $uri =404;\n    include " + RuntimePaths.NginxQuote(paramsFile) + ";\n" +
                    "    fastcgi_param SCRIPT_FILENAME $document_root$fastcgi_script_name;\n    fastcgi_pass 127.0.0.1:" + installation.Port + ";\n}\n" +
                    "location ~* \\.php/ { return 404; }\n";
                await File.WriteAllTextAsync(phpInclude, phpText, cancellationToken);
                var compiledSite = Path.Combine(generation, site.Id + ".conf");
                await File.WriteAllTextAsync(compiledSite, text.Replace(marker, "include " + RuntimePaths.NginxQuote(phpInclude) + ";", StringComparison.Ordinal), cancellationToken);
                siteIncludes.Append("include ").Append(RuntimePaths.NginxQuote(compiledSite)).AppendLine(";");
            }
            var health = "server { listen 127.0.0.1:8080 default_server; server_name localhost; location = /__pdt_health { default_type text/plain; return 200 'PHP DevTray'; } location / { return 200 'PHP DevTray is running. Configure a site in the tray popup.'; } }\n";
            // Use an application-owned fallback health server. Explicit user sites may also share port 8080 by hostname.
            var compiledRoot = "pid " + RuntimePaths.NginxQuote(Path.Combine(prefix, "nginx.pid")) + ";\nerror_log " + RuntimePaths.NginxQuote(Path.Combine(_root, "logs", "nginx", "error.log")) + ";\n" +
                sourceText.Replace("include \"@pdt/mime@\";", "include " + RuntimePaths.NginxQuote(mimeFile) + ";", StringComparison.Ordinal)
                    .Replace(sitesMarker, "access_log " + RuntimePaths.NginxQuote(Path.Combine(_root, "logs", "nginx", "access.log")) + ";\n" + health + siteIncludes, StringComparison.Ordinal);
            ValidateLoopbackListeners(compiledRoot, sourceRoot);
            var candidate = Path.Combine(generation, "nginx.conf");
            await File.WriteAllTextAsync(candidate, compiledRoot, cancellationToken);
            // Relative nested includes resolve against the main configuration's directory in Nginx.
            // Validate through a sibling of the stable bootstrap so -t and actual startup share that prefix.
            var validationBootstrap = Path.Combine(generated, ".validation-" + Guid.NewGuid().ToString("N") + ".conf");
            try
            {
                await File.WriteAllTextAsync(validationBootstrap, "include " + RuntimePaths.NginxQuote(candidate) + ";\n", cancellationToken);
                await RunCheckedAsync(executable, ["-t", "-c", validationBootstrap, "-p", prefix + Path.DirectorySeparatorChar], prefix, cancellationToken);
            }
            finally { if (File.Exists(validationBootstrap)) File.Delete(validationBootstrap); }
            foreach (var captured in capturedHashes)
                if (Hash(await File.ReadAllTextAsync(captured.Key, cancellationToken)) != captured.Value)
                    throw new InvalidOperationException("A source file changed while validating Nginx. Retry to apply its latest revision.");
            var bootstrap = Path.Combine(generated, "bootstrap.conf");
            await AtomicJsonStore.WriteTextAsync(bootstrap, "# Managed. Do not edit.\ninclude " + RuntimePaths.NginxQuote(candidate) + ";\n", cancellationToken);
            return new ManagedServiceDefinition
            {
                Id = "nginx", DisplayName = "Nginx", Kind = ServiceKind.Nginx,
                Command = new ProcessCommand { ExecutablePath = executable, WorkingDirectory = prefix, Arguments = ["-c", bootstrap, "-p", prefix + Path.DirectorySeparatorChar] },
                GracefulStopCommand = new ProcessCommand { ExecutablePath = executable, WorkingDirectory = prefix, Arguments = ["-s", "quit", "-c", bootstrap, "-p", prefix + Path.DirectorySeparatorChar] },
                HealthEndpoints = [new(HealthCheckKind.Http, "127.0.0.1", 8080, "/__pdt_health")],
                LogDirectory = Path.Combine(_root, "logs", "nginx"), Version = Path.GetFileName(settings.NginxDirectory)
            };
        }
        catch
        {
            // Never remove user-owned sources. A failed immutable candidate is safe to discard.
            Directory.Delete(generation, true);
            throw;
        }
    }

    public Task<ManagedServiceDefinition> BuildMailpitAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(settings.MailpitExecutable)) throw new InvalidOperationException("Import Mailpit first.");
        RuntimePaths.VerifyExecutable(settings.MailpitExecutable, true);
        RuntimePaths.ValidatePort(settings.SmtpPort);
        RuntimePaths.ValidatePort(settings.MailpitHttpPort);
        if (settings.SmtpPort == settings.MailpitHttpPort) throw new InvalidOperationException("SMTP and HTTP ports must differ.");
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
            if (((string)variable.Key).StartsWith("MP_", StringComparison.OrdinalIgnoreCase)) environment[(string)variable.Key] = "";
        environment["MP_SMTP_RELAY_ALL"] = "false";
        environment["MP_SMTP_RELAY_CONFIG"] = "";
        environment["MP_SMTP_FORWARD_CONFIG"] = "";
        environment["MP_SMTP_RELAY_MATCHING"] = "";
        return Task.FromResult(new ManagedServiceDefinition
        {
            Id = "mailpit", DisplayName = "Mail Trap", Kind = ServiceKind.Mailpit,
            Command = new ProcessCommand
            {
                ExecutablePath = settings.MailpitExecutable, WorkingDirectory = Path.GetDirectoryName(settings.MailpitExecutable),
                Arguments = ["--smtp", $"127.0.0.1:{settings.SmtpPort}", "--listen", $"127.0.0.1:{settings.MailpitHttpPort}",
                    "--database", Path.Combine(_root, "data", "mailpit", "mailpit.db"), "--max", "500", "--smtp-disable-rdns"],
                EnvironmentVariables = environment
            },
            HealthEndpoints = [new(HealthCheckKind.Smtp, "127.0.0.1", settings.SmtpPort), new(HealthCheckKind.Http, "127.0.0.1", settings.MailpitHttpPort, "/api/v1/info")],
            LogDirectory = Path.Combine(_root, "logs", "mailpit"), Version = Path.GetFileName(Path.GetDirectoryName(settings.MailpitExecutable))
        });
    }

    public async Task SetPathIntegrationAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (AppDeployment.IsPackaged)
            throw new InvalidOperationException("Manage the PHP DevTray php.exe alias in Windows Settings > App execution aliases.");
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(_root, "bin");
        if (enabled && !File.Exists(Path.Combine(directory, "php.exe")))
            throw new InvalidOperationException("The native PHP launcher is not deployed. Publish the CLI into the application's cli folder, then retry.");
        var previous = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
        static string Normalize(string value) => Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')).TrimEnd('\\', '/');
        var entries = previous.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(p => !string.Equals(Normalize(p), Normalize(directory), StringComparison.OrdinalIgnoreCase)).ToList();
        if (enabled) entries.Add(directory);
        Environment.SetEnvironmentVariable("Path", string.Join(';', entries), EnvironmentVariableTarget.User);
        BroadcastEnvironmentChange();
        await Task.CompletedTask;
    }

    public async Task UpdateCliSelectionAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var launcherDirectory = Path.Combine(_root, "bin");
        Directory.CreateDirectory(launcherDirectory);
        var bundled = _launcherBundleDirectory ?? Path.Combine(AppContext.BaseDirectory, "cli");
        if (!File.Exists(Path.Combine(bundled, "php.exe"))) bundled = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(bundled, "php.exe")) && !string.Equals(Path.GetFullPath(bundled), Path.GetFullPath(launcherDirectory), StringComparison.OrdinalIgnoreCase))
        {
            foreach (var source in Directory.EnumerateFiles(bundled))
            {
                var fileName = Path.GetFileName(source);
                if (string.Equals(fileName, "launcher.json", StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Copy(source, Path.Combine(launcherDirectory, fileName), true); }
                catch (IOException) when (File.Exists(Path.Combine(launcherDirectory, fileName))) { /* Running CLI apphost may be locked; selection still updates atomically. */ }
            }
            foreach (var source in Directory.EnumerateDirectories(bundled))
            {
                var destination = Path.Combine(launcherDirectory, Path.GetFileName(source));
                if (!Directory.Exists(destination)) await CopyDistributionAsync(source, destination, cancellationToken);
            }
        }
        if (settings.DefaultPhpChannel is null)
        {
            await AtomicJsonStore.WriteAsync(Path.Combine(launcherDirectory, "launcher.json"), new CliSelection(null, null, null, null), cancellationToken);
            return;
        }
        var selected = settings.PhpInstallations.SingleOrDefault(p => p.Channel == settings.DefaultPhpChannel)
            ?? throw new InvalidOperationException("The selected default PHP channel is not installed.");
        var configuration = await PreparePhpConfigurationAsync(selected, settings, cancellationToken);
        await AtomicJsonStore.WriteAsync(Path.Combine(launcherDirectory, "launcher.json"), new CliSelection(
            Path.Combine(selected.Directory, "php.exe"), configuration.PrimaryIni, configuration.ScanDirectory, selected.Channel), cancellationToken);
    }

    public async Task<IReadOnlyList<MailMessageSummary>> GetMessagesAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        EnsureMailpitOwned();
        using var response = await MailClient.GetAsync(MailUri(settings, "/api/v1/messages?limit=100"), cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var result = new List<MailMessageSummary>();
        if (!json.RootElement.TryGetProperty("messages", out var messages)) return result;
        foreach (var item in messages.EnumerateArray())
        {
            var id = Text(item, "ID");
            var from = item.TryGetProperty("From", out var sender) ? Address(sender) : "";
            var to = item.TryGetProperty("To", out var recipients) && recipients.ValueKind == JsonValueKind.Array ? string.Join(", ", recipients.EnumerateArray().Select(Address)) : "";
            _ = DateTimeOffset.TryParse(Text(item, "Created"), out var created);
            result.Add(new(id, Text(item, "Subject"), from, to, created));
        }
        return result;
    }

    public async Task<string> GetMessageTextAsync(string id, AppSettings settings, CancellationToken cancellationToken = default)
    {
        EnsureMailpitOwned();
        using var response = await MailClient.GetAsync(MailUri(settings, "/api/v1/message/" + Uri.EscapeDataString(id)), cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var text = Text(json.RootElement, "Text");
        return string.IsNullOrWhiteSpace(text) ? "This message has no plain-text body. HTML preview is not included in this milestone." : text;
    }

    public async Task<IReadOnlyList<string>> GetLogLinesAsync(CancellationToken cancellationToken = default)
    {
        var files = Directory.EnumerateFiles(Path.Combine(_root, "logs"), "*", SearchOption.AllDirectories)
            .Where(p => Path.GetExtension(p) is ".log" or ".jsonl")
            .OrderByDescending(File.GetLastWriteTimeUtc).Take(4).ToArray();
        var lines = new List<string>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lines.Add($"— {Path.GetRelativePath(_root, file)} —");
            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 256 * 1024) stream.Seek(-256 * 1024, SeekOrigin.End);
                using var reader = new StreamReader(stream);
                var content = await reader.ReadToEndAsync(cancellationToken);
                lines.AddRange(content.Split('\n').TakeLast(100).Select(l => l.TrimEnd('\r')));
            }
            catch (IOException exception) { lines.Add(exception.Message); }
        }
        return lines;
    }

    public static void ValidateSite(SiteDefinition site, AppSettings settings)
    {
        RuntimePaths.SafeIdentifier(site.Id);
        RuntimePaths.SafeHostname(site.Hostname);
        RuntimePaths.ValidatePort(site.Port);
        if (site.Port == 8080 && string.Equals(site.Hostname, "localhost", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("localhost:8080 is reserved for the manager's health page. Use a project hostname such as app.test.");
        _ = RuntimePaths.NginxQuote(site.DocumentRoot);
        if (!Path.IsPathFullyQualified(site.DocumentRoot) || !Directory.Exists(site.DocumentRoot)) throw new ArgumentException("The document root must be an existing absolute directory.");
        if (!settings.PhpInstallations.Any(p => p.Channel == site.PhpChannel)) throw new InvalidOperationException($"PHP channel {site.PhpChannel} is not installed.");
        if (settings.Sites.Any(s => s.Id != site.Id && s.Enabled && string.Equals(s.Hostname, site.Hostname, StringComparison.OrdinalIgnoreCase) && s.Port == site.Port))
            throw new InvalidOperationException("Another enabled site already uses that hostname and port.");
    }

    private async Task<PhpConfiguration> PreparePhpConfigurationAsync(PhpInstallation installation, AppSettings settings, CancellationToken cancellationToken,
        bool validate = true, bool publish = true)
    {
        var primary = Path.Combine(installation.Directory, "php.ini-development");
        if (!File.Exists(primary)) throw new FileNotFoundException("The PHP distribution must include its stock php.ini-development file.", primary);
        var sourceDirectory = Path.Combine(_root, "user", "php", installation.Channel, "conf.d");
        Directory.CreateDirectory(sourceDirectory);
        var custom = Path.Combine(sourceDirectory, "20-custom.ini");
        if (!File.Exists(custom)) await File.WriteAllTextAsync(custom, "; Your PHP overrides. This file is never overwritten by PHP DevTray.\n", cancellationToken);
        var customText = await File.ReadAllTextAsync(custom, cancellationToken);
        if (validate)
            ValidateManagedExtensionDirectives(installation, await File.ReadAllTextAsync(primary, cancellationToken), primary, customText, custom);
        var extensions = new StringBuilder("; Managed extension state. Use the manager to change this file.\n");
        foreach (var extension in installation.EnabledExtensions)
        {
            var canonical = NormalizeExtensionName(extension);
            if (!ExtensionName().IsMatch(canonical)) throw new InvalidDataException("Invalid managed extension name.");
            var dll = Path.Combine(installation.Directory, "ext", "php_" + canonical + ".dll");
            if (validate && !File.Exists(dll)) throw new FileNotFoundException($"The enabled {canonical} extension is missing from PHP {installation.Version}.", dll);
            extensions.Append(canonical == "opcache" ? "zend_extension=" : "extension=").Append(RuntimePaths.IniQuote(dll)).AppendLine();
        }
        var managed = "; Managed by PHP DevTray. Do not edit.\n" + "extension_dir=" + RuntimePaths.IniQuote(Path.Combine(installation.Directory, "ext")) + "\n";
        if (installation.MailIntegrationEnabled) managed += $"[mail function]\nSMTP=127.0.0.1\nsmtp_port={settings.SmtpPort}\n";
        var hash = Hash(primary + "\n" + extensions + "\n" + customText + "\n" + managed);
        var resolved = Path.Combine(_root, "data", "state", "php-config", installation.Version, hash[..16], "conf.d");
        Directory.CreateDirectory(resolved);
        await WriteIfChangedAsync(Path.Combine(resolved, "10-extensions.ini"), extensions.ToString(), cancellationToken);
        await WriteIfChangedAsync(Path.Combine(resolved, "20-custom.ini"), customText, cancellationToken);
        await WriteIfChangedAsync(Path.Combine(resolved, "90-managed.ini"), managed, cancellationToken);
        if (validate)
        {
            var result = await _supervisor.RunAsync(new ProcessCommand
            {
                ExecutablePath = Path.Combine(installation.Directory, "php.exe"), WorkingDirectory = installation.Directory,
                Arguments = ["-c", primary, "--ini"], EnvironmentVariables = PhpEnvironment(resolved)
            }, TimeSpan.FromSeconds(15), cancellationToken);
            var diagnostics = ExtensionDiagnostics(result);
            if (diagnostics is not null) throw new InvalidDataException("PHP configuration validation failed: " + diagnostics);
        }
        if (publish)
        {
            await WriteIfChangedAsync(Path.Combine(sourceDirectory, "10-extensions.ini"), extensions.ToString(), cancellationToken);
            await WriteIfChangedAsync(Path.Combine(sourceDirectory, "90-managed.ini"), managed, cancellationToken);
        }
        return new(primary, resolved);
    }

    private async Task<ProcessResult> RunCheckedAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await _supervisor.RunAsync(new ProcessCommand { ExecutablePath = executable, WorkingDirectory = workingDirectory, Arguments = arguments }, TimeSpan.FromSeconds(15), cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} exited with code {result.ExitCode}. {result.StandardError} {result.StandardOutput}");
        return result;
    }

    private static IReadOnlyDictionary<string, string> PhpEnvironment(string scanDirectory) => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["PHP_INI_SCAN_DIR"] = scanDirectory, ["PHPRC"] = "", ["PHP_FCGI_MAX_REQUESTS"] = "0"
    };

    private static async Task CopyDistributionAsync(string source, string destination, CancellationToken cancellationToken)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (string.Equals(source.TrimEnd(Path.DirectorySeparatorChar), destination.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return;
        if (destination.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The import destination cannot be inside the source distribution.");
        if (Directory.Exists(destination)) throw new InvalidOperationException("That runtime version already exists in managed storage. Existing binaries were preserved.");
        var staging = destination + ".staging-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            await CopyTreeAsync(source, staging, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(staging, destination);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static async Task CopyTreeAsync(string source, string destination, CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Runtime imports cannot contain symbolic links or junctions.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Runtime imports cannot contain symbolic links or junctions.");
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(target);
                await CopyTreeAsync(entry, target, cancellationToken);
            }
            else
            {
                await using var input = File.OpenRead(entry);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await input.CopyToAsync(output, cancellationToken);
            }
        }
    }

    internal static int AllocatePort(int preferred, AppSettings settings, Func<int, bool>? isAvailable = null)
    {
        const int first = 9000, last = 49151;
        var used = settings.PhpInstallations.Select(p => p.Port).Concat(settings.Sites.Select(s => s.Port))
            .Append(settings.SmtpPort).Append(settings.MailpitHttpPort).ToHashSet();
        var start = Math.Clamp(preferred, first, last);
        isAvailable ??= RuntimePaths.IsPortAvailable;
        for (var offset = 0; offset <= last - first; offset++)
        {
            var port = first + (start - first + offset) % (last - first + 1);
            if (!used.Contains(port) && isAvailable(port)) return port;
        }
        throw new InvalidOperationException("No available loopback FastCGI port was found between 9000 and 49151. Check Windows TCP exclusions and other running applications.");
    }

    private void ValidateSettings(AppSettings settings)
    {
        if (settings.SchemaVersion != 1) throw new InvalidDataException("Unsupported settings schema. The file was preserved.");
        if (!Enum.IsDefined(settings.Theme)) throw new InvalidDataException("Select a valid appearance: System, Light, or Dark.");
        RuntimePaths.ValidatePort(settings.SmtpPort);
        RuntimePaths.ValidatePort(settings.MailpitHttpPort);
        if (settings.PhpInstallations.GroupBy(p => p.Channel).Any(g => g.Count() > 1)) throw new InvalidDataException("Multiple active PHP installations in one channel are not supported by this milestone.");
        if (settings.PhpInstallations.Count > 0 && !settings.PhpInstallations.Any(p => p.Channel == settings.DefaultPhpChannel)) throw new InvalidDataException("Installed PHP requires an installed default channel.");
        if (settings.PhpInstallations.Count == 0 && settings.DefaultPhpChannel is not null) throw new InvalidDataException("The default PHP channel is not installed.");
        if (settings.PathIntegrationEnabled && settings.DefaultPhpChannel is null) throw new InvalidDataException("PATH integration requires an installed default PHP channel.");
        if (settings.RetainedPhpVersions.GroupBy(version => (version.Channel, version.Version)).Any(group => group.Count() > 1))
            throw new InvalidDataException("Retained PHP patch versions must be unique within each channel.");
        foreach (var retained in settings.RetainedPhpVersions)
        {
            if (!Regex.IsMatch(retained.Version, @"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant) ||
                retained.Channel != retained.Version[..retained.Version.LastIndexOf('.')])
                throw new InvalidDataException("Retained PHP version metadata is invalid.");
            var active = settings.PhpInstallations.SingleOrDefault(item => item.Channel == retained.Channel)
                ?? throw new InvalidDataException("A retained PHP version requires an installed active channel.");
            if (active.Version == retained.Version || SameDirectory(active.Directory, retained.Directory))
                throw new InvalidDataException("An active PHP patch cannot also be a retained version.");
            RequireManagedPhpDirectory(retained.Directory);
        }
    }

    private static void ValidateSourceIncludes(string text, string[] allowed, string source, bool allowAbsoluteFiles = false)
    {
        foreach (var directive in ReadDirectives(text).Where(d => d[0].Value == "include"))
        {
            if (directive.Count == 2 && directive[1].Quoted && allowed.Contains(directive[1].Value, StringComparer.Ordinal)) continue;
            if (allowAbsoluteFiles && directive.Count == 2 && Path.IsPathFullyQualified(directive[1].Value.Replace('/', Path.DirectorySeparatorChar))) continue;
            throw new InvalidDataException(allowAbsoluteFiles
                ? $"{source}: site include directives must name an absolute file path. Nested includes use normal Nginx syntax and are validated by nginx -t. The source was preserved."
                : $"{source}: the managed root accepts only its required include markers. The source was preserved.");
        }
    }

    private static void ValidateLoopbackListeners(string text, string source)
    {
        foreach (var directive in ReadDirectives(text).Where(d => d[0].Value == "listen"))
            if (directive.Count is < 2 or > 3 || !Regex.IsMatch(directive[1].Value, @"^127\.0\.0\.1:\d+$", RegexOptions.CultureInvariant)
                || (directive.Count == 3 && directive[2].Value != "default_server"))
                throw new InvalidDataException($"{source}: this milestone accepts explicit IPv4 loopback listeners only.");
    }

    private static void ValidateSiteSourceMatches(string text, SiteDefinition site, string source)
    {
        var directives = ReadDirectives(text);
        var roots = directives.Where(d => d[0].Value == "root").ToArray();
        var hosts = directives.Where(d => d[0].Value == "server_name").ToArray();
        var listeners = directives.Where(d => d[0].Value == "listen").ToArray();
        if (roots.Length != 1 || roots[0].Count != 2 || hosts.Length != 1 || hosts[0].Count != 2 || listeners.Length != 1 || listeners[0].Count != 2
            || !Path.IsPathFullyQualified(roots[0][1].Value)
            || !string.Equals(Path.GetFullPath(roots[0][1].Value).TrimEnd('\\', '/'), Path.GetFullPath(site.DocumentRoot).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(hosts[0][1].Value, site.Hostname, StringComparison.OrdinalIgnoreCase)
            || listeners[0][1].Value != $"127.0.0.1:{site.Port}")
            throw new InvalidDataException($"{source}: the preserved source's hostname, root, or listener differs from the site metadata, or is ambiguous. This milestone cannot reconcile those edits automatically. Restore the matching source or create a new site ID; the existing file was not overwritten.");
    }

    // Only directive boundaries are interpreted. Comments and quoted arguments are never reinterpreted as executable directives.
    private static List<List<NginxToken>> ReadDirectives(string text)
    {
        var result = new List<List<NginxToken>>();
        var current = new List<NginxToken>();
        for (var index = 0; index < text.Length;)
        {
            var character = text[index];
            if (char.IsWhiteSpace(character)) { index++; continue; }
            if (character == '#') { while (index < text.Length && text[index] != '\n') index++; continue; }
            if (character is '{' or '}') { current = []; index++; continue; }
            if (character == ';')
            {
                if (current.Count > 0) result.Add(current);
                current = [];
                index++;
                continue;
            }
            var quoted = character is '\'' or '"';
            var quote = quoted ? character : '\0';
            if (quoted) index++;
            var value = new StringBuilder();
            var closed = !quoted;
            while (index < text.Length)
            {
                character = text[index];
                if (quoted && character == quote) { index++; closed = true; break; }
                if (!quoted && (char.IsWhiteSpace(character) || character is ';' or '{' or '}' or '#')) break;
                if (character == '\\' && index + 1 < text.Length)
                {
                    var next = text[index + 1];
                    if (next is '\\' or '\'' or '"' || char.IsWhiteSpace(next) || next is ';' or '{' or '}' or '#')
                    { value.Append(next); index += 2; continue; }
                }
                value.Append(character);
                index++;
            }
            if (!closed) throw new InvalidDataException("An Nginx source contains an unterminated quoted argument.");
            current.Add(new(value.ToString(), quoted));
        }
        return result;
    }

    private sealed record NginxToken(string Value, bool Quoted);

    private static async Task WriteIfChangedAsync(string path, string content, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || !string.Equals(await File.ReadAllTextAsync(path, cancellationToken), content, StringComparison.Ordinal))
            await AtomicJsonStore.WriteTextAsync(path, content, cancellationToken);
    }

    private void EnsureMailpitOwned()
    {
        if (!_supervisor.IsRunning("mailpit")) throw new InvalidOperationException("Start the managed Mail Trap before opening its inbox.");
    }

    private static Uri MailUri(AppSettings settings, string path) => new($"http://127.0.0.1:{settings.MailpitHttpPort}{path}");
    private static string Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string Address(JsonElement item) => string.IsNullOrWhiteSpace(Text(item, "Name")) ? Text(item, "Address") : $"{Text(item, "Name")} <{Text(item, "Address")}>";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static int Count(string text, string value) => (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;
    private sealed record PhpConfiguration(string PrimaryIni, string ScanDirectory);
    private sealed record CliSelection(string? ExecutablePath, string? PrimaryIni, string? ScanDirectory, string? Channel);

    private static void BroadcastEnvironmentChange()
    {
        _ = SendMessageTimeout(new nint(0xffff), 0x001A, 0, "Environment", 0x0002, 1000, out _);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(nint window, uint message, nuint wParam, string lParam, uint flags, uint timeout, out nuint result);

    [GeneratedRegex(@"\bPHP\s+(\d+\.\d+\.\d+)", RegexOptions.CultureInvariant)] private static partial Regex PhpVersion();
    [GeneratedRegex(@"nginx/(\d+\.\d+\.\d+)", RegexOptions.CultureInvariant)] private static partial Regex NginxVersion();
    [GeneratedRegex(@"v?(\d+\.\d+\.\d+)", RegexOptions.CultureInvariant)] private static partial Regex MailpitVersion();
    [GeneratedRegex(@"^[a-zA-Z0-9_]+$", RegexOptions.CultureInvariant)] private static partial Regex ExtensionName();

    private const string FastCgiParameters = "fastcgi_param QUERY_STRING $query_string;\nfastcgi_param REQUEST_METHOD $request_method;\nfastcgi_param CONTENT_TYPE $content_type;\nfastcgi_param CONTENT_LENGTH $content_length;\nfastcgi_param SCRIPT_NAME $fastcgi_script_name;\nfastcgi_param REQUEST_URI $request_uri;\nfastcgi_param DOCUMENT_URI $document_uri;\nfastcgi_param DOCUMENT_ROOT $document_root;\nfastcgi_param SERVER_PROTOCOL $server_protocol;\nfastcgi_param REQUEST_SCHEME $scheme;\nfastcgi_param HTTPS $https if_not_empty;\nfastcgi_param GATEWAY_INTERFACE CGI/1.1;\nfastcgi_param SERVER_SOFTWARE nginx/$nginx_version;\nfastcgi_param REMOTE_ADDR $remote_addr;\nfastcgi_param REMOTE_PORT $remote_port;\nfastcgi_param SERVER_ADDR $server_addr;\nfastcgi_param SERVER_PORT $server_port;\nfastcgi_param SERVER_NAME $server_name;\nfastcgi_param REDIRECT_STATUS 200;\n";
}
