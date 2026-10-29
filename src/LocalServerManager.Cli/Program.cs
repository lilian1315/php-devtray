using System.Diagnostics;
using System.Text.Json;

namespace LocalServerManager.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "launcher.json");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var selection = await JsonSerializer.DeserializeAsync<Selection>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (selection?.ExecutablePath is null || selection.PrimaryIni is null || selection.ScanDirectory is null)
                throw new InvalidOperationException("No default PHP channel is configured. Select one in Local Server Manager.");
            if (!File.Exists(selection.ExecutablePath)) throw new FileNotFoundException("The selected PHP installation is missing. Repair it in Local Server Manager.");
            if (!Directory.Exists(selection.ScanDirectory) || !File.Exists(selection.PrimaryIni))
                throw new FileNotFoundException("The selected PHP configuration is missing. Apply it again in Local Server Manager.");
            var start = new ProcessStartInfo(selection.ExecutablePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Environment.CurrentDirectory
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(selection.PrimaryIni);
            foreach (var argument in args) start.ArgumentList.Add(argument);
            start.Environment["PHP_INI_SCAN_DIR"] = selection.ScanDirectory;
            start.Environment.Remove("PHPRC");
            // PHP CLI is intentionally independent of the tray supervisor and its Job Objects.
            using var process = Process.Start(start) ?? throw new InvalidOperationException("PHP could not be started.");
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await Console.Error.WriteLineAsync("Local Server Manager: " + exception.Message);
            return 1;
        }
    }

    private sealed record Selection(string? ExecutablePath, string? PrimaryIni, string? ScanDirectory, string? Channel);
}
