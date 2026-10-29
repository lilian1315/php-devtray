using System.Windows.Threading;

namespace PhpDevTray.App;

/// <summary>A single, cancellable refresh at a time, with no timer while a live page is hidden.</summary>
internal sealed class PageAutoRefresher : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Func<string, CancellationToken, Task> _refresh;
    private CancellationTokenSource? _scope;
    private string? _page;
    private Task _pending = Task.CompletedTask;
    private bool _disposed;

    public PageAutoRefresher(Dispatcher dispatcher, Func<string, CancellationToken, Task> refresh)
    {
        _refresh = refresh;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
    }

    public void SetPage(string? page)
    {
        if (_disposed || page == _page) return;
        _timer.Stop();
        _scope?.Cancel(); _scope?.Dispose(); _scope = null;
        _page = page;
        if (page is null) return;
        _scope = new CancellationTokenSource();
        _timer.Start();
        _ = RefreshNowAsync();
    }

    public Task RefreshNowAsync()
    {
        if (!_pending.IsCompleted) return _pending;
        if (_disposed || _page is null || _scope is null) return Task.CompletedTask;
        return _pending = InvokeAsync(_page, _scope.Token);
    }

    private async Task InvokeAsync(string page, CancellationToken token)
    {
        try { await _refresh(page, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async void OnTick(object? sender, EventArgs args) => await RefreshNowAsync();

    public void Dispose()
    {
        if (_disposed) return;
        SetPage(null);
        _disposed = true;
        _timer.Tick -= OnTick;
    }
}
