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
        foreach (var relative in new[] { "bin", "runtimes/php", "runtimes/mailpit", "user/php",
            "data/mailpit", "data/state", "logs/application",
            "logs/php", "logs/mailpit", "downloads", "backups" })
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

    public async Task<PhpInstallation> ImportPhpAsync(string phpExecutable, AppSettings settings, CancellationToken cancellationToken = default, IReadOnlySet<int>? reservedPorts = null)
    {
        var source = Path.GetFullPath(phpExecutable);
        if (!string.Equals(Path.GetFileName(source), "php.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select php.exe from an extracted official Windows x64 NTS distribution.");
        RuntimePaths.VerifyExecutable(source, true);
        var sourceDirectory = Path.GetDirectoryName(source)!;
        RuntimePaths.VerifyExecutable(Path.Combine(sourceDirectory, "php-cgi.exe"), true);
        if (!File.Exists(Path.Combine(sourceDirectory, "php.ini-development")))
            throw new InvalidDataException("Select a complete PHP distribution containing php.ini-development.");
        var result = await CheckPhpCliAsync(sourceDirectory, cancellationToken);
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
        var port = active?.Port ?? AllocatePort(9000 + int.Parse(channel.Replace(".", "")), settings,
            p => reservedPorts?.Contains(p) != true && RuntimePaths.IsPortAvailable(p));
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
        if (installation.Id.StartsWith("franken-", StringComparison.Ordinal))
        {
            var cache = Path.Combine(_root, "data", "opcache", installation.Id);
            Directory.CreateDirectory(cache);
            managed += $"opcache.file_cache={RuntimePaths.IniQuote(cache)}\nopcache.file_cache_fallback=1\nopcache.cache_id={installation.Id}\n";
        }
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

    internal Task<ProcessResult> CheckPhpCliAsync(string directory, CancellationToken token) =>
        RunCheckedAsync(Path.Combine(directory, "php.exe"), ["-n", "-v"], directory, token);

    private async Task<ProcessResult> RunCheckedAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await _supervisor.RunAsync(new ProcessCommand { ExecutablePath = executable, WorkingDirectory = workingDirectory, Arguments = arguments }, TimeSpan.FromSeconds(15), cancellationToken);
        if (result.ExitCode != 0)
        {
            var mismatch = Regex.Match(result.StandardError + "\n" + result.StandardOutput,
                @"VCRUNTIME140\.dll'\s+([\d.]+) is not compatible with this PHP build linked with ([\d.]+)", RegexOptions.IgnoreCase);
            if (mismatch.Success) throw new VisualCppRuntimeException(mismatch.Groups[1].Value, mismatch.Groups[2].Value);
            throw new InvalidOperationException($"{Path.GetFileName(executable)} exited with code {result.ExitCode}. {result.StandardError} {result.StandardOutput}");
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> PhpEnvironment(string scanDirectory) => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["PHP_INI_SCAN_DIR"] = scanDirectory, ["PHPRC"] = "", ["PHP_FCGI_MAX_REQUESTS"] = "0"
    };

    internal static async Task CopyDistributionAsync(string source, string destination, CancellationToken cancellationToken)
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
        var used = settings.PhpInstallations.Select(p => p.Port)
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
    private sealed record PhpConfiguration(string PrimaryIni, string ScanDirectory);
    private sealed record CliSelection(string? ExecutablePath, string? PrimaryIni, string? ScanDirectory, string? Channel);

    private static void BroadcastEnvironmentChange()
    {
        _ = SendMessageTimeout(new nint(0xffff), 0x001A, 0, "Environment", 0x0002, 1000, out _);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(nint window, uint message, nuint wParam, string lParam, uint flags, uint timeout, out nuint result);

    [GeneratedRegex(@"\bPHP\s+(\d+\.\d+\.\d+)", RegexOptions.CultureInvariant)] private static partial Regex PhpVersion();
    [GeneratedRegex(@"v?(\d+\.\d+\.\d+)", RegexOptions.CultureInvariant)] private static partial Regex MailpitVersion();
    [GeneratedRegex(@"^[a-zA-Z0-9_]+$", RegexOptions.CultureInvariant)] private static partial Regex ExtensionName();

}
