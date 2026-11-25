using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using PhpDevTray.Core;

namespace PhpDevTray.Infrastructure.Releases;

public sealed partial class RuntimeReleaseManager
{
    private readonly ConcurrentDictionary<string, string> _phpVersions = new();

    private async Task<RuntimeRelease[]> AddPhpVersionsAsync(IReadOnlyList<RuntimeRelease> releases, CancellationToken token)
    {
        var result = releases.ToArray();
        await Parallel.ForEachAsync(Enumerable.Range(0, result.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (i, ct) =>
        {
            var release = result[i];
            if (!_phpVersions.TryGetValue(release.Sha256!, out var php))
            {
                try
                {
                    php = await Task.Run(() => ReadPhpVersion(release, ct), ct);
                    _phpVersions.TryAdd(release.Sha256!, php);
                }
                catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or JsonException or IOException)
                {
                    php = null;
                }
            }
            result[i] = release with { Channel = Version.TryParse(php, out var embedded) ? embedded.ToString(2) : null, DisplayName = $"FrankenPHP {release.Version} · PHP {php ?? "version unavailable"} · Windows x64" };
        });
        return LatestForEachPhpMinor(result);
    }

    internal static RuntimeRelease[] LatestForEachPhpMinor(IEnumerable<RuntimeRelease> releases)
    {
        var selected = releases.Where(r => r.Channel is not null).GroupBy(r => r.Channel!)
            .OrderByDescending(group => Version.Parse(group.Key))
            .Select(group => group.MaxBy(r => Version.Parse(r.Version))!).ToArray();
        if (selected.Length == 0) throw new InvalidDataException("Could not identify the embedded PHP version for any Windows release. Refresh the catalog to retry.");
        return selected;
    }

    private string ReadPhpVersion(RuntimeRelease release, CancellationToken token)
    {
        var uri = release.DownloadUri;
        using var stream = new ArchiveWindow(release.ArchiveBytes, (offset, count) =>
        {
            using var response = GetResponseAsync(uri, ServiceKind.FrankenPhp, token, new RangeHeaderValue(offset, offset + count - 1)).GetAwaiter().GetResult();
            var range = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != offset || range.To != offset + count - 1 || range.Length != release.ArchiveBytes)
                throw new InvalidDataException("Publisher did not return the requested archive window.");
            uri = response.RequestMessage?.RequestUri ?? uri;
            var bytes = new byte[count];
            using var input = response.Content.ReadAsStream(token);
            input.ReadExactlyAsync(bytes, token).AsTask().GetAwaiter().GetResult();
            return bytes;
        });
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var sbom = zip.GetEntry("extras/sbom/php.cdx.json");
        if (sbom is { Length: > 0 and < 1048576 })
        {
            using var input = sbom.Open();
            var bytes = new byte[(int)sbom.Length];
            input.ReadExactly(bytes);
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("component", out var component)
                && String(component, "name") == "php" && String(component, "version") is var php && Regex.IsMatch(php, @"^\d+\.\d+\.\d+$"))
                return php;
        }
        var news = zip.GetEntry("news.txt") ?? throw new InvalidDataException("Missing PHP build metadata.");
        using var reader = new StreamReader(news.Open());
        var prefix = new char[1024];
        var match = Regex.Match(new string(prefix, 0, reader.ReadBlock(prefix, 0, prefix.Length)), @"(?m)^\d{1,2} \w+ \d{4}, PHP (\d+\.\d+\.\d+)\b");
        return match.Success ? match.Groups[1].Value : throw new InvalidDataException("Missing PHP version.");
    }

    // ZipArchive seeks to the central directory and the selected metadata entry. Bound remote reads independently of ZIP fields.
    private sealed class ArchiveWindow(long length, Func<long, int, byte[]> fetch) : Stream
    {
        private const int BlockSize = 128 * 1024;
        private readonly Dictionary<long, byte[]> _blocks = [];
        public override long Length { get; } = length is > 0 and <= MaximumDownloadBytes ? length : throw new InvalidDataException("Invalid archive size.");
        public override long Position { get; set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Seek(long offset, SeekOrigin origin) => Position = checked(offset + (origin switch
        { SeekOrigin.Begin => 0, SeekOrigin.Current => Position, SeekOrigin.End => Length, _ => throw new ArgumentException(nameof(origin)) }));
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position < 0) throw new InvalidDataException("Invalid ZIP offset.");
            if (Position >= Length || count == 0) return 0;
            long start = Position / BlockSize * BlockSize;
            if (!_blocks.TryGetValue(start, out var block))
            {
                if (_blocks.Count >= 16) throw new InvalidDataException("ZIP metadata exceeds the read budget.");
                _blocks[start] = block = fetch(start, (int)Math.Min(BlockSize, Length - start));
            }
            int index = (int)(Position - start);
            count = Math.Min(count, block.Length - index);
            Array.Copy(block, index, buffer, offset, count);
            Position += count;
            return count;
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
