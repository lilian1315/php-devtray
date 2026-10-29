# Program UI/UX review — 2026-10-29

## Scope and coverage

Reviewed the complete presentation layer: PHP runtimes and action menus, Nginx/site actions, Mail Trap, Activity, installer/update flow, Settings, create-site and extension dialogs, shared controls/themes and native tray menu. The service-summary redesign is separately held as HTML direction A, pending user validation. Application changes here do not implement that summary design.

Stack: .NET 10 / WPF, native Windows tray and dialogs, programmatic page construction, shared App.xaml templates and ThemeManager semantic brushes. Conventions inspected: ARCHITECTURE.md, UI-REVIEW.md, SPEC-v1.1.md. No applicable AGENTS.md or separate coding standard was found in the app scope. The six owning better-interface skills were read and applied in accessibility, layout, writing, typography, colors and UI order.

| Domain | Evidence inspected | Result |
| --- | --- | --- |
| Accessibility | Shared focus/control templates, field associations, enabled-control accessible-name smoke assertions, native theme resource checks, selected state and extension detail keyboard reachability | Fixed selected-theme indication and detail tab access; interactive screen-reader acceptance remains unverified |
| Layout | Rendered PHP, Nginx, Mail, Activity, Settings, installer, site/extension dialogs; 576/860-DIP popup captures, empty/error scroll reachability | Fixed path truncation and blank mail search; 320-DIP desktop operation and OS text scaling remain unverified |
| Writing | All page headings, empty/error/loading copy, menus, runtime update and tray exit descriptions | Fixed installer failure wording, mail retry guidance, copy confirmation, tray status and exit consequence |
| Typography | Shared font and wrapping, real long Windows paths and email subjects, extension details | Wrapped paths expose full values; retained existing compact type system |
| Colors | Light/dark rendered captures, per-text contrast assertions, selected-theme state, high-contrast resource tests | Passed checked 4.5:1 normal-text pairs; selected theme now also has a checkmark |
| UI | Common action styles, icon targets, disclosure hierarchy, search recovery, clipboard feedback, native controls and motion preference code | Refined shared feedback/actions; summary layout remains a prototype |

## Findings

All rows below are implemented in 0.5.1; severity records the original impact. No outstanding HIGH finding was confirmed within the inspected scope.

| Severity | Domain | Location | Before | After | Why |
| --- | --- | --- | --- | --- | --- |
| HIGH | Accessibility | src/PhpDevTray.App/ManagerWindow.cs:723 | Selected theme indicated by background/border colors | Checkmark and selected accessible name | Theme selection must remain understandable without color discrimination |
| MEDIUM | Layout | src/PhpDevTray.App/ManagerWindow.cs:491 | A search with no results left a blank message list and unrelated preview | Result count, explicit no-match state, Clear search, hidden preview | Explains the outcome and supplies a recovery path |
| MEDIUM | UI | src/PhpDevTray.App/ManagerWindow.cs:153 | Error details could only be dismissed | Copy details and View activity | Makes errors shareable and investigation reachable |
| MEDIUM | Accessibility | src/PhpDevTray.App/ExtensionsDialog.cs:168 | Long detail text was excluded from Tab navigation | Read-only details remain keyboard focusable and selectable; include full extension name | Keyboard users can inspect and copy long diagnostic values |
| MEDIUM | Typography | src/PhpDevTray.App/ManagerWindow.cs:410, :714 | Site/workspace paths ellipsized with pointer tooltips | Full paths wrap in the existing scrolling surface | Values remain available without hovering |
| MEDIUM | Writing | src/PhpDevTray.App/ManagerWindow.cs:639 | Network failure reused the normal discovery/empty heading | Could not load releases, explicit Retry release check | Distinguishes failed discovery from an empty catalogue |
| MEDIUM | UI | src/PhpDevTray.App/ManagerWindow.cs:577 | SMTP/hosts copy had no persistent completion feedback; SMTP copy was hidden when messages existed | Shared footer confirmation, visible SMTP copy, Activity copy | Users can tell whether copy succeeded and reach frequent actions |
| MEDIUM | Writing | src/PhpDevTray.App/TrayHost.cs:70, :128 | PHP tooltip only reported an active count; Exit Application omitted service consequence | Needs-attention/transition/count status and Exit and stop services | Makes partial failure and exit behavior explicit |
| LOW | Writing | src/PhpDevTray.App/ManagerWindow.cs:323, :394, :695 | Generic promotional page descriptions | Concrete page-purpose descriptions | Helps users predict available tasks |

## Verification

Passed:
- `work/Build-PhpDevTray.ps1`: Release build, full configuration/process checks and UI resource checks. Subsequent publish rebuild covers final copy changes. Build reported NU1900 warnings because NuGet vulnerability metadata was inaccessible; no compiler errors.
- `work/Render-UiReview.ps1 -Scene Sites,Mail,Settings,Extensions,SiteForm,Empty,Error`: actual WPF captures and UI assertions. Both themes for settings and both dialogs. Repeated changed error/settings/mail/extensions at 576 DIPs.
- `work/Render-UiReview.ps1 -Scene Downloads,Mail,Extensions,Error -Width 576`: includes real network-denied release lookup and recoverable installer state. No runtime downloads performed.
- App smoke invocations at 576 DIPs with `--smoke-section Logs` and `--smoke-section 'PHP Versions'` in dark theme: captures and assertions passed.
- Running fixture Mailpit with `--smoke-mail-search zz-no-match-ui-review-9f340 --smoke-expect-text 'No matching messages' --verify-ui`: passed in the actual WPF tree. Captures in docs/images/review-mail-no-results.png. Services used only the isolated workspace fixture profile and were stopped by smoke shutdown.
- Checked captures have adjacent .qa.txt files with actual dimensions, checked text contrast, accessible names, selected navigation and empty-state action reachability. Screenshots inspected include paths, error recovery, mail search, both dialogs, Settings and installer failure.
- `node --check outputs/summary-bar-lab/app.js`: refined A syntax passes. A now has text names, chevron page links, current-page treatment and separate 40px service controls; logos removed.

Not verified:
- Live keyboard traversal, clipboard interaction and native tray menu acceptance; screen-reader announcements, mixed-DPI behavior, OS text scaling and actual 200% zoom. Native template assertions are not replacements for these interactions.
- Re-rendering the latest HTML refinement: browser automation returned `browser.user is unavailable` after reconnect attempts. Prior A/B/C layout checks are historical and are not claimed as validation of the new refinement.
- Successful live release discovery in this run: network access was denied, which exercised the error flow instead. Existing update/process checks still passed.

## Verdict

Approve for the inspected source and rendered WPF scope. This is a bounded engineering review, not complete accessibility certification or user acceptance. The summary remains pending user validation; unresolved runtime acceptance checks are listed above.
`artifacts/releases/0.5.1/PhpDevTray.exe` also passed the isolated dark 576-DIP no-match inbox smoke with expected-text, names and contrast assertions. Start-PhpDevTray.cmd and README target 0.5.1.
