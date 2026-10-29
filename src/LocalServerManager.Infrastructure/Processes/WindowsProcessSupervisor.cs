using System.Collections.Concurrent;
using System.Diagnostics;
using LocalServerManager.Core;

namespace LocalServerManager.Infrastructure.Processes;

/// <summary>Owns every managed process through a distinct kill-on-close Windows Job Object.</summary>
public sealed class WindowsProcessSupervisor : IProcessSupervisor
{
    private readonly ConcurrentDictionary<string, ServiceSlot> _services = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, OwnedProcess> _children = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _monitor;
    private int _disposeStarted;

    public WindowsProcessSupervisor() => _monitor = MonitorAsync();

    public event EventHandler<ServiceSnapshot>? ServiceChanged;

    public IReadOnlyList<ServiceSnapshot> Snapshots => _services.Values.Select(slot => Volatile.Read(ref slot.Snapshot)).ToArray();

    public bool IsRunning(string id) => _services.TryGetValue(id, out var slot) &&
        Volatile.Read(ref slot.Snapshot).Status == ServiceStatus.Running && slot.Process is { HasExited: false };

    public async Task StartAsync(ManagedServiceDefinition definition, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Id);
        if (definition.StartupTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(definition.StartupTimeout));
        var slot = _services.GetOrAdd(definition.Id, _ => new ServiceSlot(definition));
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await slot.Gate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            if (slot.Process is { HasExited: false } && slot.Snapshot.Status == ServiceStatus.Running) return;
            if (slot.Process is not null) await ReleaseAsync(slot.Process).ConfigureAwait(false);
            slot.Process = null;
            slot.Definition = definition;
            slot.FailedProbes = 0;
            Update(slot, ServiceStatus.Starting, "Checking ports and starting the contained process.");
            try
            {
                var process = await Task.Run(() =>
                {
                    ServiceReadiness.VerifyPortsAvailable(definition.HealthEndpoints);
                    operation.Token.ThrowIfCancellationRequested();
                    return OwnedProcess.Start(definition.Command, definition.LogDirectory, definition.Id);
                }, operation.Token).ConfigureAwait(false);
                _children[process.Id] = process;
                slot.Process = process;
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
                startup.CancelAfter(definition.StartupTimeout);
                // Detect binaries that immediately exit rather than advertising a transient Running state.
                await Task.Delay(200, startup.Token).ConfigureAwait(false);
                while (true)
                {
                    startup.Token.ThrowIfCancellationRequested();
                    if (process.HasExited)
                    {
                        await process.DrainOutputAsync().ConfigureAwait(false);
                        throw new InvalidOperationException($"{definition.DisplayName} exited during startup (code {process.ExitCode}). {Tail(process.StandardError)}".Trim());
                    }
                    if (await ServiceReadiness.CheckAsync(process, definition.HealthEndpoints, startup.Token).ConfigureAwait(false)) break;
                    await Task.Delay(150, startup.Token).ConfigureAwait(false);
                }
                Update(slot, ServiceStatus.Running, "Process and expected endpoints verified.", process.Id);
            }
            catch (Exception exception)
            {
                var error = exception is OperationCanceledException && !operation.IsCancellationRequested
                    ? new TimeoutException($"{definition.DisplayName} did not become ready within {definition.StartupTimeout.TotalSeconds:0.#} seconds.", exception)
                    : exception;
                if (slot.Process is { } failed)
                {
                    var detail = Tail(failed.StandardError);
                    await ReleaseAsync(failed).ConfigureAwait(false);
                    slot.Process = null;
                    Update(slot, ServiceStatus.Failed, string.IsNullOrWhiteSpace(detail) ? error.Message : error.Message + " " + detail);
                }
                else Update(slot, ServiceStatus.Failed, error.Message);
                if (!ReferenceEquals(error, exception)) throw error;
                throw;
            }
        }
        finally { slot.Gate.Release(); }
    }

    public async Task StopAsync(string id, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (!_services.TryGetValue(id, out var slot)) return;
        await slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var process = slot.Process;
            if (process is null)
            {
                Update(slot, ServiceStatus.Stopped, "Stopped.");
                return;
            }
            Update(slot, ServiceStatus.Stopping, "Stopping owned processes.", process.Id);
            var clock = Stopwatch.StartNew();
            var forced = false;
            string? gracefulError = null;
            try
            {
                if (!process.HasExited && slot.Definition.GracefulStopCommand is { } stop && timeout > TimeSpan.Zero)
                {
                    try
                    {
                        var controlBudget = Min(timeout, TimeSpan.FromSeconds(5));
                        var control = await RunCommandAsync(stop, controlBudget, cancellationToken).ConfigureAwait(false);
                        if (control.ExitCode != 0) gracefulError = $"Graceful stop command returned {control.ExitCode}: {Tail(control.StandardError)}";
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        gracefulError = "Graceful stop failed: " + exception.Message;
                    }
                }
                else if (!process.HasExited)
                {
                    // PHP-CGI and some Windows console builds do not offer a documented graceful command.
                    // Their dependencies must already have drained before the coordinator stops them.
                    process.Terminate();
                    forced = true;
                }

                var remaining = timeout - clock.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    wait.CancelAfter(remaining);
                    try { await process.WaitForTreeExitAsync(wait.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { forced = true; process.Terminate(); }
                }
                else { forced = true; process.Terminate(); }
            }
            finally
            {
                await ReleaseAsync(process).ConfigureAwait(false);
                slot.Process = null;
                Update(slot, ServiceStatus.Stopped, gracefulError ?? (forced ? "Stopped; owned process termination was required." : "Graceful shutdown completed."));
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally { slot.Gate.Release(); }
    }

    public async Task StopAllAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var clock = Stopwatch.StartNew();
        var ordered = _services.Values.OrderBy(slot => slot.Definition.Kind switch
        {
            ServiceKind.Nginx => 0, ServiceKind.Php => 1, _ => 2
        }).ToArray();
        foreach (var slot in ordered)
        {
            var remaining = timeout - clock.Elapsed;
            var phaseBudget = timeout.TotalMilliseconds * (slot.Definition.Kind == ServiceKind.Nginx ? 2.0 / 3 : 1.0 / 6);
            await StopAsync(slot.Definition.Id, Min(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
                TimeSpan.FromMilliseconds(phaseBudget)), cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<ProcessResult> RunAsync(ProcessCommand command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        return RunCommandAsync(command, timeout, cancellationToken);
    }

    private async Task<ProcessResult> RunCommandAsync(ProcessCommand command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        operation.CancelAfter(timeout);
        operation.Token.ThrowIfCancellationRequested();
        var process = await Task.Run(() => OwnedProcess.Start(command), operation.Token).ConfigureAwait(false);
        _children[process.Id] = process;
        try
        {
            try { await process.WaitForExitAsync(operation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            {
                throw new TimeoutException($"The command exceeded its {timeout.TotalSeconds:0.#}-second timeout.", exception);
            }
            var exitCode = process.ExitCode;
            // A completed control/validation command must not leave descendants holding pipe handles.
            process.Terminate();
            await process.DrainOutputAsync().ConfigureAwait(false);
            return new ProcessResult(exitCode, process.StandardOutput, process.StandardError);
        }
        finally { await ReleaseAsync(process).ConfigureAwait(false); }
    }

    private async Task MonitorAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                foreach (var slot in _services.Values)
                {
                    if (!await slot.Gate.WaitAsync(0, _lifetime.Token).ConfigureAwait(false)) continue;
                    try
                    {
                        if (slot.Snapshot.Status != ServiceStatus.Running || slot.Process is not { } process) continue;
                        if (process.HasExited)
                        {
                            var code = process.ExitCode;
                            var detail = Tail(process.StandardError);
                            await ReleaseAsync(process).ConfigureAwait(false);
                            slot.Process = null;
                            Update(slot, ServiceStatus.Failed, $"Unexpected process exit (code {code}). {detail}".Trim());
                            continue;
                        }
                        bool healthy;
                        try { healthy = await ServiceReadiness.CheckAsync(process, slot.Definition.HealthEndpoints, _lifetime.Token).ConfigureAwait(false); }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            healthy = false;
                        }
                        if (healthy) { slot.FailedProbes = 0; continue; }
                        if (++slot.FailedProbes < 3) continue;
                        await ReleaseAsync(process).ConfigureAwait(false);
                        slot.Process = null;
                        Update(slot, ServiceStatus.Failed, "Readiness failed three consecutive checks; the owned process group was stopped.");
                    }
                    finally { slot.Gate.Release(); }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private void Update(ServiceSlot slot, ServiceStatus status, string? detail, int? processId = null)
    {
        var endpoint = slot.Definition.HealthEndpoints.FirstOrDefault(endpoint => endpoint.Kind != HealthCheckKind.Process);
        var snapshot = new ServiceSnapshot
        {
            Id = slot.Definition.Id, DisplayName = slot.Definition.DisplayName, Kind = slot.Definition.Kind,
            Status = status, Detail = detail, Version = slot.Definition.Version, ProcessId = processId,
            Endpoint = endpoint is null ? null : $"{endpoint.Host}:{endpoint.Port}"
        };
        Volatile.Write(ref slot.Snapshot, snapshot);
        if (ServiceChanged is not { } handlers) return;
        foreach (EventHandler<ServiceSnapshot> handler in handlers.GetInvocationList())
        {
            try { handler(this, snapshot); }
            catch { /* Observer failures must not strand a process outside shutdown handling. */ }
        }
    }

    private async ValueTask ReleaseAsync(OwnedProcess process)
    {
        try { await process.DisposeAsync().ConfigureAwait(false); }
        finally { _children.TryRemove(process.Id, out _); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        // This is the final fallback after the coordinator's dependency-ordered graceful shutdown.
        foreach (var child in _children.Values) child.Terminate();
        try { await _monitor.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        foreach (var slot in _services.Values)
        {
            await slot.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (slot.Process is { } process) await ReleaseAsync(process).ConfigureAwait(false);
                slot.Process = null;
            }
            finally { slot.Gate.Release(); }
        }
        foreach (var child in _children.Values) await ReleaseAsync(child).ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;
    private static string Tail(string value) => value.Length > 4096 ? value[^4096..] : value;

    private sealed class ServiceSlot(ManagedServiceDefinition definition)
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal ManagedServiceDefinition Definition = definition;
        internal OwnedProcess? Process;
        internal int FailedProbes;
        internal ServiceSnapshot Snapshot = new()
        {
            Id = definition.Id, DisplayName = definition.DisplayName, Kind = definition.Kind,
            Version = definition.Version, Status = ServiceStatus.Stopped
        };
    }
}
