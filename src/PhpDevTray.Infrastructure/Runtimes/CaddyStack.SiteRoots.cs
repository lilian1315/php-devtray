using System.Text.Json;
using PhpDevTray.Core;

namespace PhpDevTray.Infrastructure.Runtimes;

internal sealed partial record CaddyStack
{
    internal async Task<IReadOnlyDictionary<string, string?>> ReadSiteRootsAsync(string root, IProcessSupervisor supervisor, AppSettings settings, CancellationToken token)
    {
        var roots = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in Sites)
        {
            roots[site.Hostname] = null;
            var runtime = Runtimes.FirstOrDefault(r => "franken-" + r.Id == site.RuntimeId);
            var executable = runtime?.Executable ?? Executable;
            if (!File.Exists(executable)) continue;
            var php = settings.PhpInstallations.FirstOrDefault(p => p.Id == site.RuntimeId);
            if (runtime is null && php is null) continue;
            var path = Path.Combine(root, "data", "state", "site-inspection", site.Hostname + ".Caddyfile");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var global = runtime is null ? "" : "{\n frankenphp\n}\n";
            var config = global + $"http://{site.Hostname} {{\n {SiteDirectives(root, site, php)}\n}}\n";
            try
            {
                await File.WriteAllTextAsync(path, config, token);
                var result = await supervisor.RunAsync(new ProcessCommand { ExecutablePath = executable,
                    WorkingDirectory = Path.GetDirectoryName(executable), Arguments = ["adapt", "--config", path] }, TimeSpan.FromSeconds(10), token);
                if (result.ExitCode != 0) continue;
                roots[site.Hostname] = ReadFixedRoot(result.StandardOutput);
            }
            catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                // An unavailable or invalid source has no root to display. Starting the service reports validation errors.
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        return roots;
    }

    internal static string? ReadFixedRoot(string json)
    {
        using var document = JsonDocument.Parse(json);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool uncertain = false;
        Visit(document.RootElement, false);
        return !uncertain && roots.Count == 1 ? roots.Single() : null;

        void Visit(JsonElement node, bool conditional)
        {
            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray()) Visit(item, conditional);
            }
            else if (node.ValueKind == JsonValueKind.Object)
            {
                if (node.TryGetProperty("match", out var match) && match.ValueKind == JsonValueKind.Array)
                    conditional |= match.EnumerateArray().Any(m => m.ValueKind == JsonValueKind.Object && m.EnumerateObject().Any(p => p.Name != "host"));
                if (node.TryGetProperty("handler", out var handler) && handler.GetString() == "vars" && node.TryGetProperty("root", out var root))
                {
                    var value = root.ValueKind == JsonValueKind.String ? root.GetString() : null;
                    if (conditional || value is null || value.Contains('{') || !Path.IsPathFullyQualified(value)) uncertain = true;
                    else roots.Add(Path.GetFullPath(value));
                }
                foreach (var property in node.EnumerateObject()) Visit(property.Value, conditional);
            }
        }
    }
}
