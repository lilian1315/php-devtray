# Architecture and implementation milestones

The stack decision is **C# / .NET 10 / WPF with Fluent-style presentation**, as requested from the original specification. Earlier alternatives research is historical context; no Rust, Qt, Tauri, or web frontend is used in this implementation.

## Projects

| Project | Responsibility |
| --- | --- |
| LocalServerManager.Core | UI-independent records, service definitions, snapshots, and application/supervisor contracts |
| LocalServerManager.Infrastructure | Native supervision, persistence, runtime import, configuration generation, Mailpit API, and application coordinator |
| LocalServerManager.App | WPF popup, native tray icon/menu, theme resources, activation, and shutdown |
| LocalServerManager.Cli | Independent executable PHP launcher with argument/stream/exit-code forwarding |
| LocalServerManager.Checks | Dependency-free configuration, orchestration, and real Windows process checks |
| LocalServerManager.UiChecks | Native WPF control-template, theme and rendering-resource regression checks |

The tray process owns all service Job handles. There is no permanently installed Windows Service. Each service receives a separate Job Object with kill-on-close semantics. Service processes start suspended and run only after containment succeeds. Validation/control commands are contained as well. Independently launched CLI commands use a separate launcher lifecycle.

The UI reads immutable snapshots through `IEnvironmentManager`. Application-level operations are serialized; native process creation and readiness checks execute off the UI thread. Status changes are marshalled to WPF's dispatcher. The five-second supervisor monitor is independent of popup visibility. Tray animation runs only while an operation is active and respects the motion preference.

Activity and Mail Trap use a separate `PageAutoRefresher`: a one-second dispatcher timer enabled only for a visible eligible section. Leaving/hiding the page cancels its scope; stopped Mail Trap does not poll its API. A pending read is never overlapped by another tick. Refresh reads do not enter the foreground operation/busy path. Equal data retains its existing controls; mail search/selection and scroll offsets are preserved, and selected log text is left untouched until selection is cleared.

## Decisions addressing the review

1. **Shutdown dependency order:** Nginx drains first while PHP and Mailpit remain available; PHP exits next; Mailpit exits last. A total budget and per-phase deadlines bound cleanup. Forced termination is reported rather than called graceful.
2. **Safe PHP routing:** generated PHP handlers check script existence, reject PATH_INFO-style requests, and pass an explicit script filename. PHP-CGI receives `cgi.fix_pathinfo=0`. Arbitrary custom PHP blocks are outside this milestone's supported source contract.
3. **PHP identity:** one active installed release is registered per minor channel in the initial implementation. This intentionally rejects same-channel patch coexistence until a real activation/rollback model is implemented. Configuration preferences and the default reference channels, and resolved INI snapshots refer to installation-specific paths.
4. **Configuration activation:** Nginx validation reads a complete candidate tree. The stable bootstrap is replaced only after validation and source-revision checks succeed. The coordinator preserves its prior bytes and settings while attempting a stop/start activation, and restores them on activation failure. Rejected candidate validation must not stop the working instance.
5. **Configuration ownership:** editable Nginx and PHP source files stay outside runtime directories. Generated Nginx and resolved PHP snapshots are separate. Initial site-source fields are validated against metadata; conflicting external changes raise reconciliation errors instead of silently rewriting them. Advanced arbitrary-source parsing is deferred.
6. **Default and PATH invariants:** installed PHP requires a valid selected channel. Disabling PATH does not remove that selection. The initial UI does not offer uninstall, so removal/replacement workflows cannot violate the invariant silently.
7. **Prerequisites:** imported/downloaded PHP is checked for executable architecture, NTS build, required companions, actual CLI execution, and configuration warnings. A missing dependency fails visibly without registration. A prerequisite bootstrapper/installer is deferred. HTML preview is deferred, so this build has no WebView2 prerequisite.

## Version 0.2 additions

`RuntimeReleaseManager` discovers releases from PHP's Windows manifest, Nginx's download page, and the official axllent/mailpit GitHub release API. Installation resolves the requested release ID again from fresh trusted metadata rather than accepting caller-supplied URLs or hashes. Redirect destinations are restricted by publisher. PHP/Mailpit use publisher SHA256 digests; Nginx requires a detached OpenPGP signature and embedded official key fingerprints, including signing-subkey binding validation. BouncyCastle.Cryptography 2.7.0 supplies the signature implementation.

