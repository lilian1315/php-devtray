# Verification record

Date: 28 October 2026. Host: Windows 10 x64. Toolchain: workspace-local .NET SDK 10.0.403, .NET runtime 10.0.14. No preinstalled .NET SDK was available; the official SDK ZIP was checked against the SHA512 in Microsoft's release metadata before use.

## Completed checks

- Full solution Release build: **0 warnings, 0 errors**.
- Site input checks reject traversal, directive injection, invalid ports, and relative/missing roots.
- Settings preserve navigation/site data, retain atomic backups, and report corrupt files without erasing them.
- Coordinator initialization does not start unrequested services or enable PATH, and invalid default selections expose actionable errors.
- PHP configuration checks preserve source overrides, keep old immutable snapshots unchanged, publish a coherent CLI target, and include the managed CGI path guard.
- Nginx configuration checks validate the full candidate before publication, preserve existing source bytes, refuse arbitrary includes and external listeners, reject source/metadata conflicts, and keep the previous bootstrap when candidate validation fails.
- Real native process checks cover suspended creation, Unicode/empty/quoted arguments, environment variables, stdout/stderr, and timeouts.
- Job Object checks cover descendant cleanup, sibling-service isolation, and forced supervisor-owner termination.
- Protocol fixtures exercise owned FastCGI, SMTP, and HTTP readiness. A competing listener acquired after preflight is rejected as foreign.
- WPF smoke mode creates an isolated empty profile, renders the actual popup, captures a PNG, and exits successfully. The empty PHP state was visually inspected for clipping and overlapping controls.
- All 19 native UI API exports used by the tray implementation were checked on this host.

During integration, the smoke test found a misspelled native export and a failure-path logging exception; both were fixed. A fixture-file readiness race was fixed by publishing fixture files atomically after closing their write streams. The full process suite subsequently passed repeatedly and the final integrated Release suite passed.

## Limits of this evidence

Runtime configuration tests use simulated binaries and a controlled process runner. Protocol tests use real local fixture processes. The additional opt-in suite now also uses actual upstream PHP/Nginx/Mailpit distributions, as recorded below. PATH integration has not been enabled on the user's account for verification. HTML rendering is not included.

The rendered screenshot does not establish keyboard-only, screen-reader, native-menu, multi-monitor, mixed-DPI, or Explorer-restart acceptance. Resource use and cold-open latency still need benchmarks. Actual request draining and Mailpit persistence have passed the new end-to-end tests, but broader load and failure combinations remain acceptance work.

Self-contained Windows x64 publication completed successfully for both the WPF application and the independent PHP launcher. The published `PhpDevTray.exe` ran its isolated smoke mode without a system .NET installation, rendered the Settings section, cleaned up, and returned exit code 0. Both the empty PHP state and Settings captures were inspected. The complete published folder must remain together; this is a folder distribution rather than an installer.

## Version 0.2 verification

- Official live discovery and verification succeeded for PHP 8.4.27, PHP 8.5.12, Nginx 1.31.8, and Mailpit 1.32.0. Nginx's actual detached signature verified against pinned official keys.
- Offline artifact checks accept valid checksums/signatures and reject tampered data, unknown signers, caller-supplied release mutations, off-publisher redirects, unsigned releases, unsafe ZIP paths/entries, expansion bombs, and canceled staging.
- Extension checks distinguish built-ins and bundled DLLs, enforce Zend OPcache semantics, preserve immutable snapshots and user files, and reject missing/broken/silently failed DLLs, duplicate directives, warnings, and unresolved directive paths.
- The real-runtime suite passed HTTP requests through two PHP channels, per-site switching independent of the CLI default, actual OpenSSL and OPcache changes, invalid-candidate preservation, script-path guards, PHP mail(), direct SMTP capture, Mailpit persistence, active-request draining, and CLI lifetime/exit-code forwarding after manager disposal.
- Real integration initially exposed an invalid Mailpit `--version` assumption. It was corrected to the `version` subcommand and covered by a regression check before the successful rerun.
- The final Release solution build and full offline suite pass with zero compiler warnings/errors. Preview 0.2 is self-contained under `artifacts/releases/0.2.0/`; the earlier running executable was left open. Native executable smoke modes for the Updates page and a real PHP 8.4 extension inventory both returned exit code0, and their captures were inspected.

See [the complete actual-runtime report](REAL-INTEGRATION.md). The dedicated profile stayed under workspace scratch storage and no user PATH/hosts entries were changed. Broader upstream-version support remains unproven.

