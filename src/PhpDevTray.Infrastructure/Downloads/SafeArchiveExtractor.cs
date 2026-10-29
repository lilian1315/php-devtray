using System.IO.Compression;
using System.Buffers.Binary;
using PhpDevTray.Core;

namespace PhpDevTray.Infrastructure.Downloads;

internal static class SafeArchiveExtractor
{
    internal const long MaximumExpandedBytes = 1024L * 1024 * 1024;
    internal const long MaximumEntryBytes = 256L * 1024 * 1024;
    internal const int MaximumEntries = 10_000;

    internal static async Task ExtractAsync(string archivePath, string destination, IProgress<RuntimeInstallProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination)) throw new IOException("Archive extraction requires a new, empty staging directory.");
        Directory.CreateDirectory(destination);
        try
        {
            await PreflightDirectoryAsync(archivePath, cancellationToken);
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count > MaximumEntries) throw new InvalidDataException("The archive contains too many entries.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long declaredBytes = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = ValidateName(entry.FullName);
                if (!names.Add(normalized.TrimEnd('/'))) throw new InvalidDataException("The archive contains duplicate or case-colliding paths.");
                RejectLink(entry);
                if (entry.Length > MaximumEntryBytes) throw new InvalidDataException("An archive entry exceeds the allowed expanded size.");
                declaredBytes = checked(declaredBytes + entry.Length);
                if (declaredBytes > MaximumExpandedBytes) throw new InvalidDataException("The archive exceeds the total expanded size limit.");
                if (entry.Length > 1024 * 1024 && (entry.CompressedLength == 0 || entry.Length / (double)entry.CompressedLength > 250))
                    throw new InvalidDataException("The archive contains a suspiciously large compression ratio.");
            }
            long expandedBytes = 0;
            var buffer = new byte[81920];
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = ValidateName(entry.FullName);
                var target = Path.GetFullPath(Path.Combine(destination, normalized.Replace('/', Path.DirectorySeparatorChar)));
                RequireDescendant(destination, target);
                if (normalized.EndsWith('/')) { CreateSafeDirectories(destination, target); continue; }
                CreateSafeDirectories(destination, Path.GetDirectoryName(target)!);
                await using var input = entry.Open();
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                long entryBytes = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    entryBytes = checked(entryBytes + count);
                    expandedBytes = checked(expandedBytes + count);
                    if (entryBytes > MaximumEntryBytes || entryBytes > entry.Length || expandedBytes > MaximumExpandedBytes)
                        throw new InvalidDataException("The archive expanded beyond its declared size or configured limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                if (entryBytes != entry.Length) throw new InvalidDataException("An archive entry has an inconsistent uncompressed length.");
                progress?.Report(new("Extracting verified archive", expandedBytes, declaredBytes));
            }
        }
        catch
        {
            DeleteContainedDirectory(Path.GetDirectoryName(destination)!, destination);
            throw;
        }
    }

    internal static string ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 1024) throw new InvalidDataException("The archive contains an invalid or excessively long path.");
        var normalized = name.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized)) throw new InvalidDataException("Absolute paths are prohibited in runtime archives.");
        var components = normalized.TrimEnd('/').Split('/');
        foreach (var part in components)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')
                || part.Any(character => character < 32 || "<>:\"|?*".Contains(character)))
                throw new InvalidDataException("The archive contains traversal, an alternate stream, or an invalid Windows filename.");
            var stem = part.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9'))
                throw new InvalidDataException("The archive contains a reserved Windows device name.");
        }
        return normalized;
    }

    internal static void RequireDescendant(string parent, string child)
    {
        var prefix = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(child).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A staging operation attempted to leave its designated directory.");
    }

    internal static void DeleteContainedDirectory(string parent, string directory)
    {
        RequireDescendant(parent, directory);
        if (!Directory.Exists(directory)) return;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Refusing to recursively delete a staging directory that became a junction.");
        Directory.Delete(directory, true);
    }

    private static void RejectLink(ZipArchiveEntry entry)
    {
        var attributes = unchecked((uint)entry.ExternalAttributes);
        var unixType = (attributes >> 16) & 0xF000;
        if ((attributes & (uint)FileAttributes.ReparsePoint) != 0 || (unixType != 0 && unixType is not 0x8000 and not 0x4000))
            throw new InvalidDataException("Symbolic links, junctions, and special files are prohibited in runtime archives.");
    }

    // Check the bounded end record before ZipArchive materializes attacker-controlled entry objects.
    private static async Task PreflightDirectoryAsync(string archivePath, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(archivePath);
        if (input.Length < 22) throw new InvalidDataException("The archive is missing its ZIP directory record.");
        var tail = new byte[(int)Math.Min(input.Length, 65535 + 22)];
        input.Seek(-tail.Length, SeekOrigin.End);
        await input.ReadExactlyAsync(tail, cancellationToken);
        for (var offset = tail.Length - 22; offset >= 0; offset--)
        {
            var record = tail.AsSpan(offset);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x06054B50 || offset + 22 + BinaryPrimitives.ReadUInt16LittleEndian(record[20..]) != tail.Length) continue;
            var disk = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            var directoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
            var diskEntries = BinaryPrimitives.ReadUInt16LittleEndian(record[8..]);
            var entries = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
            var directoryBytes = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
            var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
            if (disk != 0 || directoryDisk != 0 || entries != diskEntries || entries > MaximumEntries)
                throw new InvalidDataException("Split, ZIP64, or excessively populated runtime archives are unsupported.");
            if (directoryBytes > 16 * 1024 * 1024 || directoryOffset == uint.MaxValue || (long)directoryOffset + directoryBytes > input.Length - tail.Length + offset)
                throw new InvalidDataException("The ZIP directory is oversized or has invalid bounds.");
            return;
        }
        throw new InvalidDataException("The archive has no supported, bounded ZIP end record.");
    }

    private static void CreateSafeDirectories(string root, string directory)
    {
        if (string.Equals(Path.GetFullPath(root), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase)) return;
        RequireDescendant(root, directory);
        var current = root;
        foreach (var part in Path.GetRelativePath(root, directory).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            Directory.CreateDirectory(current);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Runtime extraction cannot traverse a symbolic link or junction.");
        }
    }
}
