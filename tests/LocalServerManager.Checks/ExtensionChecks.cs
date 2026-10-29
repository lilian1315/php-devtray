using System.Text;
using System.Text.RegularExpressions;
using LocalServerManager.Core;
using LocalServerManager.Infrastructure.Runtimes;

namespace LocalServerManager.Checks;

internal static class ExtensionChecks
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LocalServerManager.ExtensionChecks." + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            await using var runner = new ExtensionFixtureSupervisor();
            var distribution = Path.Combine(root, "runtimes", "php", "8.4.18");
            Directory.CreateDirectory(Path.Combine(distribution, "ext"));
            var ownExecutable = Environment.ProcessPath ?? throw new InvalidOperationException("No fixture executable.");
            File.Copy(ownExecutable, Path.Combine(distribution, "php.exe"));
            File.Copy(ownExecutable, Path.Combine(distribution, "php-cgi.exe"));
            var primary = Path.Combine(distribution, "php.ini-development");
            const string stock = "; Stock configuration must remain unchanged.\n;extension=gd\n";
            await File.WriteAllTextAsync(primary, stock);
            var gdDll = Path.Combine(distribution, "ext", "php_gd.dll");
            await File.WriteAllTextAsync(gdDll, "fixture-compatible-dll");
            await File.WriteAllTextAsync(Path.Combine(distribution, "ext", "php_opcache.dll"), "fixture-compatible-dll");
            var installation = new PhpInstallation { Id = "php:8.4", Version = "8.4.18", Channel = "8.4", Directory = distribution, Port = 9084 };
            var settings = new AppSettings { PhpInstallations = [installation], DefaultPhpChannel = "8.4" };
            var catalog = new RuntimeCatalog(root, runner);
            var userDirectory = Path.Combine(root, "user", "php", "8.4", "conf.d");
            Directory.CreateDirectory(userDirectory);
            var custom = Path.Combine(userDirectory, "20-custom.ini");
            const string customText = "; User-owned overrides\nmemory_limit=192M\n";
            await File.WriteAllTextAsync(custom, customText);

            var baseline = await catalog.BuildPhpAsync(installation, settings);
            var baselineResolved = baseline.Command.EnvironmentVariables["PHP_INI_SCAN_DIR"];
            var originalResolved = await File.ReadAllTextAsync(Path.Combine(baselineResolved, "10-extensions.ini"));
            var originalProjection = await File.ReadAllTextAsync(Path.Combine(userDirectory, "10-extensions.ini"));
            var inventory = await catalog.GetPhpExtensionsAsync(installation, settings);
            Check(inventory.Single(extension => extension.Name == "json") is { IsBuiltIn: true, IsEnabled: true }, "Built-in inventory must come from -n -m.");
            Check(inventory.Single(extension => extension.Name == "gd") is { IsBuiltIn: false, IsAvailable: true, IsEnabled: false }, "Bundled DLL must be available and disabled until configured.");
            Check(inventory.Single(extension => extension.Name == "opcache") is { IsAvailable: true, IsEnabled: false }, "Bundled Zend extension must have a canonical inventory name.");
            await RejectAsync(() => catalog.PrepareExtensionChangeAsync(installation, settings, "json", false), "built into");
            await RejectAsync(() => catalog.PrepareExtensionChangeAsync(installation, settings, "../gd", true), "filesystem path");
            Console.WriteLine("PASS: extension inventory distinguishes built-ins and bundled DLLs; built-in toggles and paths are refused.");

            var enabled = await catalog.PrepareExtensionChangeAsync(installation, settings, "GD", true);
            Check(enabled.EnabledExtensions.SequenceEqual(["gd"]), "Enabling must return canonical desired extension state.");
            Check((await catalog.GetPhpExtensionsAsync(enabled, settings)).Single(extension => extension.Name == "gd") is { IsEnabled: true, IsManaged: true }, "Enabled state must be confirmed by configured -m.");
            Check(await File.ReadAllTextAsync(custom) == customText && await File.ReadAllTextAsync(primary) == stock, "Inspection/change preparation overwrote a user or stock source.");
            Check(await File.ReadAllTextAsync(Path.Combine(userDirectory, "10-extensions.ini")) == originalProjection, "A prepared candidate must not publish a source projection.");
            Check(await File.ReadAllTextAsync(Path.Combine(baselineResolved, "10-extensions.ini")) == originalResolved, "A candidate mutated an old resolved generation.");
            Check(!File.Exists(Path.Combine(root, "settings.json")), "The extension backend must not persist application settings.");
            var disabled = await catalog.PrepareExtensionChangeAsync(enabled, settings, "gd", false);
            Check(!disabled.EnabledExtensions.Contains("gd") && !(await catalog.GetPhpExtensionsAsync(disabled, settings)).Single(extension => extension.Name == "gd").IsEnabled,
                "Disabling must verify the dynamic module is absent.");
            var zend = await catalog.PrepareExtensionChangeAsync(installation, settings, "Zend OPcache", true);
            Check((await catalog.GetPhpExtensionsAsync(zend, settings)).Single(extension => extension.Name == "opcache") is { IsEnabled: true, IsManaged: true }, "Zend OPcache must normalize and load through zend_extension.");
            Check(runner.SawZendOpcacheDirective, "Zend OPcache was not loaded using its required zend_extension directive.");
            Console.WriteLine("PASS: enable/disable and Zend extension semantics are validated without publishing settings or mutating old configuration.");

            const string external = "; Preserve this directive\nextension=\"php_gd.dll\"\n";
            await File.WriteAllTextAsync(custom, external);
            var externalInventory = await catalog.GetPhpExtensionsAsync(installation, settings);
            Check(externalInventory.Single(extension => extension.Name == "gd") is { IsEnabled: true, IsManaged: false, Error: not null }, "Externally configured modules must expose their source conflict.");
            await RejectAsync(() => catalog.PrepareExtensionChangeAsync(installation, settings, "gd", false), custom + ":2");
            await RejectAsync(() => catalog.PrepareExtensionChangeAsync(installation, settings, "gd", true), custom + ":2");
            Check(await File.ReadAllTextAsync(custom) == external, "Conflict handling rewrote the user's directive.");
            await File.WriteAllTextAsync(custom, "extension=gd\nextension=php_gd.dll\n");
            Check((await catalog.GetPhpExtensionsAsync(installation, settings)).Single(extension => extension.Name == "gd").Error?.Contains("already loaded", StringComparison.OrdinalIgnoreCase) == true,
                "Duplicate loading warnings must remain visible even when PHP returns exit zero.");
            await RejectAsync(() => catalog.PrepareExtensionChangeAsync(installation, settings, "gd", false), custom);
            await File.WriteAllTextAsync(custom, customText);
            await File.WriteAllTextAsync(primary, stock + "zend_extension=php_opcache.dll\n");
            await RejectAsync(() => catalog.PrepareExtensionChangeAsync(installation, settings, "opcache", false), primary + ":3");
            Check((await File.ReadAllTextAsync(primary)).Contains("zend_extension=php_opcache.dll", StringComparison.Ordinal), "Stock-source conflict handling changed the stock file.");
            await File.WriteAllTextAsync(primary, stock);
            Console.WriteLine("PASS: duplicate, user, and stock extension directives block unsafe toggles with original source locations.");

            await File.WriteAllTextAsync(gdDll, "fixture-broken-dll");
            await RejectAsync(() => catalog.PrepareExtensionChangeAsync(installation, settings, "gd", true), "Unable to load dynamic library");
            var broken = await catalog.GetPhpExtensionsAsync(enabled, settings);
            Check(broken.Single(extension => extension.Name == "gd") is { IsAvailable: true, IsEnabled: false, Error: not null }, "A failed DLL must be distinguished from an absent DLL.");
            await File.WriteAllTextAsync(gdDll, "fixture-compatible-dll");
            runner.SuppressModule = "gd";
            await RejectAsync(() => catalog.PrepareExtensionChangeAsync(installation, settings, "gd", true), "did not load");
            runner.SuppressModule = null;
            var missing = installation with { EnabledExtensions = ["missing"] };
            Check((await catalog.GetPhpExtensionsAsync(missing, settings)).Single(extension => extension.Name == "missing") is { IsAvailable: false, IsEnabled: false, Error: not null },
                "A desired missing DLL must remain visible as an inventory error.");
            await File.WriteAllTextAsync(custom, "extension=${LSM_EXTENSION_UNDEFINED_8C29D1}\n");
            await RejectAsync(() => catalog.PrepareExtensionChangeAsync(installation, settings, "gd", false), custom + ":1");
            Console.WriteLine("PASS: broken/missing DLLs, silent load failures, startup warnings and unresolved directives cannot commit a toggle.");
        }
        finally
        {
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (root.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("LocalServerManager.ExtensionChecks.", StringComparison.Ordinal))
                Directory.Delete(root, true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task RejectAsync(Func<Task> operation, string messageFragment)
    {
        try { await operation(); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or ArgumentException or FileNotFoundException)
        {
            Check(error.Message.Contains(messageFragment, StringComparison.OrdinalIgnoreCase), "Unexpected extension rejection: " + error.Message);
            return;
        }
        throw new InvalidOperationException("An unsafe extension change was accepted.");
    }

    // A controlled PHP command boundary lets policy checks exercise startup diagnostics,
    // missing dependencies and Zend semantics independently of whatever PHP is installed on the machine.
    private sealed class ExtensionFixtureSupervisor : IProcessSupervisor
    {
        public event EventHandler<ServiceSnapshot>? ServiceChanged { add { } remove { } }
        public IReadOnlyList<ServiceSnapshot> Snapshots => [];
        public bool SawZendOpcacheDirective { get; private set; }
        public string? SuppressModule { get; set; }
        public bool IsRunning(string id) => false;
        public Task StartAsync(ManagedServiceDefinition definition, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync(string id, TimeSpan timeout, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAllAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task<ProcessResult> RunAsync(ProcessCommand command, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Check(command.Arguments.Contains("-m") || command.Arguments.Contains("--ini"), "Unexpected fixture command.");
            var modules = new HashSet<string>(["Core", "json", "standard", "PDO"], StringComparer.OrdinalIgnoreCase);
            var errors = new StringBuilder();
            if (!command.Arguments.Contains("-n"))
            {
                var iniIndex = command.Arguments.ToList().IndexOf("-c");
                var sourceFiles = new List<string> { command.Arguments[iniIndex + 1] };
                sourceFiles.AddRange(Directory.GetFiles(command.EnvironmentVariables["PHP_INI_SCAN_DIR"], "*.ini").Order(StringComparer.Ordinal));
                foreach (var file in sourceFiles)
                {
                    foreach (var line in await File.ReadAllLinesAsync(file, cancellationToken))
                    {
                        var directive = Regex.Match(line, "^\\s*(extension|zend_extension)\\s*=\\s*\"?([^\";]+)\"?", RegexOptions.IgnoreCase);
                        if (!directive.Success) continue;
                        var raw = directive.Groups[2].Value.Trim().Replace('\\', '/');
                        var name = raw[(raw.LastIndexOf('/') + 1)..];
                        if (name.StartsWith("php_", StringComparison.OrdinalIgnoreCase)) name = name[4..];
                        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                        var dll = Path.Combine(command.WorkingDirectory!, "ext", "php_" + name + ".dll");
                        if (!File.Exists(dll) || (await File.ReadAllTextAsync(dll, cancellationToken)).Contains("broken", StringComparison.Ordinal))
                        { errors.AppendLine($"PHP Warning: PHP Startup: Unable to load dynamic library '{raw}' (dependency missing)"); continue; }
                        if (name.Equals("opcache", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!directive.Groups[1].Value.Equals("zend_extension", StringComparison.OrdinalIgnoreCase))
                            { errors.AppendLine("PHP Warning: OPcache must be loaded as a Zend extension"); continue; }
                            SawZendOpcacheDirective = true;
                            name = "Zend OPcache";
                        }
                        if (name.Equals(SuppressModule, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!modules.Add(name)) errors.AppendLine($"PHP Warning: Module '{name}' is already loaded");
                    }
                }
            }
            var output = command.Arguments.Contains("-m")
                ? "[PHP Modules]\n" + string.Join('\n', modules) + "\n\n[Zend Modules]\n" + (modules.Contains("Zend OPcache") ? "Zend OPcache\n" : "")
                : "Configuration File (php.ini) Path: fixture\n";
            return new ProcessResult(0, output, errors.ToString());
        }
    }
}
