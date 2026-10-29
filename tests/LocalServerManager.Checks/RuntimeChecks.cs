using System.Text;
using System.Text.Json;
using LocalServerManager.Core;
using LocalServerManager.Infrastructure;
using LocalServerManager.Infrastructure.Configuration;
using LocalServerManager.Infrastructure.Runtimes;

namespace LocalServerManager.Checks;

public static class RuntimeChecks
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LocalServerManager-runtime-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var supervisor = new FakeSupervisor();
            var catalog = new RuntimeCatalog(directory, supervisor);
            var phpDirectory = Path.Combine(directory, "fixture-php");
            Directory.CreateDirectory(phpDirectory);
            CreateExecutable(Path.Combine(phpDirectory, "php.exe"));
            CreateExecutable(Path.Combine(phpDirectory, "php-cgi.exe"));
            await File.WriteAllTextAsync(Path.Combine(phpDirectory, "php.ini-development"), "; stock fixture\n");
            var php = new PhpInstallation { Id = "php:8.4", Version = "8.4.18", Channel = "8.4", Directory = phpDirectory, Port = 19084 };
            var nginxDirectory = Path.Combine(directory, "fixture-nginx");
            Directory.CreateDirectory(Path.Combine(nginxDirectory, "conf"));
            CreateExecutable(Path.Combine(nginxDirectory, "nginx.exe"));
            await File.WriteAllTextAsync(Path.Combine(nginxDirectory, "conf", "mime.types"), "types { text/html html; }\n");
            var mailpitFixture = Path.Combine(directory, "mailpit.exe");
            CreateExecutable(mailpitFixture);
            var importedMailpit = await catalog.ImportMailpitAsync(mailpitFixture);
            Require(File.Exists(importedMailpit), "Mailpit import must use its supported version subcommand.");
            var documentRoot = Path.Combine(directory, "site root with spaces");
            Directory.CreateDirectory(documentRoot);
            var site = new SiteDefinition { Id = "app", Hostname = "app.test", DocumentRoot = documentRoot, PhpChannel = "8.4" };
            var settings = new AppSettings { PhpInstallations = [php], DefaultPhpChannel = "8.4", NginxDirectory = nginxDirectory, Sites = [site] };

            await catalog.SaveAsync(settings);
            await catalog.SaveAsync(settings with { LastSection = "Logs" });
            Require(File.Exists(Path.Combine(directory, "settings.json.bak")), "Atomic settings update must retain a backup.");
            Require((await catalog.LoadAsync()).LastSection == "Logs", "Settings roundtrip must preserve values.");
            var corrupted = "{ broken settings";
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), corrupted);
            await ExpectAsync<InvalidDataException>(() => catalog.LoadAsync(), "Corrupt settings must be reported without resetting them.");
            Require(await File.ReadAllTextAsync(Path.Combine(directory, "settings.json")) == corrupted, "Invalid settings must remain recoverable.");
            await catalog.SaveAsync(settings);

            var firstDefinition = await catalog.BuildPhpAsync(php, settings);
            var originalScan = firstDefinition.Command.EnvironmentVariables["PHP_INI_SCAN_DIR"];
            var custom = Path.Combine(directory, "user", "php", "8.4", "conf.d", "20-custom.ini");
            await File.WriteAllTextAsync(custom, "memory_limit=256M\n; unique user content\n");
            var secondDefinition = await catalog.BuildPhpAsync(php, settings);
            var currentScan = secondDefinition.Command.EnvironmentVariables["PHP_INI_SCAN_DIR"];
            Require(originalScan != currentScan, "Applying user INI changes must create a new immutable configuration generation.");
            Require(!((await File.ReadAllTextAsync(Path.Combine(originalScan, "20-custom.ini"))).Contains("unique user content", StringComparison.Ordinal)), "Old CLI configuration snapshots must not mutate.");
            Require((await File.ReadAllTextAsync(custom)).Contains("unique user content", StringComparison.Ordinal), "User INI content must remain intact.");
            Require(secondDefinition.Command.Arguments.Contains("cgi.fix_pathinfo=0"), "Managed FastCGI must disable path guessing.");
            await catalog.UpdateCliSelectionAsync(settings);
            using (var launcher = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "bin", "launcher.json"))))
            {
                Require(launcher.RootElement.GetProperty("scanDirectory").GetString() == currentScan, "CLI must resolve the same committed configuration as CGI.");
                Require(launcher.RootElement.GetProperty("channel").GetString() == "8.4", "CLI must retain the default channel.");
            }

            await catalog.BuildNginxAsync(settings);
            var bootstrap = Path.Combine(directory, "user", "nginx", "generated", "bootstrap.conf");
            var previousBootstrap = await File.ReadAllTextAsync(bootstrap);
            Require(supervisor.LastNginxTest is not null && supervisor.LastNginxTest.Arguments.Contains("-t"), "Nginx candidate must be tested before publication.");
            var validationBootstrap = supervisor.LastNginxTest!.Arguments[2];
            var candidate = supervisor.LastNginxCandidate!;
            Require(validationBootstrap != bootstrap, "Validation must read the complete proposed candidate, not the current bootstrap.");
            Require(Path.GetDirectoryName(validationBootstrap) == Path.GetDirectoryName(bootstrap), "Validation and runtime bootstraps must use the same relative include prefix.");
            Require(!File.Exists(validationBootstrap), "Temporary validation bootstrap must be cleaned after validation.");
            var compiled = await File.ReadAllTextAsync(candidate);
            Require(compiled.Contains("127.0.0.1:8080", StringComparison.Ordinal), "Default Nginx listener must be loopback.");
            var phpInclude = Path.Combine(Path.GetDirectoryName(candidate)!, "app.php.conf");
            Require((await File.ReadAllTextAsync(phpInclude)).Contains("try_files $uri =404;", StringComparison.Ordinal), "PHP forwarding must reject nonexistent scripts.");
            var sourceSite = Path.Combine(directory, "user", "nginx", "sites-available", "app.conf");
            var userContent = await File.ReadAllTextAsync(sourceSite) + "\n# user comment retained\n# include \"C:/comment-only.conf\"; listen 0.0.0.0:9999;\n";
            await File.WriteAllTextAsync(sourceSite, userContent);
            await catalog.BuildNginxAsync(settings);
            Require(await File.ReadAllTextAsync(sourceSite) == userContent, "Building managed configuration must never overwrite user-authored site content.");

            previousBootstrap = await File.ReadAllTextAsync(bootstrap);
            supervisor.FailNginxTest = true;
            await ExpectAsync<InvalidOperationException>(() => catalog.BuildNginxAsync(settings), "Invalid candidate must fail activation.");
            Require(await File.ReadAllTextAsync(bootstrap) == previousBootstrap, "Failed validation must preserve the previous bootstrap bytes.");
            supervisor.FailNginxTest = false;
            var nestedDirectory = Path.Combine(directory, "included snippets");
            Directory.CreateDirectory(nestedDirectory);
            var nested = Path.Combine(nestedDirectory, "entry.conf");
            const string nestedContent = "# Nginx owns interpretation of nested relative paths and globs\ninclude snippets/*.conf;\n";
            await File.WriteAllTextAsync(nested, nestedContent);
            var absoluteSource = userContent.Replace("# user comment retained", "include " + RuntimePaths.NginxQuote(nested) + ";", StringComparison.Ordinal);
            await File.WriteAllTextAsync(sourceSite, absoluteSource);
            await catalog.BuildNginxAsync(settings);
            Require(await File.ReadAllTextAsync(sourceSite) == absoluteSource && await File.ReadAllTextAsync(nested) == nestedContent,
                "Absolute site includes and nested content must be passed through without rewriting user files.");
            var compiledSite = Path.Combine(Path.GetDirectoryName(supervisor.LastNginxCandidate)!, "app.conf");
            Require((await File.ReadAllTextAsync(compiledSite)).Contains("include " + RuntimePaths.NginxQuote(nested) + ";", StringComparison.Ordinal), "Candidate must retain the absolute include for nginx -t.");
            previousBootstrap = await File.ReadAllTextAsync(bootstrap);
            await File.WriteAllTextAsync(sourceSite, userContent.Replace("# user comment retained", "include snippets/live.conf;", StringComparison.Ordinal));
            await ExpectAsync<InvalidDataException>(() => catalog.BuildNginxAsync(settings), "Direct relative site includes must be rejected.");
            Require(await File.ReadAllTextAsync(bootstrap) == previousBootstrap, "Rejected relative includes must not replace working routing.");

            await File.WriteAllTextAsync(sourceSite, userContent.Replace("listen 127.0.0.1:8080;", "listen 0.0.0.0:8080;", StringComparison.Ordinal));
            await ExpectAsync<InvalidDataException>(() => catalog.BuildNginxAsync(settings), "Non-loopback listeners must not activate silently.");
            Require(await File.ReadAllTextAsync(bootstrap) == previousBootstrap, "Rejected listener changes must preserve working routing.");
            await File.WriteAllTextAsync(sourceSite, userContent);
            await ExpectAsync<InvalidDataException>(() => catalog.BuildNginxAsync(settings with { Sites = [site with { Hostname = "other.test" }] }), "Source/metadata hostname mismatches must be reported without overwriting the source.");
            Require(await File.ReadAllTextAsync(sourceSite) == userContent, "A metadata mismatch must preserve source bytes.");
            await using (var manager = new EnvironmentManager(directory, supervisor, catalog))
            {
                await manager.InitializeAsync();
                await manager.StartServiceAsync("nginx");
                var stopsBeforeFailure = supervisor.StopCalls;
                var stableBootstrap = await File.ReadAllTextAsync(bootstrap);
                supervisor.FailNginxTest = true;
                await ExpectAsync<InvalidOperationException>(() => manager.SetSiteEnabledAsync(site.Id, false),
                    "The coordinator must reject an invalid candidate without interrupting live routing.");
                Require(supervisor.StopCalls == stopsBeforeFailure, "Candidate validation failure must not stop the working Nginx process.");
                Require(await File.ReadAllTextAsync(bootstrap) == stableBootstrap, "Candidate rejection must preserve active bootstrap bytes.");
                Require(manager.Snapshot.Sites.Single().Enabled, "Rejected candidate must not commit the new site selection.");
                supervisor.FailNginxTest = false;
            }
            await catalog.SaveAsync(settings with { NginxDirectory = null, Sites = [] });
            await using (var manager = new EnvironmentManager(directory, supervisor, catalog))
            {
                await manager.InitializeAsync();
                var disabled = site with { Id = "disabled", Hostname = "disabled.test", Enabled = false };
                await manager.SaveSiteAsync(disabled);
                var disabledSource = Path.Combine(directory, "user", "nginx", "sites-available", "disabled.conf");
                Require(File.Exists(disabledSource), "Saving a disabled site without Nginx must create an editable source file.");
                var edited = await File.ReadAllTextAsync(disabledSource) + "\n# user-owned disabled config\n";
                await File.WriteAllTextAsync(disabledSource, edited);
                await manager.SaveSiteAsync(disabled);
                await catalog.EnsureSiteSourceAsync(disabled);
                Require(await File.ReadAllTextAsync(disabledSource) == edited, "Ensuring/saving a site must not overwrite its edited source.");
            }
            RequireThrows<ArgumentException>(() => RuntimePaths.NginxQuote("C:/project/$nginx_injection"));
            RequireThrows<ArgumentException>(() => RuntimePaths.SafeIdentifier("../escape"));
            Console.WriteLine("PASS: atomic settings backups and corrupt-file preservation.");
            Console.WriteLine("PASS: immutable PHP configuration snapshots, native CLI selection, and CGI path guards.");
            Console.WriteLine("PASS: complete candidate validation, user-source preservation, loopback-only routing, and source reconciliation errors.");
            Console.WriteLine("PASS: coordinator candidate rejection preserves live services and committed site selection.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task ExpectAsync<T>(Func<Task> operation, string message) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }
    private static void RequireThrows<T>(Action operation) where T : Exception
    {
        try { operation(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected input validation exception " + typeof(T).Name);
    }
    private static void CreateExecutable(string path)
    {
        using var writer = new BinaryWriter(File.Create(path), Encoding.UTF8);
        writer.Write(new byte[128]);
        writer.BaseStream.Position = 0;
        writer.Write((ushort)0x5A4D);
        writer.BaseStream.Position = 0x3C;
        writer.Write(64);
        writer.BaseStream.Position = 64;
        writer.Write((uint)0x00004550);
        writer.Write((ushort)0x8664);
    }

    private sealed class FakeSupervisor : IProcessSupervisor
    {
        public bool FailNginxTest { get; set; }
        public ProcessCommand? LastNginxTest { get; private set; }
        public string? LastNginxCandidate { get; private set; }
        public int StopCalls { get; private set; }
        private readonly HashSet<string> _running = new(StringComparer.Ordinal);
        public event EventHandler<ServiceSnapshot>? ServiceChanged { add { } remove { } }
        public IReadOnlyList<ServiceSnapshot> Snapshots => [];
        public bool IsRunning(string id) => _running.Contains(id);
        public Task StartAsync(ManagedServiceDefinition definition, CancellationToken cancellationToken = default) { _running.Add(definition.Id); return Task.CompletedTask; }
        public Task StopAsync(string id, TimeSpan timeout, CancellationToken cancellationToken = default) { StopCalls++; _running.Remove(id); return Task.CompletedTask; }
        public Task StopAllAsync(TimeSpan timeout, CancellationToken cancellationToken = default) { _running.Clear(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<ProcessResult> RunAsync(ProcessCommand command, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (Path.GetFileName(command.ExecutablePath).Equals("mailpit.exe", StringComparison.OrdinalIgnoreCase))
            {
                Require(command.Arguments.SequenceEqual(["version"]), "Mailpit exposes a version subcommand, not a --version flag.");
                return Task.FromResult(new ProcessResult(0, "Mailpit v1.30.0\n", ""));
            }
            if (command.Arguments.Contains("-t"))
            {
                LastNginxTest = command;
                var wrapper = File.ReadAllText(command.Arguments[2]);
                LastNginxCandidate = System.Text.RegularExpressions.Regex.Match(wrapper, "include\\s+\"([^\"]+)\";").Groups[1].Value;
                return Task.FromResult(new ProcessResult(FailNginxTest ? 1 : 0, "", FailNginxTest ? "invalid fixture configuration" : "configuration test is successful"));
            }
            return Task.FromResult(new ProcessResult(0, "PHP 8.4.18 (cli) (NTS Visual C++ 2022 x64)\n", ""));
        }
    }
}
