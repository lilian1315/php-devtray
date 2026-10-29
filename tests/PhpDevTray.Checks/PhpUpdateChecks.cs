using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PhpDevTray.Core;
using PhpDevTray.Infrastructure;
using PhpDevTray.Infrastructure.Runtimes;

namespace PhpDevTray.Checks;

/// <summary>Exercises actual update coordination and file publication, with only the native process boundary controlled.</summary>
internal static class PhpUpdateChecks
{
    public static async Task RunAsync()
    {
        await StoppedUpdateAndRollbackAsync();
        await RunningUpdateAsync();
        await RejectedCandidateAsync();
        await FailedActivationAsync(cancel: false);
        await FailedActivationAsync(cancel: true);
        await FailedSettingsCommitAsync();
        await UserEditDuringActivationAsync();
        await UserEditDuringActivationAsync(duringModuleValidation: true);
        await NondefaultUpdateLeavesPendingDefaultEditsAsync();
    }

    private static async Task StoppedUpdateAndRollbackAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.DistributionAsync("8.4.19");
        var original = fixture.Manager.Snapshot.PhpInstallations.Single(php => php.Channel == "8.4");
        await fixture.Manager.RegisterPhpAsync(Path.Combine(source, "php.exe"));
        var updated = fixture.Manager.Snapshot.PhpInstallations.Single(php => php.Channel == "8.4");
        Check(fixture.Supervisor.Events.Count == 0, "Updating a stopped channel must not start or stop any service.");
        fixture.CheckPolicy(updated, "8.4.19");
        Check(updated.Directory != source && updated.Directory != original.Directory, "An update must use a separate managed distribution.");
        Check(await fixture.CliExecutableAsync() == Path.Combine(updated.Directory, "php.exe"), "CLI selection did not follow the active patch.");
        Check(fixture.Manager.Snapshot.RetainedPhpVersions.Single().Directory == original.Directory, "The previous patch must be retained after activation.");
        Check((await fixture.Catalog.LoadAsync()).RetainedPhpVersions.Single().Version == "8.4.18", "Retained patches must survive settings reload.");
        await fixture.CheckUserFilesAsync();

        var validSettings = await fixture.Catalog.LoadAsync();
        var retained = validSettings.RetainedPhpVersions.Single();
        var durableSettings = await File.ReadAllBytesAsync(fixture.SettingsPath);
        await RejectAsync(() => fixture.Catalog.SaveAsync(validSettings with { RetainedPhpVersions = [retained with { Directory = source }] }), "Retained paths outside managed storage must be rejected.");
        await RejectAsync(() => fixture.Catalog.SaveAsync(validSettings with { RetainedPhpVersions = [retained with { Channel = "8.5" }] }), "Retained channel/version conflicts must be rejected.");
        await RejectAsync(() => fixture.Catalog.SaveAsync(validSettings with { RetainedPhpVersions = [retained with { Directory = updated.Directory }] }), "A retained record must not alias active binaries.");
        await RejectAsync(() => fixture.Catalog.SaveAsync(validSettings with { RetainedPhpVersions = [retained, retained] }), "Duplicate retained versions must be rejected.");
        Check(await MatchesFileAsync(durableSettings, fixture.SettingsPath), "Rejected retained metadata changed durable settings.");

        var retainedVersionMarker = Path.Combine(original.Directory, "version.fixture");
        await File.WriteAllTextAsync(retainedVersionMarker, "8.4.99");
        await RejectAsync(() => fixture.Manager.ActivatePhpVersionAsync("8.4", "8.4.18"), "Retained executables must match their recorded version before activation.");
        Check(fixture.Manager.Snapshot.PhpInstallations.Single(php => php.Channel == "8.4").Version == "8.4.19" && await fixture.CliExecutableAsync() == Path.Combine(updated.Directory, "php.exe"), "A tampered retained version changed the active channel or CLI.");
        await File.WriteAllTextAsync(retainedVersionMarker, "8.4.18");

