using System.Text;
using System.Text.Json;
using PhpDevTray.Core;
using PhpDevTray.Infrastructure;
using PhpDevTray.Infrastructure.Configuration;
using PhpDevTray.Infrastructure.Runtimes;

namespace PhpDevTray.Checks;

public static class RuntimeChecks
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PhpDevTray-runtime-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var rootJson = "{\"handler\":\"vars\",\"root\":\"C:/site\"}";
            Require(CaddyStack.ReadFixedRoot(rootJson) == Path.GetFullPath("C:/site"), "A fixed root must be recognized.");
            Require(CaddyStack.ReadFixedRoot("[{\"handler\":\"vars\",\"root\":\"C:/a\"},{\"handler\":\"vars\",\"root\":\"C:/b\"}]") is null, "Multiple roots must not be simplified.");
            Require(CaddyStack.ReadFixedRoot("{\"handler\":\"vars\",\"root\":\"{http.request.host}\"}") is null, "Dynamic roots cannot be displayed as paths.");
            Require(CaddyStack.ReadFixedRoot("{\"match\":[{\"path\":[\"/a/*\"]}],\"handle\":[" + rootJson + "]}") is null, "A conditional root must not be displayed as an unconditional path.");
            await using var supervisor = new FakeSupervisor();
            var catalog = new RuntimeCatalog(directory, supervisor);
            var phpDirectory = Path.Combine(directory, "fixture-php");
            Directory.CreateDirectory(phpDirectory);
            CreateExecutable(Path.Combine(phpDirectory, "php.exe"));
            CreateExecutable(Path.Combine(phpDirectory, "php-cgi.exe"));
            await File.WriteAllTextAsync(Path.Combine(phpDirectory, "php.ini-development"), "; stock fixture\n");
            supervisor.FailVisualCppCheck = true;
            await ExpectAsync<VisualCppRuntimeException>(() => catalog.ImportPhpAsync(Path.Combine(phpDirectory, "php.exe"), new AppSettings()), "An incompatible Microsoft runtime must identify the required prerequisite.");
            supervisor.FailVisualCppCheck = false;
            var php = new PhpInstallation { Id = "php:8.4", Version = "8.4.18", Channel = "8.4", Directory = phpDirectory, Port = 19084 };
            var mailpitFixture = Path.Combine(directory, "mailpit.exe");
            CreateExecutable(mailpitFixture);
            var importedMailpit = await catalog.ImportMailpitAsync(mailpitFixture);
            Require(File.Exists(importedMailpit), "Mailpit import must use its supported version subcommand.");
            var settings = new AppSettings { PhpInstallations = [php], DefaultPhpChannel = "8.4" };

            var probes = new List<int>();
            var reservedSettings = settings with
            {
                PhpInstallations = [php with { Port = 9087 }],
                SmtpPort = 9085, MailpitHttpPort = 9086
            };
            var allocated = RuntimeCatalog.AllocatePort(9085, reservedSettings, port => { probes.Add(port); return port != 9088; });
            Require(allocated == 9089 && probes.SequenceEqual([9088,9089]),
                "PHP allocation must skip configured site/service ports and unavailable bind probes.");
            probes.Clear();
            allocated = RuntimeCatalog.AllocatePort(60000, settings, port => { probes.Add(port); return port == 9000; });
            Require(allocated == 9000 && probes.SequenceEqual([49151,9000]),
                "PHP allocation must wrap below 49152 without probing Windows' default dynamic range.");
            var attempted = 0;
            RequireThrows<InvalidOperationException>(() => RuntimeCatalog.AllocatePort(49151, settings, port =>
            {
                Require(port is >= 9000 and < 49152, "Automatic PHP allocation entered the dynamic range.");
                attempted++;
                return false;
            }));
            Require(attempted > 0 && attempted <= 49152 - 9000, "Port allocation must terminate when all candidates are unavailable.");
            var mail = await catalog.BuildMailpitAsync(settings with { MailpitExecutable = importedMailpit });
            Require(mail.Command.Arguments.Contains("127.0.0.1:39327") && mail.HealthEndpoints.Any(e => e.Kind == HealthCheckKind.Smtp && e.Port == 39327),
                "Mailpit command and readiness checks must agree on the new SMTP default.");
            Console.WriteLine("PASS: uncommon SMTP default and bounded PHP allocation avoid dynamic ports, configured endpoints and unavailable bind probes.");

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
            Require((await File.ReadAllTextAsync(Path.Combine(secondDefinition.Command.EnvironmentVariables["PHP_INI_SCAN_DIR"], "90-managed.ini"))).Contains("smtp_port=39327", StringComparison.Ordinal),
                "Generated PHP mail configuration must use the same SMTP default as Mailpit.");
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

            RequireThrows<ArgumentException>(() => RuntimePaths.SafeIdentifier("../escape"));
            Console.WriteLine("PASS: atomic settings backups and corrupt-file preservation.");
            Console.WriteLine("PASS: immutable PHP configuration snapshots, native CLI selection, and CGI path guards.");
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
        public bool FailVisualCppCheck { get; set; }
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
            if (FailVisualCppCheck) return Task.FromResult(new ProcessResult(1, "", @"PHP Warning:  'C:\Windows\SYSTEM32\VCRUNTIME140.dll' 14.44 is not compatible with this PHP build linked with 14.51 in Unknown on line 0"));
            return Task.FromResult(new ProcessResult(0, "PHP 8.4.18 (cli) (NTS Visual C++ 2022 x64)\n", ""));
        }
    }
}