Downloads and ZIP extraction are bounded and cancellable. Extraction rejects path traversal, alternate data streams, reserved Windows names, symlink entries, case-insensitive path collisions, excessive expansion, and unsupported ZIP64/multidisk structures. Download staging has explicit ownership and is removed after registration or failure. The download workflow installs only new runtime/channel registrations; it does not pretend to implement patch updates.

Bundled extension inventory invokes actual contained PHP module commands, separates built-in modules from available DLLs, normalizes reported names, and reports startup failures. A proposed toggle is validated in a fresh immutable configuration before publication. Built-in toggles and conflicting stock/user directives are refused with source locations. OPcache uses `zend_extension`. The coordinator drains dependent Nginx workers before restarting a running PHP channel and restores its previous definition/settings on failure. A stopped channel stays stopped.

Per-site PHP selection is exposed through the existing validated activation transaction. Actual upstream tests cover these paths, not just generated configuration text.

## Configuration sources

PHP source lives in `user/php/<channel>/conf.d/`. Each validated launch configuration is snapshotted under `data/state/php-config/`. The CLI launcher's atomically replaced `bin/launcher.json` selects one executable, primary INI, and resolved scan directory together. Changing a user INI file requires a subsequent start/restart to create and validate the new snapshot.

Nginx user root and sites live in `user/nginx/`. The implemented markers are `@lsm/sites@`, `@lsm/mime@`, and `@lsm/php/<site-id>@`, each inside a quoted include directive. These source files must be compiled by the manager before being used by Nginx. The generated bootstrap includes an immutable candidate root with concrete paths. General nested includes are not accepted by this milestone.

The original requirement for zero loss of user configuration is preserved: imports and generated updates do not silently overwrite `20-custom.ini` or user-owned site directives. Corrupt settings remain on disk with a repair error and backup location, rather than being reset to an empty configuration.

## Next implementation work

Add durable activation journals, Nginx/Mailpit replacement, confirmed Nginx reload, richer Mailpit controls, startup/port settings, and installer prerequisite handling. Expand the actual runtime-version matrix and complete interactive accessibility/DPI testing and resource benchmarks before a version 1.0 release.

## Version 0.5 include handling

Direct site-source includes accept absolute paths/masks, while included files are interpreted natively by Nginx. They are not recursively rewritten or semantically restricted by the manager. A temporary validation bootstrap and stable runtime bootstrap share the generated directory, so relative include resolution is consistent. The temporary wrapper is removed after testing; stable bootstrap publication still follows successful validation/source checks. External snippets are live user files rather than immutable snapshots. The managed root's required markers and direct site metadata/FastCGI checks remain. See [NGINX-INCLUDES.md](NGINX-INCLUDES.md).

## Version 0.4 PHP activation

The channel policy stays on the active `PhpInstallation`; `PhpRuntimeVersion` retains only a previous distribution's channel, patch and directory. Both official installation and explicit import can activate a different patch. A retained switch combines those saved binaries with today's channel policy.

`BuildPhpCandidateAsync` verifies CLI/CGI patch identity, NTS architecture and configured modules, returning a definition referencing the exact tested immutable INI snapshot. The coordinator drains dependent Nginx, transitions running PHP, verifies readiness, and resumes the previous Nginx definition. The endpoint is unchanged, so Nginx routing does not need to be regenerated. Stopped channels remain stopped.

`PublishPhpCandidateAsync` publishes manager-owned INI from that snapshot and points the default CLI at the same generation; it does not reread mutable user sources. A nondefault update leaves the CLI target untouched. Settings are saved after successful health checks/publication. Exception/cancellation recovery restores saved managed INI/CLI bytes and the prior service definitions, retaining candidate files if recovery is incomplete. This is not a durable crash journal across multiple files. See [PHP-UPDATES.md](PHP-UPDATES.md) and [REAL-PHP-UPDATES.md](REAL-PHP-UPDATES.md).
