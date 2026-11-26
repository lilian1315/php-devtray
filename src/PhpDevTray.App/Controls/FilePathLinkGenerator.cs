using System.Text.RegularExpressions;
using ICSharpCode.AvalonEdit.Rendering;

namespace PhpDevTray.App;

internal sealed partial class FilePathLinkGenerator : LinkElementGenerator
{
    // Quoted paths may contain spaces. Unquoted paths stop before log separators.
    private const string Root = @"(?:[A-Za-z]:[\\/]+|\\{2,}[^\\/\s:""<>|]+[\\/]+[^\\/\s:""<>|]+[\\/]+)";
    public FilePathLinkGenerator() : base(Paths()) { }

    protected override Uri GetUriFromMatch(Match match)
    {
        var path = match.Groups["path"].Value.Replace('/', '\\');
        var network = path.StartsWith(@"\\", StringComparison.Ordinal);
        // Caddy's JSON logs escape backslashes; retain the leading UNC pair only.
        path = Backslashes().Replace(path, _ => @"\");
        if (network) path = @"\" + path;
        return Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile ? uri : null!;
    }

    [GeneratedRegex("(?<![\\w/\\\\])(?:\"(?<path>" + Root + "[^\"<>|?*\\r\\n]*)\"|'(?<path>" + Root
        + "[^'\"<>|?*\\r\\n]*)'|(?<path>" + Root + "(?:[^\\s\"'<>|?*:]*[^\\s\"'<>|?*:.,;)}\\]])?))", RegexOptions.CultureInvariant)]
    private static partial Regex Paths();

    [GeneratedRegex(@"\\+")]
    private static partial Regex Backslashes();
}
