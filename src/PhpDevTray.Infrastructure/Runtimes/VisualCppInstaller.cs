using System.ComponentModel;
using System.Diagnostics;
using PhpDevTray.Core;

namespace PhpDevTray.Infrastructure.Runtimes;

public static class VisualCppInstaller
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(3), MaxResponseContentBufferSize = 64 * 1024 * 1024 };

    public static async Task<bool> InstallAsync(IProgress<RuntimeInstallProgress> progress, CancellationToken token)
    {
        var directory = Directory.CreateTempSubdirectory("PhpDevTray-VC-").FullName;
        var path = Path.Combine(directory, "vc_redist.x64.exe");
        try
        {
            progress.Report(new("Downloading Microsoft Visual C++ x64"));
            var bytes = await Client.GetByteArrayAsync(VisualCppRuntimeException.DownloadUrl, token);
            await File.WriteAllBytesAsync(path, bytes, token);
            using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            progress.Report(new("Verifying Microsoft installer signature"));
            await VerifyPublisherAsync(path, token);
            token.ThrowIfCancellationRequested();
            progress.Report(new("Waiting for Microsoft Visual C++ setup"));
            using var installer = await Task.Run(() => Process.Start(new ProcessStartInfo(path)
            {
                WorkingDirectory = directory, UseShellExecute = true, Arguments = "/install /passive /norestart"
            }) ?? throw new InvalidOperationException("Could not start Microsoft Visual C++ setup."), token);
            // Setup owns its elevation prompt. Let it finish even if the PHP retry has been cancelled.
            await installer.WaitForExitAsync();
            return installer.ExitCode switch
            {
                0 => false,
                3010 => true,
                1602 or 1223 => throw new InvalidOperationException("Microsoft Visual C++ setup was cancelled."),
                _ => throw new InvalidOperationException($"Microsoft Visual C++ setup exited with code {installer.ExitCode}.")
            };
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Windows permission for Microsoft Visual C++ setup was declined.", error);
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static async Task VerifyPublisherAsync(string path, CancellationToken token)
    {
        var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
            "$s = Get-AuthenticodeSignature -LiteralPath $env:PDT_VC_INSTALLER; if ($s.Status -ne 'Valid' -or $s.SignerCertificate.GetNameInfo('SimpleName',$false) -ne 'Microsoft Corporation') { exit 1 }; exit 0" })
            info.ArgumentList.Add(arg);
        info.Environment["PDT_VC_INSTALLER"] = path;
        using var verify = Process.Start(info) ?? throw new InvalidOperationException("Could not verify the Microsoft installer.");
        try { await verify.WaitForExitAsync(token); }
        catch (OperationCanceledException) { if (!verify.HasExited) verify.Kill(); throw; }
        if (verify.ExitCode != 0) throw new InvalidDataException("Windows could not verify a valid Microsoft Corporation signature on the downloaded installer.");
    }
}
