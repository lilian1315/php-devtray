using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhpDevTray.Core;

public sealed record ActivitySpan(int Start, int Length, string Color);
public sealed record ActivityEntry(long Sequence, DateTimeOffset Timestamp, string Service, string Text, IReadOnlyList<ActivitySpan> Colors);

/// <summary>Chronologically ordered diagnostics bounded by both record count and character count.</summary>
public sealed partial class ActivityBuffer(int maximumEntries = 3000, int maximumCharacters = 2 * 1024 * 1024)
{
    private readonly object _sync = new();
    private readonly List<ActivityEntry> _entries = [];
    private int _characters;
    private long _sequence, _version;
    public long Version { get { lock (_sync) return _version; } }
    public event EventHandler? Changed;
    public IReadOnlyList<ActivityEntry> Snapshot { get { lock (_sync) return _entries.ToArray(); } }

    public void Append(string service, string line, DateTimeOffset? observed = null) => AppendBatch([(service, line, observed ?? DateTimeOffset.UtcNow)]);
    public void AppendBatch(IEnumerable<(string Service, string Line, DateTimeOffset Observed)> lines)
    {
        var added = false;
        lock (_sync)
        {
            foreach (var (service, raw, observed) in lines)
            {
                var (time, text) = ReadRecord(service, raw, observed);
                if (text.Length > maximumCharacters / 2) text = text[..(maximumCharacters / 2)] + " … [truncated]";
                var (plain, colors) = DecodeAnsi(text);
                var entry = new ActivityEntry(++_sequence, time, service, plain, colors);
                var index = _entries.BinarySearch(entry, EntryComparer.Instance);
                _entries.Insert(index < 0 ? ~index : index, entry); _characters += plain.Length;
                while (_entries.Count > maximumEntries || _characters > maximumCharacters)
                { _characters -= _entries[0].Text.Length; _entries.RemoveAt(0); }
                added = true;
            }
            if (added) _version++;
        }
        if (added) Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Clear()
    {
        lock (_sync) { _entries.Clear(); _characters = 0; _version++; }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static (DateTimeOffset Time, string Text) ReadRecord(string service, string raw, DateTimeOffset observed)
    {
        var time = observed; var message = raw; var level = "";
        try
        {
            using var document = JsonDocument.Parse(raw); var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                string Text(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                message = Text("message"); if (message.Length == 0) message = Text("msg");
                if (message.Length == 0) message = raw;
                level = Text("level").ToUpperInvariant();
                if (DateTimeOffset.TryParse(Text("timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) time = parsed;
                else if (root.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.Number && ts.TryGetDouble(out var seconds)
                    && seconds > -62135596800d && seconds < 253402300799d)
                    time = DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
                var context = root.EnumerateObject().Where(property => property.Name is not ("timestamp" or "ts" or "message" or "msg" or "level"))
                    .Select(property => property.Name + "=" + property.Value.GetRawText());
                var details = string.Join(" ", context); if (details.Length > 0) message += "  " + details;
            }
        }
        catch (JsonException) { }
        if (message == raw && NativeTimestamp().Match(raw) is { Success: true } native
            && DateTimeOffset.TryParse(native.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var nativeTime))
        { time = nativeTime; message = raw[native.Length..]; }
        return (time, $"[{service}] {(level.Length > 0 ? level + " " : "")}{message}");
    }

    private static (string Text, IReadOnlyList<ActivitySpan> Colors) DecodeAnsi(string raw)
    {
        var text = new StringBuilder(); var spans = new List<ActivitySpan>(); var offset = 0; string? color = null;
        foreach (Match match in Ansi().Matches(raw))
        {
            var chunk = raw[offset..match.Index];
            if (color is not null && chunk.Length > 0) spans.Add(new(text.Length, chunk.Length, color));
            text.Append(chunk);
            if (match.Value.EndsWith('m'))
                foreach (var code in match.Groups[1].Value.Split(';'))
                    color = code switch { "0" or "39" or "" => null, "31" or "91" => "ErrorBrush", "32" or "92" => "SuccessBrush", "33" or "93" => "WarningBrush", "34" or "36" or "94" or "96" => "AccentBrush", "35" or "95" => "AccentBrush", _ => color };
            offset = match.Index + match.Length;
        }
        if (color is not null && raw.Length > offset) spans.Add(new(text.Length, raw.Length - offset, color));
        text.Append(raw[offset..]); return (text.ToString(), spans);
    }
    [GeneratedRegex("\u001b\\[([0-9;?]*)(?:[ -/]*)([@-~])")]
    private static partial Regex Ansi();
    [GeneratedRegex(@"^\[?(\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?|\d{2}-[A-Za-z]{3}-\d{4} \d{2}:\d{2}:\d{2}(?: [A-Z]+)?)\]?\s*")]
    private static partial Regex NativeTimestamp();
    private sealed class EntryComparer : IComparer<ActivityEntry>
    {
        internal static readonly EntryComparer Instance = new();
        public int Compare(ActivityEntry? first, ActivityEntry? second)
        {
            var time = first!.Timestamp.CompareTo(second!.Timestamp);
            return time == 0 ? first.Sequence.CompareTo(second.Sequence) : time;
        }
    }
}
