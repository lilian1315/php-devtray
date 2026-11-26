using System.Text;
using PhpDevTray.Core;
using PhpDevTray.Infrastructure;
namespace PhpDevTray.Checks;

internal static class ApplicationStateChecks
{
    public static async Task RunAsync()
    {
        var tracker = new OperationTracker();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var job = Task.Run(async () =>
        {
            using var outer = tracker.Begin("download", () => cancelled = true);
            using (var nested = tracker.Begin("backend")) nested.Complete();
            outer.Report("half", .5); started.SetResult(); await release.Task; outer.Complete();
        });
        await started.Task;
        using (var independent = tracker.Begin("metadata"))
        {
            Require(tracker.Snapshot.Count(item => item.State == OperationState.Running) == 2, "Independent operations must overlap without duplicating nested backend operations.");
            var download = tracker.Snapshot.Single(item => item.Name == "download");
            Require(download.Progress == .5, "Progress must reach the shared tracker."); tracker.Cancel(download.Id);
            Require(cancelled, "Cancellation must invoke the supported callback."); independent.Complete();
        }
        release.SetResult(); await job;
        var retries = 0;
        using (var failed = tracker.Begin("failed", retry: () => { retries++; return Task.CompletedTask; })) failed.Fail(new IOException("offline"));
        await tracker.RetryAsync(tracker.Snapshot.Single(item => item.Name == "failed").Id);
        Require(retries == 1, "Failed operations must retain retry capabilities.");
        for (var i = 0; i < 50; i++) { using var operation = tracker.Begin("bounded"); operation.Complete(); }
        Require(tracker.Snapshot.Count == 30, "Terminal operation history must remain bounded.");
        var notifications = new NotificationCenter();
        notifications.Publish("Error", "offline", NotificationSeverity.Error); notifications.Publish("Error", "offline", NotificationSeverity.Error);
        Require(notifications.History.Count == 1, "Repeated failures must not produce duplicate notifications.");
        var buffer = new ActivityBuffer(3, 512);
        var now = DateTimeOffset.UtcNow;
        buffer.Append("php", "later", now); buffer.Append("caddy", "earlier", now.AddSeconds(-1));
        Require(buffer.Snapshot[0].Service == "caddy", "Merged records must be chronological.");
        buffer.Append("php", "\u001b[31mERROR\u001b[0m");
        Require(buffer.Snapshot.Last().Colors.Count > 0, "ANSI foreground must survive as spans.");
        buffer.Append("php", new string('x', 1000));
        Require(buffer.Snapshot.Count <= 3 && buffer.Snapshot.Sum(item => item.Text.Length) <= 512, "Both buffer limits must hold.");
        buffer.Clear(); Require(buffer.Snapshot.Count == 0, "Clear must empty the memory buffer.");
        await CheckFileTailAsync();
        Console.WriteLine("PASS: concurrent/nested operations, progress, cancel/retry, bounded history, chronological/ANSI buffers and incremental UTF-8 file tails.");
    }
    private static async Task CheckFileTailAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PhpDevTray-tail-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "php.log"); var buffer = new ActivityBuffer();
        try
        {
            await File.WriteAllTextAsync(path, "first\n");
            var partial = Path.Combine(root, "old-partial.log");
            await File.WriteAllTextAsync(partial, "previous session partial");
            await using (var reader = new ActivityLogReader(root, buffer))
            {
                await reader.StartAsync();
                await File.AppendAllTextAsync(path, "session start\n");
                await UntilAsync(() => buffer.Snapshot.Count == 1);
                Require(buffer.Snapshot.Single().Text.Contains("session start") && !buffer.Snapshot.Any(entry => entry.Text.Contains("first")),
                    "Existing records must not be loaded; appends from the current session must be captured.");
                await File.AppendAllTextAsync(partial, " continuation\nfresh partial-file line\n");
                await UntilAsync(() => buffer.Snapshot.Count == 2);
                Require(buffer.Snapshot.Any(entry => entry.Text.Contains("fresh partial-file line")) && !buffer.Snapshot.Any(entry => entry.Text.Contains("previous session") || entry.Text.Contains("continuation")),
                    "An unfinished record from the previous session must be skipped before accepting new complete lines.");
                buffer.Clear();
                var bytes = Encoding.UTF8.GetBytes("café\n");
                await using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    await stream.WriteAsync(bytes.AsMemory(0, 4)); await stream.FlushAsync();
                    await Task.Delay(400); Require(buffer.Snapshot.Count == 0, "An incomplete line must remain pending.");
                    await stream.WriteAsync(bytes.AsMemory(4)); await stream.FlushAsync();
                }
                await UntilAsync(() => buffer.Snapshot.Count == 1);
                Require(buffer.Snapshot[0].Text.Contains("café") && !buffer.Snapshot[0].Text.Contains("first"), "UTF-8 split across reads must survive; clear must retain file offsets.");
                await File.WriteAllTextAsync(path, "new\n");
                await UntilAsync(() => buffer.Snapshot.Count == 2);
                Require(buffer.Snapshot.Last().Text.Contains("new"), "Truncation must reset the file tail.");
                await File.WriteAllTextAsync(Path.Combine(root, "created-during-session.log"), "new file record\n");
                await UntilAsync(() => buffer.Snapshot.Count == 3);
                Require(buffer.Snapshot.Last().Text.Contains("new file record"), "Files created after session start must be read from their first record.");
                }
            var nextSession = new ActivityBuffer();
            await using var restarted = new ActivityLogReader(root, nextSession);
            await restarted.StartAsync();
            await File.AppendAllTextAsync(path, "next session record\n");
            await UntilAsync(() => nextSession.Snapshot.Count > 0);
            Require(nextSession.Snapshot.Count == 1 && nextSession.Snapshot[0].Text.Contains("next session record"),
                "Restarting the reader must establish fresh file offsets without replaying the previous session.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(30, timeout.Token);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
