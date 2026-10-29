# WPF design implementation — preview 0.6.0

Implemented the reviewed HTML direction in the native WPF app. The application continues to use the existing supervisor, configuration validation, release verification and persistence services.

- Summary cards are whole-card navigation targets with independent 40-DIP start/stop buttons. No logos, chevrons, PHP version disclosure or hover fill. Selected service uses the shared accent surface; sidebar selection uses the strong accent fill.
- Nginx cards have an inline PHP version picker, explicit Enabled/Disabled badge, full document root and a three-dot menu. Edit config is inside that menu alongside enable/disable, browser, root-folder and hosts-entry actions. No colored card-edge marker.
- PHP keeps its existing native three-dot runtime menus. Its configuration folder action and Nginx's read Open config folder. Activity reads Open logs folder. Mail Trap and Install runtimes have no folder action.
- Activity displays raw log strings in the existing selectable read-only text control with a monospace font, without search or copy actions. Visible-page one-second refresh remains in place for Activity and running Mail Trap.
- Installer runtime choices use a connected tab strip and a shared content surface. Selecting a runtime reads official metadata; version selection, verification, retained-patch switching and existing installation safeguards remain real backend operations. No runtime was installed during this UI verification.

Verification:
- Release builds passed with no compiler errors. NU1900 warnings reflect inaccessible NuGet vulnerability metadata.
- Full configuration/process checks passed, including site configuration validation, patch transitions, rollback and process containment. UI resource checks passed after the styling changes.
- Actual WPF smoke renders passed for Nginx, PHP, Mail Trap, Activity, Settings, installer, empty and error pages at 576 DIPs, with additional 860-DIP installer capture and light/dark captures. Adjacent .qa.txt reports check accessible names, selected navigation, text contrast and scroll reachability.
- Added an opt-in smoke interaction for the actual site ComboBox. `--smoke-site-php 'beta.integration.test|8.4'` changed the isolated fixture's channel through its selection event and committed it. A second invocation selected 8.5 and restored the original fixture configuration. Both passed UI assertions, including a dark capture.
- Official release lookup succeeded with network permission and rendered a real release list, active/selected patch information and the Already installed state. The network-error/retry state also passed.
- HTML browser verification checked the requested folder labels/visibility on all five relevant pages. Matching prototype source remains in outputs/summary-bar-lab.

Captures: docs/images/wpf-site-version-changed.png, wpf-site-version-restored.png, redesign-final-PHP-Versions-Dark.png, redesign-final-Logs-Dark.png, redesign-Downloads-Light.png.

Not verified: full live keyboard traversal, real screen-reader announcements, menu acceptance through desktop automation, OS scaling/mixed DPI and all service transitions by pointer. Existing native control/resource checks and smoke interactions do not replace those acceptance checks. Existing backend limitations (including Nginx/Mailpit in-place updates) remain unchanged.

Run `Start-PhpDevTray.cmd` for the packaged 0.6.0 build. Exit the previous tray instance first so the existing single-instance process does not reopen the older executable.
