# PHP DevTray

PHP DevTray (PDT) manages local PHP development environments on Windows.

A Windows desktop application that keeps running in the tray using the original **C# / .NET 10 / WPF** stack. The UI and runtime management layer are separate projects; native Windows Job Objects contain the services.

This is **0.7.0 preview**: a normal resizable desktop window that hides to the tray on minimize or Close, preserving services and restoring its previous window state. It includes the reviewed summary cards, site PHP dropdowns, raw Activity log and installer tabs. The complete version 1.0 specification is still in progress.

![PHP DevTray](docs/images/php-devtray-light.png)

## Install

Run [PhpDevTray-0.7.0-Setup-x64.exe](artifacts/installers/PhpDevTray-0.7.0-Setup-x64.exe). It installs for your Windows account, adds Start menu access and supports uninstall. Your runtimes, settings and captured mail are kept separately. See [installer details](docs/INSTALLER.md).

GitHub Actions builds the installer, signs tagged releases through SignPath, and submits stable releases to WinGet. See [release setup](docs/RELEASING.md) for the required repository configuration.

## Run

Open `artifacts/releases/0.7.0/PhpDevTray.exe`. The normal resizable window opens on launch. Minimize or Close hides it to the tray while services continue. Left-click its notification icon to restore it, including its maximized state. Right-click for **Open manager**, **Start all / Stop all**, and **Exit and stop services**.

The published folder is self-contained for Windows x64. Keep the full folder, including `cli/`, together. No .NET installation is required to run this published build. PHP retains its own Visual C++ prerequisite.

To launch the application from a terminal, run:

```powershell
.\artifacts\releases\0.7.0\PhpDevTray.exe
```

Normal application data is stored in `%LOCALAPPDATA%\PhpDevTray`. No runtimes are bundled or started on first launch. PATH integration is off until explicitly enabled.

For an isolated development profile:

```powershell
.\artifacts\releases\0.7.0\PhpDevTray.exe --data-directory C:\Dev\PhpDevTrayData
```

Exit the older tray instance before opening the new executable. Only one ordinary instance runs in a Windows session. Exit the existing instance before changing its data-directory argument.

## First environment

1. Open **Install runtimes**, choose PHP, Nginx, or Mailpit, and select **Check releases**. This reads official metadata without downloading an archive.
2. Choose a release and explicitly select **Download and install**. PHP x64 NTS and Mailpit archives are checked against publisher SHA256 metadata. Nginx archives require a detached signature verified against pinned official keys. Download/extraction is bounded and cancellable. Install the matching Microsoft Visual C++ runtime if PHP requires it.
   Alternatively, use **Add runtime / Import extracted runtime** to select `php.exe`, `nginx.exe`, or Mailpit's executable from a distribution you have verified yourself. The manager validates and copies it into managed storage. Retain PHP's full distribution and Nginx's `conf/` directory.
3. Create a site with an existing document root, installed PHP channel, and hostname such as `app.test`. The default HTTP port is **8080**.
4. Configure that hostname in Windows hosts/DNS yourself. The manager does not modify the hosts file. Without DNS, use an explicit Host header for testing: `curl.exe -H "Host: app.test" http://127.0.0.1:8080/`.
5. Start the environment. Mailpit uses loopback SMTP **49327** and HTTP **8025**. PHP FastCGI ports are allocated separately for each minor channel.
6. Select a default PHP channel. Optionally enable user PATH integration in Settings and open a new terminal. `php -v` then uses the manager's executable launcher, including when the tray application is closed.
7. Use **Extensions** on a PHP runtime to search bundled modules and enable or disable them. Built-in modules are read-only. Conflicting user/stock INI directives are reported with source locations. A running channel and its dependent Nginx workers are drained/restarted for the change; stopped channels remain stopped.

Each site's PHP selector changes that site's channel independently of the CLI default.

