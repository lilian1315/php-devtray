# Architecture and implementation milestones

The stack decision is **C# / .NET 10 / WPF with Fluent-style presentation**, as requested from the original specification. Earlier alternatives research is historical context; no Rust, Qt, Tauri, or web frontend is used in this implementation.

## Projects

| Project | Responsibility |
| --- | --- |
| PhpDevTray.Core | UI-independent records, service definitions, snapshots, and application/supervisor contracts |
| PhpDevTray.Infrastructure | Native supervision, persistence, runtime import, configuration generation, Mailpit API, and application coordinator |
| PhpDevTray.App | WPF popup, native tray icon/menu, theme resources, activation, and shutdown |
| PhpDevTray.Cli | Independent executable PHP launcher with argument/stream/exit-code forwarding |
| PhpDevTray.Checks | Dependency-free configuration, orchestration, and real Windows process checks |
| PhpDevTray.UiChecks | Native WPF control-template, theme and rendering-resource regression checks |

The tray process owns all service Job handles. There is no permanently installed Windows Service. Each service receives a separate Job Object with kill-on-close semantics. Service processes start suspended and run only after containment succeeds. Validation/control commands are contained as well. Independently launched CLI commands use a separate launcher lifecycle.

Startup with Windows is an opt-in Windows-owned preference, read and changed by the WPF app rather than duplicated in workspace settings. Packaged builds use `StartupTask` (`PhpDevTrayStartup`); unpackaged builds register the current executable and profile in the current user's Run key. Both pass `--startup`, which starts the configured service stack without creating a manager window. Duplicate automatic launches do not activate the existing window. Manual launches still activate it. Automatic-start failures keep the tray available, expose coordinator errors on opening the manager, and write `logs/application/startup.log` without a modal dialog. Windows Startup settings remain authoritative for tasks disabled by the user or policy. See [StartupTask](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptask) and [desktop extension parameters](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-desktop-extension).

The UI reads immutable snapshots through `IEnvironmentManager`. Application-level operations are serialized; native process creation and readiness checks execute off the UI thread. Status changes are marshalled to WPF's dispatcher. The five-second supervisor monitor is independent of popup visibility. Tray animation runs only while an operation is active and respects the motion preference.

Activity and Mail Trap use a separate `PageAutoRefresher`: a one-second dispatcher timer enabled only for a visible eligible section. Leaving/hiding the page cancels its scope; stopped Mail Trap does not poll its API. Pause updates cancels the visible page refresh scope; Resume updates restores it without stopping services. A pending read is never overlapped by another tick. Refresh reads do not enter the foreground operation/busy path. Equal data retains its existing controls; mail search/selection and scroll offsets are preserved, and selected log text is left untouched until selection is cleared.

## Web serving

Caddy is the only front-end server. The editable workspace file `settings.json` (`web`) maps hostnames and document roots to a registered FrankenPHP instance or PHP NTS channel. Each site owns one user-editable directive file in `user/caddy/sites/`. Creation writes the literal document root and explanatory comments into that file. The creation-time root remains metadata used to generate the initial file, and imports take no positional arguments. The generated site block imports user directives and appends the selected engine’s PHP handler. The same composition is used for native adaptation when inspecting roots. A separate read-only snapshot resolves the displayed root through native Caddy/FrankenPHP adaptation on initialization, refresh and site changes. One FileSystemWatcher monitors the site directory. A single worker groups save notifications over 500 ms, then adapts only affected sites under the coordinator lock. Renames and deletions invalidate the displayed root. Watcher overflow triggers a full site reread. Disposal cancels and awaits the worker before supervisor shutdown. Ambiguous, conditional, dynamic or invalid roots are omitted from that snapshot. Runtime selection and enable/disable remain available on the site row. The selected FrankenPHP instance imports it behind the HTTPS proxy. For NTS sites, Caddy imports it directly and routes PHP via FastCGI. Site metadata and runtime selection are saved separately so changes preserve edits to the Caddyfile. Start validates all generated Caddyfiles, starts the HTTP-only backends, then starts the HTTPS proxy. The proxy uses its local CA with default trust installation. Each backend disables automatic HTTPS and sets `skip_install_trust`. Stop All stops the proxy before its backends. FrankenPHP uses the same extension inventory, conflict checks and immutable INI snapshots as PHP NTS, with configuration isolated by instance ID. The selected snapshot is supplied to both its bundled CLI validation and FrankenPHP through PHPRC/PHP_INI_SCAN_DIR. A writable OPcache cache and Windows fallback remain part of the managed INI. Extension activation validates before stopping services and restores previous definitions after a failed restart.

Hostname changes keep the site’s original directive file in place, using a persisted file key. The editor, site import, and root watcher resolve that same file. When the site is active, the coordinator validates generated Caddyfiles in a separate directory before replacing the affected services. It persists the new hostname after startup succeeds and restores the previous service definitions if activation fails.

Enabling and disabling a site uses the same routing transition. With Caddy running, the coordinator validates the new routing before replacing the relevant services and saves the setting after they are ready. Disabling the last site leaves Caddy listening on loopback HTTP with an empty route table. With Caddy stopped, the setting takes effect when Caddy next starts. A running FrankenPHP instance also updates its own route table, including when running independently.

