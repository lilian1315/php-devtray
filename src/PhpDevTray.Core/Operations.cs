namespace PhpDevTray.Core;

public enum OperationState { Running, Succeeded, Cancelled, Failed }
public sealed record OperationSnapshot(Guid Id, string Name, DateTimeOffset Started, OperationState State = OperationState.Running,
    string? Detail = null, double? Progress = null, string? Error = null, bool CanCancel = false, bool CanRetry = false, bool Quiet = false);

/// <summary>Application-wide operation state. No dispatcher, controls, or synchronization context required.</summary>
public sealed class OperationTracker
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, OperationSnapshot> _items = [];
    private readonly Dictionary<Guid, Action> _cancel = [];
    private readonly Dictionary<Guid, Func<Task>> _retry = [];
    private readonly AsyncLocal<Guid?> _current = new();
    public event EventHandler? Changed;
    public event EventHandler<OperationSnapshot>? Finished;
    public IReadOnlyList<OperationSnapshot> Snapshot { get { lock (_sync) return _items.Values.OrderByDescending(item => item.Started).ToArray(); } }

    public OperationHandle Begin(string name, Action? cancel = null, Func<Task>? retry = null, bool quiet = false)
    {
        lock (_sync)
            if (_current.Value is { } current && _items.TryGetValue(current, out var running) && running.State == OperationState.Running)
                return new OperationHandle(this, current, ownsOperation: false);
        var item = new OperationSnapshot(Guid.NewGuid(), name, DateTimeOffset.UtcNow, CanCancel: cancel is not null, CanRetry: retry is not null, Quiet: quiet);
        lock (_sync)
        {
            _items[item.Id] = item;
            if (cancel is not null) _cancel[item.Id] = cancel;
            if (retry is not null) _retry[item.Id] = retry;
        }
        _current.Value = item.Id;
        RaiseChanged();
        return new OperationHandle(this, item.Id, ownsOperation: true);
    }
    private void RaiseChanged()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
            try { ((EventHandler)handler)(this, EventArgs.Empty); } catch { /* Presentation observers are isolated. */ }
    }
    public void ReportCurrent(string detail, double? progress = null) { if (_current.Value is { } id) Report(id, detail, progress); }
    public void ConfigureCurrent(Action? cancel = null, Func<Task>? retry = null)
    {
        lock (_sync)
        {
            if (_current.Value is not { } id || !_items.TryGetValue(id, out var item)) return;
            if (cancel is not null) _cancel[id] = cancel;
            if (retry is not null) _retry[id] = retry;
            _items[id] = item with { CanCancel = _cancel.ContainsKey(id), CanRetry = _retry.ContainsKey(id) };
        }
        RaiseChanged();
    }

    public void Cancel(Guid id)
    {
        Action? action; lock (_sync) _cancel.TryGetValue(id, out action);
        try { action?.Invoke(); } catch (ObjectDisposedException) { }
    }
    public Task RetryAsync(Guid id)
    {
        Func<Task>? action; lock (_sync) _retry.TryGetValue(id, out action);
        return action?.Invoke() ?? Task.CompletedTask;
    }
    internal void Report(Guid id, string? detail, double? progress)
    {
        lock (_sync)
        {
            if (!_items.TryGetValue(id, out var item) || item.State != OperationState.Running) return;
            _items[id] = item with { Detail = detail, Progress = progress is { } value ? Math.Clamp(value, 0, 1) : null };
        }
        RaiseChanged();
    }
    internal void End(Guid id, OperationState state, string? error)
    {
        OperationSnapshot item;
        lock (_sync)
        {
            if (!_items.TryGetValue(id, out var previous) || previous.State != OperationState.Running) return;
            item = previous with { State = state, Error = error, CanCancel = false, CanRetry = state == OperationState.Failed && _retry.ContainsKey(id), Progress = state == OperationState.Succeeded ? 1 : previous.Progress };
            _items[id] = item; _cancel.Remove(id);
            if (state != OperationState.Failed) _retry.Remove(id);
            foreach (var old in _items.Values.Where(value => value.State != OperationState.Running).OrderByDescending(value => value.Started).Skip(30).ToArray())
            { _items.Remove(old.Id); _retry.Remove(old.Id); }
        }
        if (_current.Value == id) _current.Value = null;
        RaiseChanged();
        foreach (var handler in Finished?.GetInvocationList() ?? [])
            try { ((EventHandler<OperationSnapshot>)handler)(this, item); } catch { /* Observers cannot alter an operation outcome. */ }
    }
}

public sealed class OperationHandle(OperationTracker tracker, Guid id, bool ownsOperation) : IDisposable
{
    private int _ended;
    public void Report(string detail, double? progress = null) => tracker.Report(id, detail, progress);
    public void Complete() => End(OperationState.Succeeded, null);
    public void Fail(Exception error) => End(error is OperationCanceledException ? OperationState.Cancelled : OperationState.Failed, error.Message);
    public void Dispose() => End(OperationState.Cancelled, null);
    private void End(OperationState state, string? error) { if (ownsOperation && Interlocked.Exchange(ref _ended, 1) == 0) tracker.End(id, state, error); }
}

public enum NotificationSeverity { Information, Success, Warning, Error }
public sealed record NotificationMessage(DateTimeOffset Created, string Title, string Message, NotificationSeverity Severity);

public sealed class NotificationCenter
{
    private readonly object _sync = new();
    private readonly List<NotificationMessage> _history = [];
    public event EventHandler<NotificationMessage>? Published;
    public IReadOnlyList<NotificationMessage> History { get { lock (_sync) return _history.ToArray(); } }
    public void Publish(string title, string message, NotificationSeverity severity)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var entry = new NotificationMessage(DateTimeOffset.UtcNow, title, message, severity);
        lock (_sync)
        {
            if (_history.Any(item => item.Message == message && item.Severity == severity && entry.Created - item.Created < TimeSpan.FromSeconds(5))) return;
            _history.Insert(0, entry);
            if (_history.Count > 40) _history.RemoveAt(_history.Count - 1);
        }
        foreach (var handler in Published?.GetInvocationList() ?? [])
            try { ((EventHandler<NotificationMessage>)handler)(this, entry); } catch { /* Keep history even when a presenter fails. */ }
    }
}
