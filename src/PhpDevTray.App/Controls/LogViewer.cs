using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using PhpDevTray.Core;

namespace PhpDevTray.App;

/// <summary>Read-only native text editing behavior over incrementally applied diagnostic records.</summary>
internal sealed partial class LogViewer : TextEditor
{
    private readonly SearchPanel _search;
    private readonly ActivityBuffer _fallback = new();
    private IReadOnlyList<string>? _fallbackLines;
    private IReadOnlyList<ActivityEntry> _rendered = [];
    private IReadOnlyList<ActivityEntry> _latest = [];
    private readonly List<(int Start, int End, string Brush)> _colors = [];
    private bool _applying;
    private string? _filter;
    private int _tailUpdate;
    public event EventHandler? SearchRequested;
    public event EventHandler<Uri>? LinkRequested;
    public IEnumerable<string> Services => _latest.Select(entry => entry.Service).Distinct();

    public LogViewer()
    {
        IsReadOnly = true; ShowLineNumbers = false;
        FontFamily = new FontFamily("Cascadia Mono, Consolas"); FontSize = 12;
        Padding = new Thickness(12); BorderThickness = new Thickness(0);
        HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
        SetResourceReference(BackgroundProperty, "CardBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        AutomationProperties.SetName(this, "Recent application log");
        // Keep external links out of the containing page's WPF navigation frame.
        AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler((_, args) =>
        {
            args.Handled = true;
            LinkRequested?.Invoke(this, args.Uri);
        }));
        _search = SearchPanel.Install(this);
        // AvalonEdit owns matching and highlighting; the page owns the persistent search input.
        _search.Visibility = Visibility.Collapsed;
        TextArea.TextView.LineTransformers.Add(new LogColors(this));
        TextArea.SelectionChanged += (_, _) => { if (!_applying && SelectionLength == 0) UpdateEntries(_latest); };
    }
    public string? ServiceFilter
    {
        get => _filter;
        set { if (_filter == value) return; _filter = value; Select(0, 0); UpdateEntries(_latest); }
    }
    public string AllText => Text;
    public void CopyAll() { if (Text.Length > 0) Clipboard.SetText(Text); }
    public void ShowSearch() => SearchRequested?.Invoke(this, EventArgs.Empty);
    public void Search(string pattern)
    {
        if (pattern.Length == 0) { _search.SearchPattern = ""; _search.Close(); Select(SelectionStart, 0); }
        else { _search.Open(); _search.SearchPattern = pattern; }
    }
    public void FindMatch(bool previous = false)
    {
        if (previous) _search.FindPrevious(); else _search.FindNext();
    }
    public void UpdateLines(IReadOnlyList<string> lines)
    {
        if (_fallbackLines?.SequenceEqual(lines) == true) return;
        _fallbackLines = lines; _fallback.Clear(); var service = "application";
        foreach (var line in lines)
        {
            if (line.StartsWith("— ", StringComparison.Ordinal))
            { var path = line[2..].Replace('\\', '/').Split('/'); service = path.Length > 1 ? path[^2] : "application"; continue; }
            _fallback.Append(service, line);
        }
        UpdateEntries(_fallback.Snapshot);
    }
    public void UpdateEntries(IReadOnlyList<ActivityEntry> entries)
    {
        _latest = entries;
        if (SelectionLength > 0 || _applying) return;
        var now = DateTimeOffset.Now;
        var culture = CultureInfo.CurrentCulture;
        var next = entries.Where(entry => _filter is null || entry.Service == _filter)
            .OrderBy(entry => entry.Timestamp).ThenBy(entry => entry.Sequence)
            .Select(entry => FormatEntry(entry, now, culture)).ToArray();
        var prefix = 0;
        while (prefix < next.Length && prefix < _rendered.Count && next[prefix].Sequence == _rendered[prefix].Sequence && next[prefix].Text == _rendered[prefix].Text) prefix++;
        var suffix = 0;
        while (suffix < next.Length - prefix && suffix < _rendered.Count - prefix && next[^(suffix + 1)].Sequence == _rendered[^(suffix + 1)].Sequence && next[^(suffix + 1)].Text == _rendered[^(suffix + 1)].Text) suffix++;
        if (prefix == next.Length && prefix == _rendered.Count) { TextArea.TextView.Redraw(); return; }
        var following = _rendered.Count == 0 || VerticalOffset >= ExtentHeight - ViewportHeight - 2;
        var vertical = VerticalOffset; var horizontal = HorizontalOffset;
        var start = _rendered.Take(prefix).Sum(entry => entry.Text.Length + 1);
        var removed = _rendered.Skip(prefix).Take(_rendered.Count - prefix - suffix).Sum(entry => entry.Text.Length + 1);
        var added = string.Concat(next.Skip(prefix).Take(next.Length - prefix - suffix).Select(entry => entry.Text + "\n"));
        _applying = true;
        try
        {
            using (Document.RunUpdate()) Document.Replace(start, removed, added);
            _rendered = next; _colors.Clear(); var offset = 0;
            foreach (var entry in next)
            {
                foreach (var color in entry.Colors) _colors.Add((offset + color.Start, offset + color.Start + color.Length, color.Color));
                foreach (Match match in Highlights().Matches(entry.Text))
                {
                    var token = match.Value.ToUpperInvariant();
                    var brush = token is "ERROR" or "FATAL" or "PANIC" or "CRITICAL" ? "ErrorBrush" : token is "WARN" or "WARNING" ? "WarningBrush" : "MutedBrush";
                    _colors.Add((offset + match.Index, offset + match.Index + match.Length, brush));
                }
                offset += entry.Text.Length + 1;
            }
            _colors.Sort((a, b) => a.Start.CompareTo(b.Start));
            TextArea.TextView.Redraw();
            var tailUpdate = ++_tailUpdate;
            if (following)
            {
                ScrollToEnd();
                var expectedOffset = VerticalOffset;
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    if (tailUpdate == _tailUpdate && SelectionLength == 0 && Math.Abs(VerticalOffset - expectedOffset) < 1)
                        ScrollToEnd();
                });
            }
            else ScrollToVerticalOffset(vertical);
            ScrollToHorizontalOffset(horizontal);
        }
        finally { _applying = false; }
    }
    internal static ActivityEntry FormatEntry(ActivityEntry entry, DateTimeOffset now, CultureInfo culture)
    {
        var local = entry.Timestamp.ToLocalTime();
        var stamp = local.Date == now.ToLocalTime().Date ? local.ToString("t", culture)
            : NumericMonthDay(local, culture) + " " + local.ToString("t", culture);
        var prefix = stamp + " ";
        var removed = entry.Service == "application" && entry.Text.StartsWith("[application] ", StringComparison.Ordinal)
            ? "[application] ".Length : 0;
        ActivitySpan[] colors = [new(0, stamp.Length, "MutedBrush"),
            .. entry.Colors.Where(span => span.Start + span.Length > removed).Select(span => span with
            { Start = Math.Max(span.Start, removed) - removed + prefix.Length, Length = span.Length - Math.Max(0, removed - span.Start) })];
        return entry with { Text = prefix + entry.Text[removed..], Colors = colors };
    }
    private static string NumericMonthDay(DateTimeOffset time, CultureInfo culture)
    {
        var firstField = DateFields().Matches(culture.DateTimeFormat.ShortDatePattern)
            .FirstOrDefault(match => match.Groups[1].Success)?.Groups[1].Value[0];
        var day = time.ToString("dd", culture); var month = time.ToString("MM", culture);
        var separator = culture.DateTimeFormat.DateSeparator;
        return firstField == 'd' ? day + separator + month : month + separator + day;
    }
    [GeneratedRegex("'[^']*'|\"[^\"]*\"|\\\\.|(d+|M+)")]
    private static partial Regex DateFields();
    [GeneratedRegex(@"\b(?:ERROR|FATAL|PANIC|CRITICAL|WARN|WARNING|DEBUG|TRACE)\b|\[[^\]\r\n]+\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Highlights();
    private sealed class LogColors(LogViewer owner) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            foreach (var span in owner._colors)
            {
                if (span.Start >= line.EndOffset) break;
                var start = Math.Max(line.Offset, span.Start); var end = Math.Min(line.EndOffset, span.End);
                if (start < end && owner.TryFindResource(span.Brush) is Brush brush)
                    ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(brush));
            }
        }
    }
}
