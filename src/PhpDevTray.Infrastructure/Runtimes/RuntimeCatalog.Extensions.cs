using System.Text.RegularExpressions;
using PhpDevTray.Core;

namespace PhpDevTray.Infrastructure.Runtimes;

public sealed partial class RuntimeCatalog
{
    /// <summary>Inspects the selected distribution without publishing new desired state or modifying user INI sources.</summary>
    public async Task<IReadOnlyList<PhpExtensionInfo>> GetPhpExtensionsAsync(PhpInstallation installation, AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var baseline = await ReadExtensionModulesAsync(installation, null, cancellationToken);
        if (ExtensionDiagnostics(baseline.Result) is { } baselineFailure)
            throw new InvalidDataException("PHP could not enumerate its built-in modules with -n: " + baselineFailure);

        var configuration = await PreparePhpConfigurationAsync(installation, settings, cancellationToken, validate: false, publish: false);
        var configured = await ReadExtensionModulesAsync(installation, configuration, cancellationToken);
        var diagnostics = ExtensionDiagnostics(configured.Result);
        var available = AvailableExtensions(installation);
        var desired = installation.EnabledExtensions.Select(NormalizeExtensionName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var directives = await ReadUserExtensionDirectivesAsync(installation, cancellationToken);
        var names = baseline.Names.Concat(configured.Names).Concat(available).Concat(desired)
            .Concat(directives.Where(directive => directive.Name is not null).Select(directive => directive.Name!))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
        var result = new List<PhpExtensionInfo>();
        foreach (var name in names)
        {
            var builtIn = baseline.Names.Contains(name);
            var isAvailable = builtIn || available.Contains(name);
            var enabled = configured.Names.Contains(name);
            var sources = directives.Where(directive => string.Equals(directive.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            var errors = new List<string>();
            if (desired.Contains(name) && !builtIn && !isAvailable) errors.Add("The configured extension DLL is missing from this installation.");
            if (desired.Contains(name) && !enabled) errors.Add("The requested extension is not loaded by the configured PHP process.");
            if (desired.Contains(name) && builtIn) errors.Add("This module is built in and cannot have a managed loading directive.");
            if (sources.Length > 0)
                errors.Add("Loading directive controlled outside the manager: " + string.Join(", ", sources.Select(SourceLocation)) + ". Edit that source before changing this extension.");
            if (diagnostics is not null) errors.Add("PHP configuration diagnostics: " + diagnostics);
            var unresolved = directives.Where(directive => directive.Name is null).ToArray();
            if (unresolved.Length > 0)
                errors.Add("An extension directive could not be resolved safely: " + string.Join(", ", unresolved.Select(SourceLocation)) + ".");
            result.Add(new PhpExtensionInfo
            {
                Name = name, IsBuiltIn = builtIn, IsAvailable = isAvailable, IsEnabled = enabled,
                IsManaged = desired.Contains(name), Error = errors.Count == 0 ? null : string.Join(" ", errors)
            });
        }
        return result;
    }

    /// <summary>Validates a desired extension change in a separate immutable configuration; caller owns persistence and restart.</summary>
    public async Task<PhpInstallation> PrepareExtensionChangeAsync(PhpInstallation installation, AppSettings settings,
        string extension, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        if (extension.Contains('/') || extension.Contains('\\'))
            throw new ArgumentException("Select an extension name, not a filesystem path.", nameof(extension));
        var name = NormalizeExtensionName(extension);
        if (!ExtensionName().IsMatch(name)) throw new ArgumentException("Select an extension from the bundled extension inventory.", nameof(extension));
        var inventory = await GetPhpExtensionsAsync(installation, settings, cancellationToken);
        var target = inventory.SingleOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"The {name} extension is not part of this PHP installation.");
        if (target.IsBuiltIn) throw new InvalidOperationException($"{name} is built into PHP and cannot be enabled or disabled.");
        if (enabled && !target.IsAvailable) throw new FileNotFoundException($"The {name} extension DLL is unavailable in PHP {installation.Version}.");

        var directives = await ReadUserExtensionDirectivesAsync(installation, cancellationToken);
        var conflicts = directives.Where(directive => directive.Name is null || string.Equals(directive.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (conflicts.Length > 0)
            throw new InvalidOperationException($"Cannot {(enabled ? "enable" : "disable")} {name} safely. Extension loading is also controlled by " +
                string.Join(", ", conflicts.Select(SourceLocation)) + ". Remove or change that directive in its original source, then retry. User configuration was preserved.");

        var desired = installation.EnabledExtensions.Select(NormalizeExtensionName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (enabled) desired.Add(name); else desired.Remove(name);
        var candidate = installation with { EnabledExtensions = desired.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray() };
        var configuration = await PreparePhpConfigurationAsync(candidate, settings, cancellationToken, validate: true, publish: false);
        var loaded = await ReadExtensionModulesAsync(candidate, configuration, cancellationToken);
        if (ExtensionDiagnostics(loaded.Result) is { } failure)
            throw new InvalidDataException("The extension change failed PHP startup validation. " + failure);
        if (loaded.Names.Contains(name) != enabled)
            throw new InvalidDataException(enabled
                ? $"The {name} DLL did not load successfully. Its PHP API/build or dependencies may be incompatible."
                : $"The {name} extension remains loaded from another configuration source; the change was not applied.");
        foreach (var expected in desired)
            if (!loaded.Names.Contains(expected)) throw new InvalidDataException($"The desired {expected} extension did not load; the change was not applied.");
        return candidate;
    }

    private async Task<ExtensionModuleResult> ReadExtensionModulesAsync(PhpInstallation installation, PhpConfiguration? configuration,
        CancellationToken cancellationToken)
    {
        var command = new ProcessCommand
        {
            ExecutablePath = Path.Combine(installation.Directory, "php.exe"), WorkingDirectory = installation.Directory,
            Arguments = configuration is null ? ["-n", "-m"] : ["-c", configuration.PrimaryIni, "-m"],
            EnvironmentVariables = PhpEnvironment(configuration?.ScanDirectory ?? "")
        };
        var result = await _supervisor.RunAsync(command, TimeSpan.FromSeconds(15), cancellationToken);
        if (!result.StandardOutput.Contains("[PHP Modules]", StringComparison.OrdinalIgnoreCase) && ExtensionDiagnostics(result) is null)
            throw new InvalidDataException("PHP did not return the expected module inventory for -m.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inModuleSection = false;
        foreach (var line in result.StandardOutput.Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith('['))
            {
                inModuleSection = text.Equals("[PHP Modules]", StringComparison.OrdinalIgnoreCase) || text.Equals("[Zend Modules]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inModuleSection || text.Length == 0 || text.Contains(':')) continue;
            var name = NormalizeExtensionName(text);
            if (ExtensionName().IsMatch(name)) names.Add(name);
        }
        return new(result, names);
    }

    private static HashSet<string> AvailableExtensions(PhpInstallation installation)
    {
        var directory = Path.Combine(installation.Directory, "ext");
        return !Directory.Exists(directory) ? new(StringComparer.OrdinalIgnoreCase) : Directory.EnumerateFiles(directory, "*.dll")
            .Select(Path.GetFileName).Where(file => file is not null && file.StartsWith("php_", StringComparison.OrdinalIgnoreCase))
            .Select(file => NormalizeExtensionName(file!)).Where(name => ExtensionName().IsMatch(name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<ExtensionDirective>> ReadUserExtensionDirectivesAsync(PhpInstallation installation, CancellationToken cancellationToken)
    {
        var primary = Path.Combine(installation.Directory, "php.ini-development");
        var custom = Path.Combine(_root, "user", "php", installation.Channel, "conf.d", "20-custom.ini");
        var directives = ParseExtensionDirectives(await File.ReadAllTextAsync(primary, cancellationToken), primary);
        if (File.Exists(custom)) directives.AddRange(ParseExtensionDirectives(await File.ReadAllTextAsync(custom, cancellationToken), custom));
        return directives;
    }

    private static void ValidateManagedExtensionDirectives(PhpInstallation installation, string primaryText, string primaryPath, string customText, string customPath)
    {
        var desired = installation.EnabledExtensions.Select(NormalizeExtensionName).ToArray();
        if (desired.Distinct(StringComparer.OrdinalIgnoreCase).Count() != desired.Length)
            throw new InvalidDataException("Managed extension state contains duplicate loading directives.");
        var sources = ParseExtensionDirectives(primaryText, primaryPath).Concat(ParseExtensionDirectives(customText, customPath));
        var conflicts = sources.Where(source => desired.Length > 0 && (source.Name is null || desired.Contains(source.Name, StringComparer.OrdinalIgnoreCase))).ToArray();
        if (conflicts.Length > 0)
            throw new InvalidDataException("Managed extension loading conflicts with user or stock configuration: " + string.Join(", ", conflicts.Select(SourceLocation)) + ". The source files were preserved.");
    }

    private static List<ExtensionDirective> ParseExtensionDirectives(string content, string source)
    {
        var result = new List<ExtensionDirective>();
        var lineNumber = 0;
        foreach (var rawLine in content.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.TrimStart(' ', '\t', '\r', '\uFEFF');
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            var match = Regex.Match(line, @"^(?:zend_extension|extension)\s*=\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var value = ReadIniDirectiveValue(match.Groups[1].Value);
            var unresolved = false;
            value = Regex.Replace(value, @"\$\{([^}]+)\}", environment =>
            {
                var replacement = Environment.GetEnvironmentVariable(environment.Groups[1].Value);
                if (replacement is null) unresolved = true;
                return replacement ?? "";
            }, RegexOptions.CultureInvariant);
            var name = NormalizeExtensionName(value);
            result.Add(new(unresolved || !ExtensionName().IsMatch(name) ? null : name, source, lineNumber));
        }
        return result;
    }

    private static string ReadIniDirectiveValue(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return "";
        if (text[0] is '\'' or '"')
        {
            var quote = text[0];
            var end = 1;
            while (end < text.Length)
            {
                if (text[end] == quote && text[end - 1] != '\\') return text[1..end];
                end++;
            }
            return "";
        }
        var comment = text.IndexOf(';');
        return (comment < 0 ? text : text[..comment]).Trim();
    }

    private static string NormalizeExtensionName(string value)
    {
        value = value.Trim().Replace('\\', '/');
        if (value.Contains('/')) value = value[(value.LastIndexOf('/') + 1)..];
        if (value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        if (value.StartsWith("php_", StringComparison.OrdinalIgnoreCase)) value = value[4..];
        if (value.Equals("Zend OPcache", StringComparison.OrdinalIgnoreCase)) value = "opcache";
        return value.ToLowerInvariant();
    }

    private static string? ExtensionDiagnostics(ProcessResult result)
    {
        var errors = new List<string>();
        if (result.ExitCode != 0) errors.Add($"PHP exited with code {result.ExitCode}.");
        if (!string.IsNullOrWhiteSpace(result.StandardError)) errors.Add(result.StandardError.Trim());
        foreach (var line in result.StandardOutput.Split('\n'))
            if (Regex.IsMatch(line, @"(?:^|\s)(?:PHP\s+)?(?:Warning|Fatal error|Parse error|Notice|Deprecated)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                errors.Add(line.Trim());
        if (errors.Count == 0) return null;
        var text = string.Join(" ", errors.Distinct(StringComparer.Ordinal));
        return text.Length > 4096 ? text[..4096] : text;
    }

    private static string SourceLocation(ExtensionDirective directive) => $"{directive.Source}:{directive.Line}";
    private sealed record ExtensionDirective(string? Name, string Source, int Line);
    private sealed record ExtensionModuleResult(ProcessResult Result, HashSet<string> Names);
}
