using System.Text;
using PhpDevTray.Core;

namespace PhpDevTray.Infrastructure;

/// <summary>Tails shared log files off the UI thread, preserving partial lines and UTF-8 between reads.</summary>
internal sealed class ActivityLogReader(string root, ActivityBuffer buffer) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, Tail> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Position> _positions = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _started;
    private Task? _start;
    private Task? _worker;
    public Task StartAsync() => _start ??= StartCoreAsync();
    private async Task StartCoreAsync()
    {
        _started = DateTime.UtcNow;
        // Establish EOF before initialization or service startup can append session records.
        await Task.Run(() =>
        {
            try
            {
                foreach (var path in LogFiles())
                {
                    _lifetime.Token.ThrowIfCancellationRequested();
                    try { _positions[path] = EndPosition(path); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }, _lifetime.Token);
        _worker = Task.Run(ReadAsync);
    }
    private IEnumerable<string> LogFiles() => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetExtension(path).Equals(".log", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase));
    private static Position EndPosition(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        if (length > 0) stream.Position = length - 1;
        return new(File.GetCreationTimeUtc(path), length, length > 0 && stream.ReadByte() != '\n');
    }
    private async Task ReadAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(300));
        try
        {
            do
            {
                var batch = new List<(string, string, DateTimeOffset)>();
                try
                {
                    var files = LogFiles()
                        .OrderByDescending(File.GetLastWriteTimeUtc).Take(64).ToArray();
                    foreach (var path in files)
                    {
                        _lifetime.Token.ThrowIfCancellationRequested();
                        try { await ReadFileAsync(path, batch, _lifetime.Token); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                    }
                    if (_files.Count > 128)
                        foreach (var path in _files.Keys.Where(path => !files.Contains(path, StringComparer.OrdinalIgnoreCase)).Take(_files.Count - 128).ToArray())
                        {
                            var tail = _files[path];
                            _positions[path] = new(tail.Created, tail.Offset, tail.SkipPartial || tail.HasPartialBytes);
                            _files.Remove(path);
                        }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                buffer.AppendBatch(batch);
            } while (await timer.WaitForNextTickAsync(_lifetime.Token));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task ReadFileAsync(string path, List<(string, string, DateTimeOffset)> batch, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (!_files.TryGetValue(path, out var tail) || info.CreationTimeUtc != tail.Created || info.Length < tail.Offset)
        {
            var firstSeen = !_files.ContainsKey(path);
            tail = new Tail(info.CreationTimeUtc); _files[path] = tail;
            if (firstSeen && _positions.TryGetValue(path, out var position))
            {
                if (position.Created == info.CreationTimeUtc && info.Length >= position.Offset)
                { tail.Offset = position.Offset; tail.SkipPartial = position.SkipPartial; }
            }
            else if (firstSeen && info.CreationTimeUtc < _started)
            {
                // An old file discovered late (for example a renamed archive) is not replayed.
                var end = EndPosition(path); tail.Offset = end.Offset; tail.SkipPartial = end.SkipPartial;
            }
        }
        if (info.Length == tail.Offset) return;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, true);
        stream.Position = tail.Offset;
        var bytes = new byte[8192]; var characters = new char[8192]; var budget = 256 * 1024;
        var relative = Path.GetRelativePath(root, path);
        var service = (Path.GetDirectoryName(relative) is { Length: > 0 } directory ? directory : Path.GetFileNameWithoutExtension(relative)).Replace('\\', '/');
        while (budget > 0)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(0, Math.Min(bytes.Length, budget)), token);
            if (count == 0) break;
            tail.Offset += count; budget -= count;
            tail.HasPartialBytes = bytes[count - 1] != '\n';
            var length = tail.Decoder.GetChars(bytes, 0, count, characters, 0, false);
            for (var index = 0; index < length; index++)
            {
                var character = characters[index];
                if (character == '\n')
                {
                    if (!tail.SkipPartial) batch.Add((service, tail.Pending.ToString().TrimEnd('\r'), DateTimeOffset.UtcNow));
                    tail.Pending.Clear(); tail.SkipPartial = false;
                }
                else if (!tail.SkipPartial && tail.Pending.Length < 64 * 1024) tail.Pending.Append(character);
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        if (_worker is not null) await _worker;
        _lifetime.Dispose();
    }
    private sealed class Tail(DateTime created)
    {
        public DateTime Created { get; } = created;
        public long Offset;
        public bool SkipPartial;
        public bool HasPartialBytes;
        public Decoder Decoder { get; } = Encoding.UTF8.GetDecoder();
        public StringBuilder Pending { get; } = new();
    }
    private sealed record Position(DateTime Created, long Offset, bool SkipPartial);
}
