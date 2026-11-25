using System.IO;
using Microsoft.Win32;
using PhpDevTray.Core;
using Windows.ApplicationModel;

namespace PhpDevTray.App;

internal sealed record StartupRegistrationState(bool Enabled, string? Notice = null);

internal interface IStartupRegistration
{
    Task<StartupRegistrationState> ReadAsync();
    Task<StartupRegistrationState> SetEnabledAsync(bool enabled);
}

// Windows owns this preference, so changes made in its Startup settings are respected.
internal sealed class StartupRegistration : IStartupRegistration
{
    internal const string TaskId = "PhpDevTrayStartup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PHP DevTray";
    private readonly string _dataDirectory;

    public StartupRegistration(string dataDirectory) => _dataDirectory = dataDirectory;

    public async Task<StartupRegistrationState> ReadAsync()
    {
        if (AppDeployment.IsPackaged)
            return FromState((await StartupTask.GetAsync(TaskId)).State);
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        var registered = key?.GetValue(ValueName) is string command && string.Equals(command, Command(), StringComparison.OrdinalIgnoreCase);
        if (registered)
        {
            using var approval = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
            if (approval?.GetValue(ValueName) is byte[] { Length: > 0 } status && status[0] is 3 or 7)
                return new(false, "Disabled in Windows. Enable PHP DevTray in Windows Startup settings.");
        }
        return new(registered);
    }

    public async Task<StartupRegistrationState> SetEnabledAsync(bool enabled)
    {
        if (AppDeployment.IsPackaged)
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (enabled) return FromState(await task.RequestEnableAsync());
            task.Disable();
            return FromState(task.State);
        }
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, Command(), RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
        return await ReadAsync();
    }

    private string Command()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the PHP DevTray executable.");
        return BuildCommand(executable, _dataDirectory);
    }

    internal static string BuildCommand(string executable, string dataDirectory)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"'))
            throw new ArgumentException("The startup executable must have an absolute path.", nameof(executable));
        if (!Path.IsPathFullyQualified(dataDirectory) || dataDirectory.Contains('"'))
            throw new ArgumentException("The startup profile must have an absolute path.", nameof(dataDirectory));
        // Double trailing backslashes so none can escape the closing argv quote (including C:\).
        var profile = dataDirectory.TrimEnd('\\');
        profile += new string('\\', (dataDirectory.Length - profile.Length) * 2);
        return $"\"{executable}\" --startup --data-directory \"{profile}\"";
    }

    internal static StartupRegistrationState FromState(StartupTaskState state) => state switch
    {
        StartupTaskState.Enabled => new(true),
        StartupTaskState.EnabledByPolicy => new(true, "Startup is enabled by your Windows administrator."),
        StartupTaskState.DisabledByUser => new(false, "Disabled in Windows. Enable PHP DevTray in Windows Startup settings."),
        StartupTaskState.DisabledByPolicy => new(false, "Startup is disabled by your Windows administrator."),
        _ => new(false)
    };
}
