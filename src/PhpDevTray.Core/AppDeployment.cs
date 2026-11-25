using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace PhpDevTray.Core;

public static class AppDeployment
{
    public static string? PackageFamilyName { get; } = ReadPackageFamilyName();
    public static bool IsPackaged => PackageFamilyName is not null;

    // Use one physical path for the manager, execution alias and downloaded processes.
    // Package-owned data is kept across Store updates and removed on uninstall.
    public static string DefaultDataDirectory => PackageFamilyName is { } family
        ? Path.Combine(ReadUnredirectedLocalAppData(), "Packages", family, "LocalState", "PhpDevTray")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhpDevTray");

    public static string CliSelectionPath => IsPackaged
        ? Path.Combine(DefaultDataDirectory, "bin", "launcher.json")
        : Path.Combine(AppContext.BaseDirectory, "launcher.json");

    private static string ReadUnredirectedLocalAppData()
    {
        var localAppDataId = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
        // KF_FLAG_NO_PACKAGE_REDIRECTION: avoid appending Packages/<family> to
        // a framework-returned folder that has already been redirected.
        var result = SHGetKnownFolderPath(in localAppDataId, 0x00010000, 0, out var path);
        try
        {
            Marshal.ThrowExceptionForHR(result);
            return Marshal.PtrToStringUni(path) ?? throw new InvalidOperationException("Windows did not return the local application data folder.");
        }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    private static string? ReadPackageFamilyName()
    {
        uint length = 0;
        var result = GetCurrentPackageFamilyName(ref length, null);
        if (result == 15700) return null; // APPMODEL_ERROR_NO_PACKAGE
        if (result != 122) throw new Win32Exception(result); // ERROR_INSUFFICIENT_BUFFER
        var family = new StringBuilder(checked((int)length));
        result = GetCurrentPackageFamilyName(ref length, family);
        if (result != 0) throw new Win32Exception(result);
        return family.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, StringBuilder? packageFamilyName);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid folderId, uint flags, nint token, out nint path);
}