## Version 0.3 verification

The redesigned WPF interface builds and publishes self-contained for Windows x64 with **0 warnings and 0 errors**. The full offline process/configuration suite and new native WPF resource regression suite pass. Theme preference roundtrips through settings and snapshots without starting services or changing CLI/PATH state.

Real WPF rendering covers all six sections, both dialogs, empty/error states, light/dark palettes, and constrained work-area navigation. The published executable independently passed light/dark PHP, compact popup, and Activity smoke captures. Actual Mailpit messages and official runtime metadata were used for those screens. A real-data review caught and fixed a custom ComboBox DisplayMemberPath regression, covered in the committed UI checks.

Live desktop automation approval timed out, so keyboard/menu/screen-reader acceptance remains pending. Contrast/name/template checks do not establish full accessibility. No idle-memory or popup-latency benchmark was performed. See [UI-REVIEW.md](UI-REVIEW.md) for the exact coverage, changes, screenshots, and remaining limits. Preview 0.3 is under `artifacts/releases/0.3.0/`; exit the older tray instance before launching it.

## Version 0.4 verification

The final Release build passes with **0 warnings and 0 errors** after refreshing official NuGet vulnerability metadata. The full offline suite, native process containment/protocol checks, and WPF resource suite pass. Early restricted-network builds reported cached NU1900 audit-retrieval warnings; a fresh network-enabled restore resolved those without disabling auditing.

Nine PHP activation check groups cover stopped and running activation/retained switching, channel-policy preservation, sibling isolation, missing/silently unloaded modules, CLI/CGI identity mismatch, startup failure, cancellation, a real atomic-settings replacement failure, concurrent user INI edits during startup and module validation, and a nondefault update alongside unrelated pending default-channel edits. Retained path/metadata and tampered executable rejection are also covered. Configuration/coordination tests control only the native process boundary.

Actual publisher-verified PHP **8.4.26 → 8.4.27 → 8.4.26** passed through real Nginx requests and the independent CLI launcher. The enabled OpenSSL module, user memory-limit override, channel endpoint/default/site assignments, and user Nginx/INI bytes were preserved. A bad candidate was rejected while the active route remained usable. Rollback drained an active PHP request; the unrelated PHP 8.5 process kept its process ID. Switching a stopped channel changed the CLI without starting the service. No PATH/hosts changes or fixture processes remained. Exact hashes and results are in [REAL-PHP-UPDATES.md](REAL-PHP-UPDATES.md).

The self-contained preview's runtime inventory and channel-specific update offer were rendered from that actual profile in both palettes. Captures use the latest retained patch after rollback, so the offer correctly switches locally without downloading it again. Contrast, accessible names, navigation selection and label fit are checked in the adjacent PNG assertion records. Live keyboard/menu/screen-reader acceptance and performance benchmarks remain pending. Durable crash journaling, same-patch rebuild replacement and Nginx/Mailpit updates remain outside this milestone.

Final visual review additionally corrected an inherited black verification label in dark mode and light secondary text that fell below 4.5:1 on the sidebar. Render assertions now measure enabled visible TextBlocks against ancestor painted solid surfaces, alongside palette checks. The resource suite checks muted text on sidebar/hover/pressed colors. Both dialog palettes and Settings, mail, sites, empty/error captures pass these stronger checks. This remains a targeted solid-surface check rather than full accessibility certification.

## Version 0.4.1 notification layout

The reported notification screenshot exposed an empty state whose minimum content height exceeded the space left by the banner. Empty states now compact their illustration/spacing and place actions side by side when needed, with a scroll fallback. Notification text is bounded to 96 DIPs with its own scroll area; the dismiss control stays separate. Page content is clipped to its allocated region rather than drawing over the footer.

Stopped Mail Trap now displays a Start action and disables inbox refresh. Navigation/opening the stopped service no longer requests the unavailable inbox. Start/restart actions load messages after the service starts. Existing message storage is unchanged.

Release build/publish: **0 warnings, 0 errors**. WPF resource checks pass. Source renders cover installed/stopped Mail Trap, real running Mailpit inbox, PHP/install empty states, and errors. The published 0.4.1 executable passed short notification, long notification and no-notification captures in both palettes at 576 × 600 DIPs. Smoke assertions scroll empty states to confirm the final action is reachable, then restore the original offset; the reproduced short/long Mail Trap cases now fit with zero content scroll. Contrast/names/navigation assertions pass and no fixture services remain running. Native process and upstream update suites were not repeated for this UI-only change; the prior results remain recorded above.