## PHP management

The extension dialog stages changes locally. Apply validates the complete desired module set against a single INI snapshot. An active PHP NTS or FrankenPHP service restarts once with that snapshot and recovers its previous definition on failure. FrankenPHP keeps its listener and routing while Caddy continues serving. Stopped runtimes use the saved changes at the next start.

6. **Default and PATH invariants:** installed PHP requires a valid selected channel. Disabling PATH does not remove that selection. The initial UI does not offer uninstall, so removal/replacement workflows cannot violate the invariant silently.
7. **Prerequisites:** imported/downloaded PHP is checked for executable architecture, NTS build, required companions, actual CLI execution, and configuration warnings. A missing dependency fails visibly without registration. A prerequisite bootstrapper/installer is deferred. HTML preview is deferred, so this build has no WebView2 prerequisite.

## Version 0.2 additions


Downloads and ZIP extraction are bounded and cancellable. Extraction rejects path traversal, alternate data streams, reserved Windows names, symlink entries, case-insensitive path collisions, excessive expansion, and unsupported ZIP64/multidisk structures. Download staging has explicit ownership and is removed after registration or failure. The download workflow installs only new runtime/channel registrations; it does not pretend to implement patch updates.

Bundled extension inventory invokes actual contained PHP module commands, separates built-in modules from available DLLs, normalizes reported names, and reports startup failures. A proposed toggle is validated in a fresh immutable configuration before publication. Built-in toggles and conflicting stock/user directives are refused with source locations. OPcache uses `zend_extension`. The coordinator restarts a running PHP channel and restores its previous definition/settings on failure. A stopped channel stays stopped.


## Configuration sources

Default listeners use Caddy HTTP `80` and HTTPS `443`, Mailpit HTTP `8025`, and the uncommon Mailpit SMTP port `39327`, outside Windows' default dynamic range (`49152–65535`). New PHP channels prefer `9000 + <minor channel digits>` and search only `9000–49151`, skipping all configured PHP, SMTP and Mailpit HTTP ports. Availability is tested with an exclusive loopback bind, so Windows-excluded ports and existing listeners are skipped. If the upper end is reached, allocation wraps to `9000`. Startup repeats the bind preflight; an access-denied bind reports Windows reservations/permissions explicitly. This reduces collisions but does not guarantee any fixed port is available on every machine.

This beta does not migrate existing port settings. For a profile created with the old SMTP default, set `smtpPort` to `39327` in `settings.json` or start with a new profile, and update SMTP clients accordingly. Existing PHP channel ports and user-owned configuration are retained. The supplied original specification documents the historical `49327` default.

Microsoft Store packaging uses a full-trust MSIX desktop app. `AppDeployment` resolves package identity through `GetCurrentPackageFamilyName`. Packaged builds use a physical workspace under the package's `LocalState/PhpDevTray` directory so the manager, its command alias and downloaded processes share the same paths. Windows retains this data across package updates and removes it on uninstall/reset. Unpackaged builds retain the ordinary `%LOCALAPPDATA%/PhpDevTray` profile. There is no automatic migration between packaged and unpackaged profiles.

The MSIX `php.exe` execution alias targets the bundled CLI, which reads the default package profile's `bin/launcher.json` without starting the tray app. Store settings open Windows' alias controls instead of editing user PATH. Unpackaged builds keep the existing opt-in PATH integration and deployed independent launcher. No virtualization opt-out capabilities are requested. See [Store releases](RELEASING.md) for packaging and acceptance checks.

PHP source lives in `user/php/<channel>/conf.d/`. Each validated launch configuration is snapshotted under `data/state/php-config/`. The CLI launcher's atomically replaced `bin/launcher.json` selects one executable, primary INI, and resolved scan directory together. Changing a user INI file requires a subsequent start/restart to create and validate the new snapshot.


The original requirement for zero loss of user configuration is preserved: imports and generated updates do not silently overwrite `20-custom.ini` or user-owned site directives. Corrupt settings remain on disk with a repair error and backup location, rather than being reset to an empty configuration.


## Version 0.4 PHP activation

The channel policy stays on the active `PhpInstallation`; `PhpRuntimeVersion` retains only a previous distribution's channel, patch and directory. Both official installation and explicit import can activate a different patch. A retained switch combines those saved binaries with today's channel policy.

`BuildPhpCandidateAsync` verifies CLI/CGI patch identity, NTS architecture and configured modules, returning a definition referencing the exact tested immutable INI snapshot. The coordinator transitions running PHP and verifies readiness. Stopped channels remain stopped.

`PublishPhpCandidateAsync` publishes manager-owned INI from that snapshot and points the default CLI at the same generation; it does not reread mutable user sources. A nondefault update leaves the CLI target untouched. Settings are saved after successful health checks/publication. Exception/cancellation recovery restores saved managed INI/CLI bytes and the prior service definitions, retaining candidate files if recovery is incomplete. This is not a durable crash journal across multiple files. See [PHP-UPDATES.md](PHP-UPDATES.md) and [REAL-PHP-UPDATES.md](REAL-PHP-UPDATES.md).