The service summary bar has icon-only start/stop controls for Nginx, all PHP channels, and Mail Trap. Stopping PHP through its summary control drains dependent Nginx first. Page folder icons open configuration locations (Mail Trap settings live in the data directory's `settings.json`). Each site has a pencil action for its configuration file, including disabled sites. Stop/start Nginx to validate and activate saved edits; editor saves do not automatically reload it.

To update a PHP channel, open its runtime actions and choose **Check for updates**. Review the active/selected patch and choose **Update PHP**. The previous patch is kept under **Previous patches** for an explicit switch back. Each channel keeps its port, extension choices, sites, and CLI default. Running PHP and its dependent Nginx briefly restart; stopped channels stay stopped. See [PHP update behavior and recovery](docs/PHP-UPDATES.md).

The application reserves `localhost:8080` for its Nginx health/default page. Use another hostname for a site sharing that port.

Stopping a PHP runtime manually leaves dependent sites unavailable until you start it again. Exit Application drains Nginx before stopping PHP, then Mailpit. PHP CLI processes launched independently through PATH are not service processes and remain independent of the tray owner's lifetime.

## Included

- Single-instance native notification icon and three-command native context menu.
- Redesigned WPF popup with runtime cards, site controls, a searchable mail inbox, clearer installation states, and focused extension/site dialogs. Navigation adapts to narrow work areas.
- Persisted System, Light, and Dark appearance choices, native high-contrast colors, accessible control names, and explicit progress/error states.
- Import and validation of extracted PHP, Nginx, and Mailpit distributions.
- Official release discovery, publisher-verified downloads, safe extraction, progress, and cancellation for new installations and PHP patch updates.
- PHP patch activation with validation before transition, readiness checks, retained patches, and recovery of the previous running state on failure or cancellation.
- Simultaneous PHP minor channels, per-channel configuration, immutable resolved INI snapshots, CLI default, and explicit user PATH toggle.
- PHP, Nginx, and Mailpit start/stop/restart with endpoint ownership verification and periodic health checks.
- Actual PHP extension inventory and bundled DLL toggles, including Zend OPcache handling and conflict detection.
- Suspended process creation, Job Object assignment before resume, kill-on-close containment, and isolated service groups.
- Site creation and enable/disable with generated FastCGI routing and complete candidate configuration validation before publication.
- Persistent Mailpit storage, actual inbox listing, and plain-text message reading.
- Activity and Mail Trap refresh automatically every second while their page is visible. Mail polling requires a running Mail Trap; page refresh stops when hidden or another section is selected.
- Atomic settings with backups, preserved user configuration, and application/process logs.
- Source, repeatable build/publish scripts, console verification harness, and isolated UI screenshot mode.

## Current boundaries

- **PHP patch updates are supported.** Nginx/Mailpit updates, runtime uninstall, and application self-update are not implemented. The download UI still blocks installed Nginx/Mailpit replacements.
- One active PHP patch per minor channel, with previous patches retained for explicit switching. Same-patch Windows rebuild replacements are rejected; rebuild identity tracking and retained-binary cleanup are pending.
- PHP activation recovers from handled errors/cancellation, including failed readiness or settings publication. Durable crash-recovery journaling across sudden process/OS termination is not implemented.
- Third-party extension installation and advanced per-site extension profiles are not implemented. Bundled extensions are supported.
- Site-file include directives accept absolute paths and masks; nested files use normal Nginx include syntax. See [include behavior and examples](docs/NGINX-INCLUDES.md). The managed root remains marker-based. Direct site hostname/root/listen fields must agree with metadata; direct competing FastCGI blocks are rejected. Nested files are validated by Nginx and remain user-owned/live rather than snapshotted.
- Site changes currently use a validated stop/start of Nginx. Graceful reload, generation-specific HTTP confirmation, and durable crash-recovery journals remain future work.
- Mail supports actual plain-text message inspection and local subject/sender filtering of the loaded inbox. HTML preview, attachment handling, server-side search, deletion, and retention UI are pending. No browser renderer is loaded.
- No installer, startup-with-Windows UI, data-directory migration, settings editor for ports, or automatic restart policy is included yet.
- FastCGI monitoring confirms the owned protocol responder. Dedicated opt-in integration now also verifies actual PHP script execution, per-site routing, extension toggles, SMTP capture, persistent messages, request draining, and independent CLI lifetime. The tested versions are recorded in [REAL-INTEGRATION.md](docs/REAL-INTEGRATION.md); this does not establish compatibility with every upstream release.
- The ordinary offline checks use real Windows process/protocol fixtures and simulated binaries for configuration cases. The separate upstream test downloads verified actual distributions into an isolated profile and never changes user PATH or hosts.
- Native-menu interaction, full keyboard/screen-reader flows, Explorer restart, and mixed-DPI/multi-monitor behavior require interactive acceptance testing. Memory and the 200 ms popup target have not been benchmarked.
- Logging currently uses a bounded native process-output stream and structured JSON application files through BCL code. Microsoft.Extensions logging/DI integration, retention, and installer hardening are later foundation refinements.

## Build and verify

Requirements: Windows x64, .NET 10 SDK, and PowerShell. Restore uses **BouncyCastle.Cryptography 2.7.0** for OpenPGP verification; self-contained publishing also restores Microsoft's runtime packs. See [third-party notices](THIRD-PARTY-NOTICES.md).

```powershell
.\scripts\build.ps1
.\scripts\build.ps1 -Publish
```

An explicit SDK executable is also supported:

```powershell
.\scripts\build.ps1 -DotNetPath C:\Tools\dotnet\dotnet.exe -Publish
```

The first command builds Release and runs the process/configuration checks plus WPF control-resource regression checks. Publishing creates the complete Windows app under `artifacts/releases/0.7.0/` and deploys the independent PHP launcher under `artifacts/releases/0.7.0/cli/`.

For a rendered popup smoke test without touching the ordinary application profile:

```powershell
.\artifacts\releases\0.7.0\PhpDevTray.exe --smoke-test C:\Dev\PdtSmoke --screenshot C:\Dev\PdtSmoke\popup.png
```

This creates an isolated empty profile, renders the actual WPF popup, writes a PNG, cleans up the tray resources, and exits. Add `--smoke-section Settings` to inspect another section, `--theme Dark` for the dark palette, or `--verify-ui` for contrast and accessible-name assertions. `--smoke-width 860` forces a test viewport even on a smaller display; omit it to exercise work-area sizing.

See [the UI review](docs/UI-REVIEW.md), [architecture and implementation decisions](docs/ARCHITECTURE.md), and [verification record](docs/VERIFICATION.md). The supplied original specification is preserved as [SPEC-v1.1.md](docs/SPEC-v1.1.md).

The separate PHP patch acceptance test downloads an archived official Windows PHP build, verifies its publisher SBOM checksum, and exercises live update/rollback against the current release:

```powershell
dotnet tests/PhpDevTray.Checks/bin/Release/net10.0-windows/PhpDevTray.Checks.dll --real-php-updates --profile C:\Dev\PdtPatchChecks --cli-bundle C:\Dev\PhpDevTray\artifacts\releases\0.7.0\cli --report C:\Dev\PdtPatchReport.md
```

Use a new empty profile and free loopback port 8080. This test starts actual PHP and Nginx processes inside Jobs, leaves unrelated listeners alone, and does not enable PATH integration or modify hosts. Results are recorded in [REAL-PHP-UPDATES.md](docs/REAL-PHP-UPDATES.md).

For the opt-in actual-runtime checks, build/publish first, then invoke the checks executable with absolute paths:

```powershell
dotnet tests/PhpDevTray.Checks/bin/Release/net10.0-windows/PhpDevTray.Checks.dll --real-runtimes --profile C:\Dev\PdtIntegration --cli-bundle C:\Dev\PhpDevTray\artifacts\releases\0.7.0\cli --report C:\Dev\PdtIntegrationReport.md
```

Use a dedicated empty profile. The harness requires free loopback port 8080, installs two PHP channels plus Nginx/Mailpit, selects temporary SMTP/HTTP ports, runs the assertions, and stops all owned services. The profile remains available for diagnosis. It never kills unrelated listeners or changes PATH/hosts.