## Version 0.4.2 input spacing and extension density

The shared TextBox template applied Padding as a content-host margin while WPF's native text view also applied it internally. A regression check using `GetRectFromCharacterIndex(0)` failed before the fix: a 34-DIP left padding put text at 70 DIPs. Removing the duplicate template margin makes the check pass (34 DIPs plus native border/caret allowance). Native text selection and the rest of the WPF resource suite still pass.

The extension inventory uses 40-DIP rows instead of 44, a 28-DIP column header, smaller group headings, and reduced dialog header/footer/details spacing. Redundant subtitle text was removed and the Enabled column tightened; checkbox targets remain 40 DIPs. More modules are visible without changing the window dimensions. Actual extension and site-form captures passed in both palettes; the real Mailpit reader capture also passed after the shared input-template fix. The self-contained 0.4.2 executable additionally passed both extension dialog palettes. Release build: **0 warnings, 0 errors**. Runtime/process suites were not repeated for this presentation-only change.

## Version 0.4.3 visible-page live refresh

29 October 2026. Activity and Mail Trap no longer have manual data-refresh buttons, including the redundant footer refresh on those sections. A one-second timer runs only for the visible eligible page, cancels pending reads on page exit/hide, and stops on shutdown. Mail Trap must be running before its API is polled. Foreground operations temporarily take precedence; refresh does not flash busy controls. Unchanged mail/log content is not replaced, mail search/current selection is retained, and log text selection postpones text replacement so copying is not disrupted.

The committed WPF checks pump a real dispatcher timer without opening a window. They verify immediate entry refresh, the one-second interval, no overlapping slow request, cancellation on exit, no polling while hidden, and stopped polling after disposal. The complete WPF resource/padding/template suite also passes. Release build and self-contained publication: **0 warnings, 0 errors**.

Black-box checks launched the published 0.4.3 app against the isolated real-runtime profile, waited for its initial page to load, then appended a new JSON log event or sent an actual SMTP message to the owned fixture Mailpit. After3.5 seconds, the app asserted that the injected content appeared in its rendered page without any refresh input. Both checks passed. The inbox retained the original selected message while showing the newly arrived subject. Captures and contrast/name/navigation assertions are in `images/auto-refresh-Logs-Light.png` and `images/auto-refresh-Mail-Light.png`. No test services remained running and no PATH/hosts entries were changed.

The smoke-only readiness/wait/expected-text flags support repeatable external injection checks; ordinary startup does not use them. Native process/update suites were not repeated for this UI data-refresh change. Full interactive keyboard/screen-reader acceptance and resource benchmarks remain pending.

## Version 0.5 summary controls and Nginx includes

29 October 2026. The full offline configuration/download/extension/PHP-update suite, native process/protocol/Job checks, and WPF resource/auto-refresh suite passed. Release build and publication completed with **0 warnings, 0 errors**.

Controlled catalog checks accept absolute site includes and preserve their source text, reject relative direct includes without replacing the previous bootstrap, and verify temporary validation-wrapper cleanup and matching validation/runtime configuration prefixes. Missing site files are created for disabled sites even without an installed Nginx; edited existing sources remain byte-identical.

An actual-runtime check reused previously publisher-verified distributions in a new isolated profile. Nginx started and served HTTP routes from an absolute outer snippet, nested relative and wildcard includes, a nested absolute snippet and a direct absolute wildcard. A broken nested directive failed actual nginx -t while the previous instance/PID, bootstrap and HTTP route remained intact. A relative direct site include was rejected without disturbing the running instance. Sources were preserved, temporary wrappers removed and all owned services stopped. See [REAL-NGINX-INCLUDES.md](REAL-NGINX-INCLUDES.md) and [include semantics/limits](NGINX-INCLUDES.md).

The published app rendered running Nginx/PHP and Mail Trap summary controls in both palettes at860 DIPs; source renders also passed at576 DIPs. Icon controls retain40-DIP targets and accessible Start/Stop names. Nginx/Mail page title toggles and page-level overflow menus are gone; configuration folder shortcuts and per-site edit icons are present. Runtime/site row menus remain for their additional actions. Mail Trap's folder opens the data directory containing settings.json. The PHP summary action drains dependent running Nginx before stopping its channels. Actual edit-window keyboard interactions and full accessibility acceptance remain pending; the editor was not launched during automated captures.
