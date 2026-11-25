# Interface review — 25 November 2026

The [workflow review](WORKFLOW-UX-AUDIT-2026-11-25.md) follows this visual pass. It covers site and extension changes, waiting states, configuration application and recovery.

## Scope and coverage

Native WPF manager shell and seven sections, with implementation focused on entry/setup and reading flows. Existing conventions: AGENTS.md, ARCHITECTURE.md, UI-REVIEW.md and DESKTOP-WINDOW-0.7.md. Existing resource dictionary, semantic palettes, Segoe UI and native controls retained. All seven owning skills loaded.

| Domain | Evidence inspected | Result |
| --- | --- | --- |
| Accessibility | App.xaml templates, control names, Sites form, UiSmokeChecks, PageAutoRefresher | Native keyboard form submission, error focus, accessible names and pause preservation verified; full screen-reader output unverified |
| Layout | Shell/actions, mail panes, Sites form; baseline captures at 1000/560 DIPs | Adaptive actions implemented; populated desktop inbox verified with isolated data |
| Writing | Seven page builders, empty/error/install strings and logs | Duplicate installation CTA removed; form guidance and readable logs |
| Typography | Shared text helper, mail detail/truncation, Activity output | Font retained; structured Activity text implemented |
| Colors | ThemeManager, input boundaries, text contrast smoke assertions | Input boundaries corrected; captured text checks pass |
| UI | Shared templates, startup card, site dialog and seven compact views | Shared styles retained; form actions made consistent |
| Motion | ThemeManager, TrayHost, page refresh | Existing reduced-motion gate retained; no new animation; update pause added |

## Findings

Locations refer to resulting source. Findings are grouped by root cause.

| Severity | Domain | Location | Before | After | Why |
| --- | --- | --- | --- | --- | --- |
| HIGH | Colors | src/PhpDevTray.App/App.xaml:137, :173, :231; ThemeManager.cs:104 | Decorative input boundaries: 1.34:1 light, 1.44:1 dark against card | InputBorderBrush #76849A: 3.79:1 light, 4.15:1 dark; native high-contrast mapping | Meaningful control boundaries require 3:1, WCAG 1.4.11 |
| HIGH | Accessibility | src/PhpDevTray.App/ManagerWindow.cs:1052 | Automatically updating Activity/inbox with no pause | Pause updates / Resume updates cancel and restore refresh scope | User control of auto-updating information, WCAG 2.2.2 |
| MEDIUM | Layout | src/PhpDevTray.App/ManagerWindow.cs:164 | Heading/actions share one row; compact Caddy subtitle wraps into three short lines | Actions move below heading under 730 DIPs and wrap | Preserve reading and action space |
| MEDIUM | Writing | src/PhpDevTray.App/ManagerWindow.cs:358 | Duplicate primary Install CTA in empty PHP page | One installation CTA in empty state | One clear entry action |
| MEDIUM | Typography | src/PhpDevTray.App/ManagerWindow.cs:1074 | Raw JSON in Activity, contrary to previous review documentation | Local timestamp, severity, readable message; partial/native output preserved | No decoding required to read events |
| MEDIUM | Accessibility | src/PhpDevTray.App/ManagerWindow.Sites.cs:60 | No explicit Cancel, native label associations, initial focus or announced validation | Shared native buttons, targeted labels, hostname focus, polite errors and field focus | Clear keyboard path and recoverable errors |
| MEDIUM | Layout | src/PhpDevTray.App/ManagerWindow.cs:532 | Fixed 220-DIP inbox list regardless of available width | Desktop master/detail layout retained; list narrows to 160 DIPs below 600 DIPs of inbox width | Preserve visible message rows and readable detail at the native desktop minimum |
| LOW | Layout | src/PhpDevTray.App/ManagerWindow.cs:122 | 170 DIPs of persistent top chrome | 152 DIPs, retaining 40-DIP targets | More room for content in small windows |

## Mockup

[HTML/CSS study](mockups/interface.html): seven representative desktop views, synthetic data, navigation, distinct Sites/Caddy icons, themes, configured/empty/loading/error states, site creation, runtime selection, inbox search/selection, extension dialog, simulated installation and preferences. No real services or Windows settings are changed. Run `node docs/mockups/serve.cjs`, then open http://127.0.0.1:4179/.

Browser rendering and interactions verified through the loopback HTTP server. File protocol is not used. The final mockup follows the desktop window minimum (560 pixels) and icon rail, rather than a mobile navigation layout.

## Verification

- Baseline smoke captures: all seven sections at 1000 and 560 DIPs, height 600, Light. Exact flags: `--smoke-test <isolated-profile> --smoke-section <section> --smoke-width <width> --smoke-height 600 --theme Light --verify-ui --screenshot <path>`.
- Result captures: seven sections at 560 × 600, both Light and Dark, same flags. Contrast/name/navigation/action-reachability assertions passed. Files: ignored `artifacts/interface-audit/`.
- Visually inspected baseline compact captures of seven sections and resulting compact Caddy and Activity captures. Other resulting images assertion-checked, not individually visually approved.
- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -DotNetPath <workspace>/artifacts/ci-tools/dotnet/dotnet.exe`: build, process/configuration and WPF checks pass. NU1900 warnings reflect unavailable NuGet vulnerability metadata.
- New assertions cover readable application log messages and lossless partial/non-application records. Existing tests cover control semantics, contrast and high contrast.
- Input boundary contrast calculated from declared sRGB values using relative luminance. Disabled controls are exempt; hover pixels were not exhaustively measured.

**Acceptance completed:** native form initial focus, Enter validation, field correction, Tab navigation and simulated site submission; inbox search, empty results and Pause preserving the query. A live native inspection found a missing Browse button name; fixed in DialogUi.ActionButton with a regression assertion. 28 populated native captures (seven sections × two themes × 560/1000 DIPs) pass rendered contrast/name/navigation checks. Final compact inbox image was visually inspected: both message rows and detail remain available side by side. Desktop captures use `PhpDevTray.UiChecks --desktop-capture <absolute-output-directory>`; `--desktop-preview` opens the same isolated fixture for manual reproduction. No production data or services are touched.

**Remaining verification boundaries:** full screen-reader speech, mixed-monitor DPI transitions, 200% Windows text scaling and real upstream download failures were not certified. These are explicitly outside the completed local desktop interaction pass. Mobile/320-pixel layout is not a product requirement. No OS accessibility setting was modified.

## Verdict

**Approve — desktop UI/UX correction pass complete.** Browser mockup interactions, populated WPF captures and the core native keyboard flow have been verified. No confirmed HIGH finding remains within this scope. This is not a whole-app accessibility certification; the boundaries above remain explicit.


## Final follow-up findings

| Severity | Domain | Location | Before | After | Why |
| --- | --- | --- | --- | --- | --- |
| HIGH | Accessibility | src/PhpDevTray.App/ExtensionsDialog.cs, DialogUi.ActionButton | Icon/text button content could expose no name (observed on Browse folders) | Explicit AutomationProperties.Name for every shared dialog button; regression assertion | Every interactive control needs an accessible name |
| MEDIUM | UI | src/PhpDevTray.App/Controls/UiIcon.cs; ManagerWindow.cs | Sites and Caddy shared a globe geometry | Sites uses a browser-window outline, Caddy a server outline consistently in navigation and installer | Distinct destinations need distinguishable cues in the desktop icon rail |
| MEDIUM | Layout | src/PhpDevTray.App/ManagerWindow.cs, AddUpdateControl | Pausing rebuilt the inbox and cleared the query | Update only the button and refresh scope | Preserve reading/search state during pause |