        // A rollback uses today's desired policy, rather than restoring old extension settings.
        await fixture.Manager.SetPhpExtensionEnabledAsync("8.4", "opcache", true);
        await fixture.Manager.ActivatePhpVersionAsync("8.4", "8.4.18");
        var rolledBack = fixture.Manager.Snapshot.PhpInstallations.Single(php => php.Channel == "8.4");
        fixture.CheckPolicy(rolledBack, "8.4.18", ["gd", "opcache"]);
        Check(rolledBack.Directory == original.Directory, "Rollback must activate the retained binaries, not copy over them.");
        Check(await fixture.CliExecutableAsync() == Path.Combine(original.Directory, "php.exe"), "Rollback did not restore the CLI executable.");
        Check(fixture.Manager.Snapshot.RetainedPhpVersions.Single().Directory == updated.Directory, "Rollback must retain the patch being left.");
        Check(fixture.Supervisor.Events.Count == 0, "A stopped rollback or extension change must leave the environment stopped.");
        await fixture.Manager.ActivatePhpVersionAsync("8.4", "8.4.19");
        fixture.CheckPolicy(fixture.Manager.Snapshot.PhpInstallations.Single(php => php.Channel == "8.4"), "8.4.19", ["gd", "opcache"]);
        Check(fixture.Manager.Snapshot.RetainedPhpVersions.Count == 1, "Repeated switches must not duplicate retained versions.");
        await RejectAsync(() => fixture.Manager.RegisterPhpAsync(Path.Combine(source, "php.exe")), "Identical active patches must be rejected.");
        await RejectAsync(() => fixture.Manager.ActivatePhpVersionAsync("8.4", "8.4.99"), "Unknown retained versions must be rejected.");
        Check(File.Exists(Path.Combine(original.Directory, "php.exe")) && File.Exists(Path.Combine(updated.Directory, "php.exe")), "Switching or rejecting a selection deleted a retained binary.");
        await fixture.CheckUserFilesAsync();
        Console.WriteLine("PASS: stopped PHP patch updates and retained rollback preserve current channel policy, sites, user INI, CLI default, binaries and persisted history without starting services.");
    }

    private static async Task RunningUpdateAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.StartEnvironmentAsync();
        var sibling = fixture.Supervisor.Running["php:8.5"];
        var source = await fixture.DistributionAsync("8.4.19");
        await fixture.Manager.RegisterPhpAsync(Path.Combine(source, "php.exe"));
        CheckTransition(fixture.Supervisor.Events, "8.4.19");
        Check(ReferenceEquals(sibling, fixture.Supervisor.Running["php:8.5"]), "A patch update restarted an unrelated PHP channel.");
        Check(fixture.Supervisor.Running["php:8.4"].Version == "8.4.19" && fixture.Supervisor.IsRunning("nginx"), "The new PHP patch and dependent Nginx must be healthy before committing.");
        fixture.CheckPolicy(fixture.Manager.Snapshot.PhpInstallations.Single(php => php.Channel == "8.4"), "8.4.19");
        fixture.Supervisor.Events.Clear();
        await fixture.Manager.ActivatePhpVersionAsync("8.4", "8.4.18");
        CheckTransition(fixture.Supervisor.Events, "8.4.18");
        Check(ReferenceEquals(sibling, fixture.Supervisor.Running["php:8.5"]), "Rollback restarted an unrelated channel.");
        await fixture.CheckUserFilesAsync();
        Console.WriteLine("PASS: running PHP updates and rollback drain dependent Nginx, transition only the selected channel, verify readiness and resume Nginx in order.");
    }

    private static async Task RejectedCandidateAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.StartEnvironmentAsync();
        var settings = await File.ReadAllBytesAsync(fixture.SettingsPath);
        var cli = await File.ReadAllBytesAsync(fixture.CliPath);
        var source = await fixture.DistributionAsync("8.4.19");
        File.Delete(Path.Combine(source, "ext", "php_gd.dll"));
        await RejectAsync(() => fixture.Manager.RegisterPhpAsync(Path.Combine(source, "php.exe")), "A candidate without a desired extension must be rejected before transition.");
        Check(fixture.Supervisor.Events.Count == 0, "A bad candidate stopped healthy services.");
        Check(await MatchesFileAsync(settings, fixture.SettingsPath) && await MatchesFileAsync(cli, fixture.CliPath), "Prevalidation failure changed committed settings or CLI selection.");
        Check(Directory.GetDirectories(fixture.ManagedPhpRoot).Length == 2, "Failed prevalidation leaked a new managed distribution.");

        source = await fixture.DistributionAsync("8.4.20");
        await File.WriteAllTextAsync(Path.Combine(source, "cgi-version.fixture"), "8.4.21");
        await RejectAsync(() => fixture.Manager.RegisterPhpAsync(Path.Combine(source, "php.exe")), "CLI/CGI version mismatches must be rejected.");
        Check(fixture.Supervisor.Events.Count == 0 && Directory.GetDirectories(fixture.ManagedPhpRoot).Length == 2, "A CLI/CGI mismatch caused a transition or leaked its staged binaries.");
        source = await fixture.DistributionAsync("8.4.22");
        fixture.Supervisor.SuppressedModuleVersion = "8.4.22";
        await RejectAsync(() => fixture.Manager.RegisterPhpAsync(Path.Combine(source, "php.exe")), "A desired DLL that silently fails to load must be rejected.");
        Check(fixture.Supervisor.Events.Count == 0 && Directory.GetDirectories(fixture.ManagedPhpRoot).Length == 2, "A silently missing module caused a transition or leaked staging.");
        Check(fixture.Supervisor.Running["php:8.4"].Version == "8.4.18" && fixture.Supervisor.IsRunning("nginx"), "Prevalidation failure disturbed the old environment.");
        await fixture.CheckUserFilesAsync();
        Console.WriteLine("PASS: missing DLLs, silent desired-module load failures and mismatched CLI/CGI versions fail before service transition, settings publication or CLI changes, and staging is removed.");
    }

    private static async Task FailedActivationAsync(bool cancel)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.StartEnvironmentAsync();
        var settings = await File.ReadAllBytesAsync(fixture.SettingsPath);
        var cli = await File.ReadAllBytesAsync(fixture.CliPath);
        var bootstrap = await File.ReadAllBytesAsync(fixture.BootstrapPath);
        var source = await fixture.DistributionAsync("8.4.19");
        using var cancellation = new CancellationTokenSource();
        fixture.Supervisor.StartHook = (definition, token) =>
        {
            if (definition.Id != "php:8.4" || definition.Version != "8.4.19") return;
            if (cancel) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            throw new InvalidOperationException("Fixture candidate could not become ready.");
        };
        await RejectAsync(() => fixture.Manager.RegisterPhpAsync(Path.Combine(source, "php.exe"), cancellation.Token), "A failing candidate must not be committed.", allowCancellation: cancel);
        Check(!cancel || cancellation.IsCancellationRequested, "The cancellation scenario did not reach candidate startup.");
        Check(fixture.Supervisor.Running["php:8.4"].Version == "8.4.18" && fixture.Supervisor.IsRunning("nginx") && fixture.Supervisor.IsRunning("php:8.5"), "Failed activation did not recover the previous running environment.");
        Check(fixture.Supervisor.Events.Any(item => item == "start:php:8.4:8.4.19") && fixture.Supervisor.Events.LastIndexOf("start:php:8.4:8.4.18") > fixture.Supervisor.Events.IndexOf("start:php:8.4:8.4.19"), "Recovery must start the previous PHP only after the failed candidate is stopped.");
        Check(await MatchesFileAsync(settings, fixture.SettingsPath) && await MatchesFileAsync(cli, fixture.CliPath), "Failed activation changed committed settings or CLI selection.");
        Check(await MatchesFileAsync(bootstrap, fixture.BootstrapPath), "Failed activation changed Nginx bootstrap routing.");
        Check(fixture.Manager.Snapshot.RetainedPhpVersions.Count == 0 && fixture.Manager.Snapshot.PhpInstallations.Single(php => php.Channel == "8.4").Version == "8.4.18", "A failed activation published its version or retained history.");
        Check(Directory.GetDirectories(fixture.ManagedPhpRoot).Length == 2, "A failed activation leaked or removed a managed binary directory.");
        await fixture.CheckUserFilesAsync();
        Console.WriteLine(cancel
            ? "PASS: cancellation during candidate startup restores the previous running PHP/Nginx with uncanceled recovery and leaves settings, CLI and bootstrap unchanged."
            : "PASS: candidate readiness failure restores the previous running PHP/Nginx and leaves settings, CLI, bootstrap and history unchanged.");
    }

    private static async Task FailedSettingsCommitAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var settings = await File.ReadAllBytesAsync(fixture.SettingsPath);
        var cli = await File.ReadAllBytesAsync(fixture.CliPath);
        var source = await fixture.DistributionAsync("8.4.19");
        // Holding a real no-delete handle makes the atomic settings replacement fail after candidate/CLI preparation.
        using (var settingsLock = new FileStream(fixture.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await RejectAsync(() => fixture.Manager.RegisterPhpAsync(Path.Combine(source, "php.exe")), "A settings publication failure must roll back the candidate.");
        Check(await MatchesFileAsync(settings, fixture.SettingsPath) && await MatchesFileAsync(cli, fixture.CliPath), "Settings publication failure left settings or CLI pointing at the candidate.");
        Check(fixture.Manager.Snapshot.PhpInstallations.Single(php => php.Channel == "8.4").Version == "8.4.18" && fixture.Manager.Snapshot.RetainedPhpVersions.Count == 0, "Settings failure changed active or retained state.");
        Check(fixture.Supervisor.Events.Count == 0 && Directory.GetDirectories(fixture.ManagedPhpRoot).Length == 2, "A stopped settings failure started services, deleted old binaries or leaked staging.");
        await fixture.CheckUserFilesAsync();
        Console.WriteLine("PASS: real atomic-settings replacement failure restores the CLI and old active/retained state without starting services or losing binaries.");
    }

    private static async Task UserEditDuringActivationAsync(bool duringModuleValidation = false)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.StartEnvironmentAsync();
        var settings = await File.ReadAllBytesAsync(fixture.SettingsPath);
        var cli = await File.ReadAllBytesAsync(fixture.CliPath);
        var bootstrap = await File.ReadAllBytesAsync(fixture.BootstrapPath);
        var oldDefinition = fixture.Supervisor.Running["php:8.4"];
        var previousResolvedCustom = await File.ReadAllBytesAsync(Path.Combine(oldDefinition.Command.EnvironmentVariables["PHP_INI_SCAN_DIR"], "20-custom.ini"));
        var source = await fixture.DistributionAsync("8.4.19");
        var customPath = Path.Combine(fixture.Root, "user", "php", "8.4", "conf.d", "20-custom.ini");
        var editedCustom = "; Changed by the user while candidate readiness was pending\r\nmemory_limit=256M\r\n";
        var editRevision = 0;
        if (duringModuleValidation) fixture.Supervisor.ModuleValidationHook = command =>
        {
            if (File.ReadAllText(Path.Combine(command.WorkingDirectory!, "version.fixture")).Trim() != "8.4.19") return;
            // Every successful candidate -m result gets a subsequent source edit, so the most recently tested
            // snapshot must remain the activation target rather than being rebuilt from these newer contents.
            editRevision++;
            editedCustom = $"; User edit after candidate module validation {editRevision}\r\nmemory_limit={256 + editRevision}M\r\n";
            File.WriteAllText(customPath, editedCustom);
        };
        else fixture.Supervisor.StartHook = (definition, _) =>
        {
            if (definition.Id == "php:8.4" && definition.Version == "8.4.19") { editRevision++; File.WriteAllText(customPath, editedCustom); }
        };
        await RejectAsync(() => fixture.Manager.RegisterPhpAsync(Path.Combine(source, "php.exe")), "A user edit during candidate startup must prevent publication.");
        Check(editRevision > 0, "The INI drift check did not edit a candidate source at the intended command boundary.");
        Check(await File.ReadAllTextAsync(customPath) == editedCustom, "Activation recovery discarded the user's concurrent INI edit.");
        Check(ReferenceEquals(fixture.Supervisor.Running["php:8.4"], oldDefinition) && fixture.Supervisor.IsRunning("nginx"), "INI drift recovery must resume the previous validated service definitions.");
        Check(await MatchesFileAsync(previousResolvedCustom, Path.Combine(oldDefinition.Command.EnvironmentVariables["PHP_INI_SCAN_DIR"], "20-custom.ini")), "Concurrent source editing mutated the old immutable configuration.");
        Check(await MatchesFileAsync(settings, fixture.SettingsPath) && await MatchesFileAsync(cli, fixture.CliPath) && await MatchesFileAsync(bootstrap, fixture.BootstrapPath), "INI drift recovery changed committed settings, CLI or routing.");
        Check(fixture.Manager.Snapshot.RetainedPhpVersions.Count == 0 && Directory.GetDirectories(fixture.ManagedPhpRoot).Length == 2, "INI drift published history or leaked candidate files.");
        Console.WriteLine(duringModuleValidation
            ? "PASS: a source edit after candidate module validation cannot rebuild the tested configuration; activation rejects drift and preserves the user's edit and previous service definitions."
            : "PASS: a user INI edit during candidate readiness cancels publication, preserves the edit, and resumes the previous immutable PHP/Nginx configuration.");
    }

    private static async Task NondefaultUpdateLeavesPendingDefaultEditsAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.StartEnvironmentAsync();
        await fixture.Manager.SetDefaultPhpAsync("8.5");
        var sibling = fixture.Manager.Snapshot.PhpInstallations.Single(php => php.Channel == "8.5");
        var siblingDefinition = fixture.Supervisor.Running["php:8.5"];
        var cli = await File.ReadAllBytesAsync(fixture.CliPath);
        var currentCli = await fixture.CliExecutableAsync();
        var pendingSource = Path.Combine(fixture.Root, "user", "php", "8.5", "conf.d", "20-custom.ini");
        const string invalidPendingEdit = "; Pending user edit, unrelated to PHP 8.4\nextension=missing-fixture-extension.dll\n";
        await File.WriteAllTextAsync(pendingSource, invalidPendingEdit);
        var source = await fixture.DistributionAsync("8.4.19");
        fixture.Supervisor.Events.Clear();
        fixture.Supervisor.Commands.Clear();
        await fixture.Manager.RegisterPhpAsync(Path.Combine(source, "php.exe"));
        fixture.CheckPolicy(fixture.Manager.Snapshot.PhpInstallations.Single(php => php.Channel == "8.4"), "8.4.19", defaultChannel: "8.5");
        Check(await MatchesFileAsync(cli, fixture.CliPath) && await fixture.CliExecutableAsync() == currentCli, "A nondefault patch update republished the unrelated CLI target.");
        Check(!fixture.Supervisor.Commands.Any(command => command.ExecutablePath.StartsWith(sibling.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)), "A nondefault update revalidated an unrelated default channel's pending source edits.");
        Check(ReferenceEquals(fixture.Supervisor.Running["php:8.5"], siblingDefinition), "A nondefault patch update disturbed the default channel's running process.");
        Check(await File.ReadAllTextAsync(pendingSource) == invalidPendingEdit, "A nondefault update rewrote the unrelated pending user edit.");
        CheckTransition(fixture.Supervisor.Events, "8.4.19");
        await fixture.CheckUserFilesAsync();
        Console.WriteLine("PASS: a nondefault PHP patch update does not validate pending edits, restart processes, or rewrite the immutable CLI selection of an unrelated default channel.");
    }

    private static void CheckTransition(List<string> events, string targetVersion)
    {
        Check(events.SequenceEqual(["stop:nginx", "stop:php:8.4", $"start:php:8.4:{targetVersion}", "start:nginx:1.30.0"]), "Incorrect PHP/Nginx transition order: " + string.Join(", ", events));
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static async Task<bool> MatchesFileAsync(byte[] expected, string path)
    {
        var actual = await File.ReadAllBytesAsync(path);
        return expected.AsSpan().SequenceEqual(actual);
    }

    private static async Task RejectAsync(Func<Task> action, string message, bool allowCancellation = false)
    {
        try { await action(); }
        catch (OperationCanceledException) when (allowCancellation) { return; }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string CustomText = "; User-owned bytes é\r\nmemory_limit=192M\r\n";
        public required string Root { get; init; }
        public required UpdateSupervisor Supervisor { get; init; }
        public required RuntimeCatalog Catalog { get; init; }
        public required EnvironmentManager Manager { get; init; }
        public string ManagedPhpRoot => Path.Combine(Root, "runtimes", "php");
        public string SettingsPath => Path.Combine(Root, "settings.json");
        public string CliPath => Path.Combine(Root, "bin", "launcher.json");
        public string BootstrapPath => Path.Combine(Root, "user", "nginx", "generated", "bootstrap.conf");

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PhpDevTray.PhpUpdates." + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            var supervisor = new UpdateSupervisor();
            var catalog = new RuntimeCatalog(root, supervisor);
            var old = await CreateDistributionAsync(Path.Combine(root, "runtimes", "php", "8.4.18"), "8.4.18");
            var sibling = await CreateDistributionAsync(Path.Combine(root, "runtimes", "php", "8.5.4"), "8.5.4");
            var nginx = Path.Combine(root, "runtimes", "nginx", "1.30.0");
            Directory.CreateDirectory(Path.Combine(nginx, "conf"));
            CreateExecutable(Path.Combine(nginx, "nginx.exe"));
            await File.WriteAllTextAsync(Path.Combine(nginx, "conf", "mime.types"), "types { text/html html; }\n");
            var documentRoot = Path.Combine(root, "project");
            Directory.CreateDirectory(documentRoot);
            var custom = Path.Combine(root, "user", "php", "8.4", "conf.d", "20-custom.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(custom)!);
            await File.WriteAllTextAsync(custom, CustomText);
            await catalog.SaveAsync(new AppSettings
            {
                PhpInstallations =
                [
                    new PhpInstallation { Id = "php:8.4", Channel = "8.4", Version = "8.4.18", Directory = old, Port = 19084, AutoStart = false, MailIntegrationEnabled = false, EnabledExtensions = ["gd"] },
                    new PhpInstallation { Id = "php:8.5", Channel = "8.5", Version = "8.5.4", Directory = sibling, Port = 19085 }
                ],
                DefaultPhpChannel = "8.4", NginxDirectory = nginx,
                Sites = [new SiteDefinition { Id = "project", Hostname = "project.test", DocumentRoot = documentRoot, PhpChannel = "8.4" }]
            });
            var manager = new EnvironmentManager(root, supervisor, catalog);
            await manager.InitializeAsync();
            supervisor.Events.Clear();
            return new Fixture { Root = root, Supervisor = supervisor, Catalog = catalog, Manager = manager };
        }

        public async Task StartEnvironmentAsync()
        {
            await Manager.StartServiceAsync("php:8.5");
            await Manager.StartServiceAsync("nginx");
            Supervisor.Events.Clear();
        }

        public Task<string> DistributionAsync(string version) => CreateDistributionAsync(Path.Combine(Root, "imports", version), version);

        public async Task<string?> CliExecutableAsync()
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(CliPath));
            return json.RootElement.GetProperty("executablePath").GetString();
        }

        public void CheckPolicy(PhpInstallation installation, string version, string[]? extensions = null, string defaultChannel = "8.4")
        {
            Check(installation is { Id: "php:8.4", Channel: "8.4", Port: 19084, AutoStart: false, MailIntegrationEnabled: false } && installation.Version == version, "Patch activation changed stable channel policy.");
            Check(installation.EnabledExtensions.SequenceEqual(extensions ?? ["gd"]), "Patch activation did not preserve today's extension policy.");
            Check(Manager.Snapshot.DefaultPhpChannel == defaultChannel && !Manager.Snapshot.PathIntegrationEnabled && Manager.Snapshot.Sites.Single() is { Id: "project", PhpChannel: "8.4", Hostname: "project.test" }, "Patch activation changed site assignment, default CLI channel or PATH integration.");
        }

        public async Task CheckUserFilesAsync()
        {
            Check((await File.ReadAllBytesAsync(Path.Combine(Root, "user", "php", "8.4", "conf.d", "20-custom.ini"))).SequenceEqual(Encoding.UTF8.GetBytes(CustomText)), "Patch operations rewrote user INI bytes.");
            Check(File.Exists(Path.Combine(ManagedPhpRoot, "8.4.18", "php.exe")) && File.Exists(Path.Combine(ManagedPhpRoot, "8.5.4", "php.exe")), "Patch cleanup removed an existing binary directory.");
        }

        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Root.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(Root).StartsWith("PhpDevTray.PhpUpdates.", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing unsafe fixture cleanup.");
            Directory.Delete(Root, recursive: true);
        }
    }

    private static async Task<string> CreateDistributionAsync(string directory, string version)
    {
        Directory.CreateDirectory(Path.Combine(directory, "ext"));
        CreateExecutable(Path.Combine(directory, "php.exe"));
        CreateExecutable(Path.Combine(directory, "php-cgi.exe"));
        await File.WriteAllTextAsync(Path.Combine(directory, "version.fixture"), version);
        await File.WriteAllTextAsync(Path.Combine(directory, "php.ini-development"), "; Immutable stock configuration\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "ext", "php_gd.dll"), "fixture-compatible");
        await File.WriteAllTextAsync(Path.Combine(directory, "ext", "php_opcache.dll"), "fixture-compatible");
        return directory;
    }

    private static void CreateExecutable(string path)
    {
        using var writer = new BinaryWriter(File.Create(path), Encoding.UTF8);
        writer.Write(new byte[128]); writer.BaseStream.Position = 0; writer.Write((ushort)0x5A4D);
        writer.BaseStream.Position = 0x3C; writer.Write(64); writer.BaseStream.Position = 64;
        writer.Write((uint)0x00004550); writer.Write((ushort)0x8664);
    }

    private sealed class UpdateSupervisor : IProcessSupervisor
    {
        public Dictionary<string, ManagedServiceDefinition> Running { get; } = new(StringComparer.Ordinal);
        public List<string> Events { get; } = [];
        public List<ProcessCommand> Commands { get; } = [];
        public Action<ManagedServiceDefinition, CancellationToken>? StartHook { get; set; }
        public Action<ProcessCommand>? ModuleValidationHook { get; set; }
        public string? SuppressedModuleVersion { get; set; }
        public event EventHandler<ServiceSnapshot>? ServiceChanged { add { } remove { } }
        public IReadOnlyList<ServiceSnapshot> Snapshots => Running.Values.Select(definition => new ServiceSnapshot { Id = definition.Id, DisplayName = definition.DisplayName, Kind = definition.Kind, Status = ServiceStatus.Running, Version = definition.Version }).ToArray();
        public bool IsRunning(string id) => Running.ContainsKey(id);
        public Task StartAsync(ManagedServiceDefinition definition, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add($"start:{definition.Id}:{definition.Version}");
            StartHook?.Invoke(definition, cancellationToken);
            Running[definition.Id] = definition;
            return Task.CompletedTask;
        }
        public Task StopAsync(string id, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Events.Add("stop:" + id); Running.Remove(id); return Task.CompletedTask;
        }
        public Task StopAllAsync(TimeSpan timeout, CancellationToken cancellationToken = default) { Running.Clear(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task<ProcessResult> RunAsync(ProcessCommand command, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            if (Path.GetFileName(command.ExecutablePath).Equals("nginx.exe", StringComparison.OrdinalIgnoreCase))
                return new ProcessResult(0, "", command.Arguments.Contains("-t") ? "configuration test is successful" : "nginx version: nginx/1.30.0");
            var marker = Path.Combine(command.WorkingDirectory!, Path.GetFileName(command.ExecutablePath).Equals("php-cgi.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(command.WorkingDirectory!, "cgi-version.fixture")) ? "cgi-version.fixture" : "version.fixture");
            var version = (await File.ReadAllTextAsync(marker, cancellationToken)).Trim();
            if (command.Arguments.Contains("-v")) return new ProcessResult(0, $"PHP {version} (cli) (NTS Visual C++ 2022 x64)\n", "");
            var iniInspection = command.Arguments.Contains("--ini");
            Check(iniInspection || command.Arguments.Contains("-m"), "Unexpected PHP update fixture command: " + string.Join(' ', command.Arguments));
            var modules = new HashSet<string>(["Core", "json", "standard", "PDO"], StringComparer.OrdinalIgnoreCase);
            var errors = new StringBuilder();
            if (!command.Arguments.Contains("-n"))
            {
                var scan = command.EnvironmentVariables["PHP_INI_SCAN_DIR"];
                var primaryIndex = command.Arguments.ToList().IndexOf("-c");
                var files = Directory.GetFiles(scan, "*.ini").Prepend(command.Arguments[primaryIndex + 1]);
                foreach (var ini in files)
                foreach (var line in await File.ReadAllLinesAsync(ini, cancellationToken))
                {
                    var match = Regex.Match(line, "^\\s*(?:zend_extension|extension)\\s*=\\s*\"?([^\";]+)\"?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (!match.Success) continue;
                    var dll = match.Groups[1].Value.Trim();
                    if (!File.Exists(dll)) { errors.AppendLine("PHP Warning: PHP Startup: Unable to load dynamic library " + dll); continue; }
                    var module = Path.GetFileNameWithoutExtension(dll);
                    if (module.StartsWith("php_", StringComparison.OrdinalIgnoreCase)) module = module[4..];
                    if (module == "gd" && version == SuppressedModuleVersion) continue;
                    modules.Add(module == "opcache" ? "Zend OPcache" : module);
                }
            }
            if (iniInspection) return new ProcessResult(0, "Configuration File (php.ini) Path: fixture\n", errors.ToString());
            var result = new ProcessResult(0, "[PHP Modules]\n" + string.Join('\n', modules) + "\n\n[Zend Modules]\n" + (modules.Contains("Zend OPcache") ? "Zend OPcache\n" : ""), errors.ToString());
            if (!command.Arguments.Contains("-n")) ModuleValidationHook?.Invoke(command);
            return result;
        }
    }
}
