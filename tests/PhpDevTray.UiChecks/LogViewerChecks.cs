using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;
using System.Windows.Threading;
using PhpDevTray.App;
using PhpDevTray.Core;

internal static class LogViewerChecks
{
    public static void Run()
    {
        CheckLocalizedTimestamps();
        CheckLinkNavigation();
        var buffer = new ActivityBuffer();
        buffer.Append("php", "first line"); buffer.Append("caddy", "second line");
        var viewer = new LogViewer(); viewer.UpdateEntries(buffer.Snapshot);
        var document = viewer.Document;
        viewer.Select(viewer.Text.IndexOf("first line", StringComparison.Ordinal), viewer.Text.IndexOf("second line", StringComparison.Ordinal) + "second line".Length - viewer.Text.IndexOf("first line", StringComparison.Ordinal));
        var selection = viewer.SelectedText;
        Require(selection.Contains('\n') && selection.Contains("second line"), "Native selection must span multiple records.");
        buffer.Append("php", "third line"); viewer.UpdateEntries(buffer.Snapshot);
        Require(viewer.SelectedText == selection && !viewer.Text.Contains("third line"), "Streaming must preserve an active selection.");
        viewer.Select(0, 0);
        Require(viewer.Text.Contains("third line") && ReferenceEquals(document, viewer.Document), "Selection release must apply pending lines to the existing document.");
        viewer.ServiceFilter = "caddy";
        Require(viewer.Text.Contains("second line") && !viewer.Text.Contains("first line"), "Service filtering must preserve only matching records.");
        viewer.ServiceFilter = null; viewer.ShowSearch();
        buffer.Clear(); viewer.UpdateEntries(buffer.Snapshot);
        Require(viewer.Text.Length == 0, "Clearing the shared buffer must clear the viewer.");
        buffer.Append("php", "\u001b[31mERROR\u001b[0m diagnostic");
        Require(buffer.Snapshot.Single().Colors.Count > 0 && !buffer.Snapshot.Single().Text.Contains('\u001b'), "ANSI colors must become semantic spans without escape characters.");
        viewer.UpdateEntries(buffer.Snapshot);
        viewer.Measure(new Size(700, 300)); viewer.Arrange(new Rect(0, 0, 700, 300)); viewer.UpdateLayout();
        viewer.Search("diagnostic"); viewer.FindMatch();
        Require(viewer.SelectedText == "diagnostic", "The persistent search input must use AvalonEdit's matching and navigation.");
        viewer.Search("");
        Console.WriteLine("PASS: native multiline selection, held streaming updates, incremental document, service filtering, search, clearing and ANSI spans.");
    }
    private static void CheckLinkNavigation()
    {
        var viewer = new LogViewer();
        var requestedLinks = new List<Uri>();
        viewer.LinkRequested += (_, uri) => requestedLinks.Add(uri);
        var page = new Page { Content = viewer };
        var frame = new Frame { Content = page };
        var window = new Window { Content = frame, Width = 700, Height = 300, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            frame.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            frame.Measure(new Size(700, 300)); frame.Arrange(new Rect(0, 0, 700, 300)); frame.UpdateLayout();
            var navigated = false;
            frame.Navigating += (_, args) => { navigated = true; args.Cancel = true; };
            var request = new RequestNavigateEventArgs(new Uri("https://example.test/"), null)
            { RoutedEvent = Hyperlink.RequestNavigateEvent };
            viewer.TextArea.TextView.RaiseEvent(request);
            Require(!navigated, "Activity links must not navigate the containing application frame away from its page.");
            Require(request.Handled, "Activity must handle external links before they reach WPF navigation.");
            Require(requestedLinks.SequenceEqual(new[] { request.Uri }), "The Activity link must be forwarded exactly once to the external opener.");
            Require(ReferenceEquals(frame.Content, page) && ReferenceEquals(page.Content, viewer), "Opening a link must preserve the Activity page.");
            Console.WriteLine("PASS: Activity links do not navigate away from the application page.");
        }
        finally { window.Close(); }
    }
    private static void CheckLocalizedTimestamps()
    {
        var localTime = new DateTime(2026, 11, 26, 14, 5, 37, DateTimeKind.Local);
        var time = new DateTimeOffset(localTime);
        var entry = new ActivityEntry(1, time, "php", "[php] ERROR", [new(6, 5, "ErrorBrush")]);
        var french = CultureInfo.GetCultureInfo("fr-FR");
        var english = CultureInfo.GetCultureInfo("en-US");
        var today = LogViewer.FormatEntry(entry, time, french);
        Require(today.Text == "14:05 [php] ERROR", "Today's timestamps must show only local hours and minutes.");
        var previous = LogViewer.FormatEntry(entry, time.AddDays(1), french);
        Require(previous.Text == "26/11 14:05 [php] ERROR", "Older timestamps must use numeric months in locale order, without year or seconds.");
        var american = LogViewer.FormatEntry(entry, time.AddDays(1), english);
        Require(american.Text.StartsWith("11/26 2:05", StringComparison.Ordinal) && american.Text.Contains("PM"),
            "US timestamps must use month/day order and the locale's 12-hour clock.");
        Require(LogViewer.FormatEntry(entry, time.AddDays(1), CultureInfo.GetCultureInfo("de-DE")).Text.StartsWith("26.11 ", StringComparison.Ordinal),
            "Numeric dates must preserve locale-specific separators.");
        Require(LogViewer.FormatEntry(entry, time.AddDays(1), CultureInfo.GetCultureInfo("ja-JP")).Text.StartsWith("11/26 ", StringComparison.Ordinal),
            "Year-first locales must retain month/day order after omitting the year.");
        var error = previous.Colors.Single(span => span.Color == "ErrorBrush");
        Require(previous.Text.Substring(error.Start, error.Length) == "ERROR" && entry.Text == "[php] ERROR",
            "Date formatting must preserve ANSI spans and leave the shared buffer unchanged.");
        var application = LogViewer.FormatEntry(entry with { Service = "application", Text = "[application] ERROR", Colors = [new(14, 5, "ErrorBrush")] }, time, french);
        Require(application.Text == "14:05 ERROR" && application.Text.Substring(application.Colors.Single(span => span.Color == "ErrorBrush").Start) == "ERROR",
            "Application labels must be omitted without moving diagnostic highlighting.");
        Console.WriteLine("PASS: localized Activity dates, hours/minutes, midnight rollover and preserved color spans.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
