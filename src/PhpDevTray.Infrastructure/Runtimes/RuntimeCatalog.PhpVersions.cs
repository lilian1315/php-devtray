using System.Text.RegularExpressions;
using PhpDevTray.Core;
using PhpDevTray.Infrastructure.Configuration;
using PhpDevTray.Infrastructure.Storage;

namespace PhpDevTray.Infrastructure.Runtimes;

public sealed partial class RuntimeCatalog
{
    /// <summary>Publishes the exact configuration that passed candidate validation; never rereads user INI sources.</summary>
    public async Task PublishPhpCandidateAsync(PhpInstallation installation, ManagedServiceDefinition prepared, AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (prepared.Id != installation.Id || prepared.Kind != ServiceKind.Php || prepared.Version != installation.Version ||
            !string.Equals(Path.GetFullPath(prepared.Command.ExecutablePath), Path.GetFullPath(Path.Combine(installation.Directory, "php-cgi.exe")), StringComparison.OrdinalIgnoreCase) ||
            !prepared.Command.EnvironmentVariables.TryGetValue("PHP_INI_SCAN_DIR", out var scanDirectory))
            throw new InvalidDataException("The prepared PHP definition does not match the installation being activated.");
        var snapshotRoot = Path.GetFullPath(Path.Combine(_root, "data", "state", "php-config")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(scanDirectory).StartsWith(snapshotRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The prepared PHP configuration must be an immutable managed snapshot.");
        var extensions = await File.ReadAllTextAsync(Path.Combine(scanDirectory, "10-extensions.ini"), cancellationToken);
        var managed = await File.ReadAllTextAsync(Path.Combine(scanDirectory, "90-managed.ini"), cancellationToken);
        var published = Path.Combine(_root, "user", "php", installation.Channel, "conf.d");
        await WriteIfChangedAsync(Path.Combine(published, "10-extensions.ini"), extensions, cancellationToken);
        await WriteIfChangedAsync(Path.Combine(published, "90-managed.ini"), managed, cancellationToken);
        if (settings.DefaultPhpChannel == installation.Channel)
            await AtomicJsonStore.WriteAsync(Path.Combine(_root, "bin", "launcher.json"), new CliSelection(
                Path.Combine(installation.Directory, "php.exe"), Path.Combine(installation.Directory, "php.ini-development"), scanDirectory, installation.Channel), cancellationToken);
    }

    /// <summary>Validates a replacement without publishing managed INI files or changing CLI selection.</summary>
    public async Task<ManagedServiceDefinition> BuildPhpCandidateAsync(PhpInstallation installation, AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        RuntimePaths.ValidatePort(installation.Port);
        if (!Regex.IsMatch(installation.Version, @"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant) ||
            installation.Channel != installation.Version[..installation.Version.LastIndexOf('.')])
            throw new InvalidDataException("The PHP patch version must belong to its selected minor channel.");
        foreach (var executable in new[] { "php.exe", "php-cgi.exe" })
        {
            var path = Path.Combine(installation.Directory, executable);
            RuntimePaths.VerifyExecutable(path, true);
            var result = await RunCheckedAsync(path, ["-n", "-v"], installation.Directory, cancellationToken);
            var output = result.StandardOutput + result.StandardError;
            var version = PhpVersion().Match(output);
            if (!version.Success || version.Groups[1].Value != installation.Version || !output.Contains("NTS", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{executable} must report the expected PHP {installation.Version} x64 NTS build.");
            if (ExtensionDiagnostics(result) is { } diagnostics)
                throw new InvalidDataException($"{executable} failed clean startup validation: {diagnostics}");
        }
        var configuration = await PreparePhpConfigurationAsync(installation, settings, cancellationToken, publish: false);
        var loaded = await ReadExtensionModulesAsync(installation, configuration, cancellationToken);
        if (ExtensionDiagnostics(loaded.Result) is { } failure)
            throw new InvalidDataException("The replacement failed PHP module validation. " + failure);
        foreach (var extension in installation.EnabledExtensions.Select(NormalizeExtensionName))
            if (!loaded.Names.Contains(extension))
                throw new InvalidDataException($"The enabled {extension} extension did not load in PHP {installation.Version}. The active patch was preserved.");
        RuntimePaths.ValidatePort(installation.Port);
        return CreatePhpDefinition(installation, configuration);
    }

    /// <summary>Rejects retained paths outside managed storage or traversing symbolic links/junctions.</summary>
    public void ValidateRetainedPhpVersion(PhpRuntimeVersion version)
    {
        if (!Regex.IsMatch(version.Version, @"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant) ||
            version.Channel != version.Version[..version.Version.LastIndexOf('.')])
            throw new InvalidDataException("A retained PHP version has invalid channel/version metadata.");
        RequireManagedPhpDirectory(version.Directory);
        if (!Directory.Exists(version.Directory)) throw new DirectoryNotFoundException($"The retained PHP {version.Version} distribution is missing.");
    }

    /// <summary>Only cleans a fresh, unreferenced managed distribution. Existing active/retained files are never removed.</summary>
    public void DeleteUnreferencedPhpDirectory(string directory, AppSettings settings)
    {
        var candidate = RequireManagedPhpDirectory(directory);
        if (settings.PhpInstallations.Any(item => SameDirectory(item.Directory, candidate)) ||
            settings.RetainedPhpVersions.Any(item => SameDirectory(item.Directory, candidate))) return;
        if (Directory.Exists(candidate)) Directory.Delete(candidate, true);
    }

    private string RequireManagedPhpDirectory(string directory)
    {
        var root = Path.GetFullPath(Path.Combine(_root, "runtimes", "php"));
        var candidate = Path.GetFullPath(directory);
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PHP activation and cleanup require a directory inside managed PHP runtime storage.");
        // Check existing ancestors as well as the leaf: lexical containment alone is insufficient for a junction.
        for (var current = new DirectoryInfo(candidate); current is not null &&
             current.FullName.Length >= _root.Length; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Managed PHP runtime paths cannot traverse a symbolic link or junction.");
        return candidate;
    }

    private static bool SameDirectory(string first, string second) => string.Equals(
        Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}
