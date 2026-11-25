using PhpDevTray.Core;

namespace PhpDevTray.Infrastructure.Runtimes;

public sealed partial class RuntimeCatalog
{
    internal static PhpInstallation FrankenInstallation(FrankenRuntime runtime) => new()
    {
        Id = "franken-" + runtime.Id, Channel = "franken-" + runtime.Id,
        Directory = Path.GetDirectoryName(runtime.Executable)!, Version = runtime.PhpVersion, Port = runtime.Port,
        EnabledExtensions = runtime.EnabledExtensions
    };

    internal async Task<IReadOnlyDictionary<string, string>> FrankenEnvironmentAsync(FrankenRuntime runtime, AppSettings settings, CancellationToken token)
    {
        var installation = FrankenInstallation(runtime);
        var configuration = await PreparePhpConfigurationAsync(installation, settings, token, publish: false);
        var modules = await ReadExtensionModulesAsync(installation, configuration, token);
        if (ExtensionDiagnostics(modules.Result) is { } error) throw new InvalidDataException(error);
        foreach (var extension in runtime.EnabledExtensions)
            if (!modules.Names.Contains(NormalizeExtensionName(extension))) throw new InvalidDataException($"The {extension} extension failed to load.");
        return new Dictionary<string, string>(PhpEnvironment(configuration.ScanDirectory)) { ["PHPRC"] = configuration.PrimaryIni };
    }
}
