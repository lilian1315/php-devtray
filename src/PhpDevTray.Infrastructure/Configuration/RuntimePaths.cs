using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace PhpDevTray.Infrastructure.Configuration;

public static partial class RuntimePaths
{
    public static string NginxQuote(string value)
    {
        if (value.IndexOfAny(['\r', '\n', '\0', '$', '"']) >= 0)
            throw new ArgumentException("Nginx paths cannot contain quotes, dollar signs, or control characters.");
        return '"' + Path.GetFullPath(value).Replace('\\', '/') + '"';
    }

    public static string IniQuote(string value)
    {
        if (value.IndexOfAny(['\r', '\n', '\0', '"']) >= 0)
            throw new ArgumentException("PHP paths cannot contain quotes or control characters.");
        return '"' + Path.GetFullPath(value).Replace('\\', '/') + '"';
    }

    public static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
    }

    public static bool IsPortAvailable(int port)
    {
        ValidatePort(port);
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Server.ExclusiveAddressUse = true;
            try { listener.Start(); return true; }
            finally { listener.Stop(); }
        }
        catch (SocketException) { return false; }
    }

    public static string SafeIdentifier(string value)
    {
        if (!IdentifierPattern().IsMatch(value)) throw new ArgumentException("Use 1–64 letters, digits, underscores, or hyphens for the site ID.");
        return value;
    }

    public static string SafeHostname(string value)
    {
        if (!HostnamePattern().IsMatch(value) || value.Length > 253 || value.Split('.').Any(p => p.Length > 63))
            throw new ArgumentException("Enter a valid local hostname, such as app.test.");
        return value.ToLowerInvariant();
    }

    public static void VerifyExecutable(string path, bool requireX64 = false)
    {
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Choose a Windows executable from an extracted official distribution.", path);
        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.BaseStream.Length < 64 || reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException("The selected file is not a Windows executable.");
        reader.BaseStream.Position = 0x3C;
        var peOffset = reader.ReadInt32();
        if (peOffset < 0 || peOffset > reader.BaseStream.Length - 6) throw new InvalidDataException("Invalid Windows executable header.");
        reader.BaseStream.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550) throw new InvalidDataException("Invalid Windows executable signature.");
        var machine = reader.ReadUInt16();
        if (requireX64 && machine != 0x8664) throw new InvalidDataException("PHP must be an x64 Windows build.");
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?)(?:\\.[a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?)*$", RegexOptions.CultureInvariant)]
    private static partial Regex HostnamePattern();
}
