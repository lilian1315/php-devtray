using System.Threading.Channels;

namespace PhpDevTray.Infrastructure.Configuration;

internal sealed class SiteConfigurationWatcher : IAsyncDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task _worker;

    internal SiteConfigurationWatcher(string directory, Func<IReadOnlySet<string>, CancellationToken, Task> refresh, Action<Exception> report)
    {
        Directory.CreateDirectory(directory);
        _watcher = new FileSystemWatcher(directory, "*.Caddyfile")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false
        };
        _watcher.Created += Changed;
        _watcher.Changed += Changed;
        _watcher.Deleted += Changed;
        _watcher.Renamed += (_, args) => { Enqueue(args.OldName); Enqueue(args.Name); };
        _watcher.Error += (_, _) => Enqueue("*");
        _worker = Task.Run(async () =>
        {
            var token = _lifetime.Token;
            try
            {
                while (await _signal.Reader.WaitToReadAsync(token))
                {
                    _signal.Reader.TryRead(out _);
                    do { await Task.Delay(500, token); } while (_signal.Reader.TryRead(out _));
                    HashSet<string> files;
                    lock (_pending) { files = new(_pending, StringComparer.OrdinalIgnoreCase); _pending.Clear(); }
                    try { await refresh(files, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error) { report(error); }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        });
        _watcher.EnableRaisingEvents = true;
    }

    private void Changed(object sender, FileSystemEventArgs args) => Enqueue(args.Name);

    private void Enqueue(string? name)
    {
        if (name is null) return;
        lock (_pending) _pending.Add(name);
        _signal.Writer.TryWrite(true);
    }

    public async ValueTask DisposeAsync()
    {
        _watcher.Dispose();
        _lifetime.Cancel();
        await _worker;
        _lifetime.Dispose();
    }
}
