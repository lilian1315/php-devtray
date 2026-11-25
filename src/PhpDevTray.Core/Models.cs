namespace PhpDevTray.Core;

public enum ServiceKind { Php, Mailpit, Caddy, FrankenPhp }
public enum ServiceStatus { NotInstalled, Stopped, Starting, Running, Stopping, Restarting, Updating, Failed }
public enum HealthCheckKind { Process, Tcp, FastCgi, Http, Smtp }
public enum ThemePreference { System, Light, Dark }

public sealed record HealthEndpoint(HealthCheckKind Kind, string Host, int Port, string? Path = null);

public sealed record ProcessCommand
{
    public required string ExecutablePath { get; init; }
    public string? WorkingDirectory { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();
}

public sealed class VisualCppRuntimeException(string installed, string required) : InvalidOperationException(
    $"PHP requires the Microsoft Visual C++ x64 runtime {required} or newer. Windows has {installed}. Install the latest x64 redistributable, then retry PHP installation.")
{
    public const string DownloadUrl = "https://aka.ms/vc14/vc_redist.x64.exe";
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed record ManagedServiceDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required ServiceKind Kind { get; init; }
    public required ProcessCommand Command { get; init; }
    public IReadOnlyList<HealthEndpoint> HealthEndpoints { get; init; } = [];
    public ProcessCommand? GracefulStopCommand { get; init; }
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public string? LogDirectory { get; init; }
    public string? Version { get; init; }
}

public sealed record ServiceSnapshot
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required ServiceKind Kind { get; init; }
    public ServiceStatus Status { get; init; } = ServiceStatus.Stopped;
    public string? Detail { get; init; }
    public string? Version { get; init; }
    public string? Endpoint { get; init; }
    public int? ProcessId { get; init; }
    public bool IsDefault { get; init; }
}

public sealed record PhpInstallation
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string Channel { get; init; }
    public required string Directory { get; init; }
    public int Port { get; init; }
    public bool MailIntegrationEnabled { get; init; } = true;
    public bool AutoStart { get; init; } = true;
    public IReadOnlyList<string> EnabledExtensions { get; init; } = [];
}

/// <summary>A previously activated binary distribution; channel policy remains on the active installation.</summary>
public sealed record PhpRuntimeVersion
{
    public required string Channel { get; init; }
    public required string Version { get; init; }
    public required string Directory { get; init; }
}

public sealed record FrankenRuntime(string Id, string Executable, int Port, [property: System.Text.Json.Serialization.JsonRequired] string PhpVersion, [property: System.Text.Json.Serialization.JsonRequired] string Version)
{
    public IReadOnlyList<string> EnabledExtensions { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public string Label => $"FrankenPHP {Version} · PHP {PhpVersion}";
}
public sealed record WebConfiguration(string Executable, FrankenRuntime[] Runtimes, WebSite[] Sites);

public sealed record AppSettings
{
    public ThemePreference Theme { get; init; } = ThemePreference.System;
    public WebConfiguration Web { get; init; } = new("", [], []);
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<PhpInstallation> PhpInstallations { get; init; } = [];
    public IReadOnlyList<PhpRuntimeVersion> RetainedPhpVersions { get; init; } = [];
    public string? DefaultPhpChannel { get; init; }
    public bool PathIntegrationEnabled { get; init; }
    public string? MailpitExecutable { get; init; }
    public int SmtpPort { get; init; } = 39327;
    public int MailpitHttpPort { get; init; } = 8025;
    public string LastSection { get; init; } = "Caddy";
    public bool StartServicesOnLaunch { get; init; }
}

public sealed record WebSite(string Hostname, string DocumentRoot, string RuntimeId, bool Enabled = true)
{
    public string? CaddyfileKey { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string CaddyfileName => $"site-{(CaddyfileKey ?? Hostname).ToLowerInvariant()}.Caddyfile";
}

public sealed record PhpExtensionChange(string Name, bool Enabled);

public sealed record EnvironmentSnapshot
{
    public ThemePreference Theme { get; init; } = ThemePreference.System;
    public required string DataDirectory { get; init; }
    public IReadOnlyDictionary<string, string?> SiteRoots { get; init; } = new Dictionary<string, string?>();
    public IReadOnlyList<WebSite> Sites { get; init; } = [];
    public IReadOnlyList<ServiceSnapshot> Services { get; init; } = [];
    public IReadOnlyList<PhpInstallation> PhpInstallations { get; init; } = [];
    public IReadOnlyList<PhpRuntimeVersion> RetainedPhpVersions { get; init; } = [];
    public string? DefaultPhpChannel { get; init; }
    public bool PathIntegrationEnabled { get; init; }
    public string? LastError { get; init; }
    public string LastSection { get; init; } = "Caddy";
    public bool IsBusy { get; init; }
    public int SmtpPort { get; init; } = 39327;
    public int MailpitHttpPort { get; init; } = 8025;
}

public sealed record RuntimeRelease
{
    public long ArchiveBytes { get; init; }
    public required string Id { get; init; }
    public required ServiceKind Kind { get; init; }
    public required string Version { get; init; }
    public string? Channel { get; init; }
    public required string DisplayName { get; init; }
    public required Uri DownloadUri { get; init; }
    public string? Sha256 { get; init; }
    public string? IntegrityDescription { get; init; }
}

public sealed record RuntimeInstallProgress(string Phase, long BytesReceived = 0, long? TotalBytes = null);

public sealed record PhpExtensionInfo
{
    public required string Name { get; init; }
    public bool IsBuiltIn { get; init; }
    public bool IsAvailable { get; init; }
    public bool IsEnabled { get; init; }
    public bool IsManaged { get; init; }
    public string? Error { get; init; }
}
