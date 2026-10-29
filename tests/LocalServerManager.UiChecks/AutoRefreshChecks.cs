using System.Windows.Threading;
using LocalServerManager.App;

internal static class AutoRefreshChecks
{
    public static void Run()
    {
        var calls = new List<string>();
        var cancellationSeen = false;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var refresh = new PageAutoRefresher(Dispatcher.CurrentDispatcher, async (page, token) =>
        {
            calls.Add(page);
            if (page != "Mail Trap") return;
            try { await pending.Task.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { cancellationSeen = true; throw; }
        });
        refresh.SetPage("Mail Trap");
        Pump(1150);
        Require(calls.Count == 1, "A slow page request must not overlap the next one-second tick.");
        refresh.SetPage(null);
        Pump(80);
        Require(cancellationSeen, "Leaving or hiding the page must cancel its pending refresh.");
        Pump(1100);
        Require(calls.Count == 1, "A hidden/ineligible page must not continue polling.");
        refresh.SetPage("Logs");
        Require(calls.SequenceEqual(["Mail Trap", "Logs"]), "Entering a page should refresh it immediately.");
        Pump(300);
        Require(calls.Count == 2, "Periodic refresh must not fire before its one-second interval.");
        Pump(850);
        Require(calls.Count >= 3 && calls.Skip(1).All(page => page == "Logs"), "The visible selected page must refresh each second.");
        refresh.Dispose();
        var count = calls.Count;
        Pump(1100);
        Require(calls.Count == count, "Disposing the popup must stop its page timer.");
        Console.WriteLine("PASS: visible-page refresh runs every second, cancels on exit, stops while hidden/disposed, and never overlaps a slow request.");
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var deadline = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        deadline.Tick += (_, _) => { deadline.Stop(); frame.Continue = false; };
        deadline.Start();
        Dispatcher.PushFrame(frame);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
