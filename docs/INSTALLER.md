# Windows installer

The Windows x64 setup is in `artifacts/installers/PhpDevTray-0.7.0-Setup-x64.exe`, with a SHA-256 sidecar.

The installer runs for the current user without administrator elevation. Default program location: `%LOCALAPPDATA%\Programs\PhpDevTray`. It creates a Start menu shortcut, offers an optional desktop shortcut, and registers an uninstaller in Windows Installed apps. Launching the app after setup is optional.

The full self-contained app and its CLI launcher are included. PHP, Nginx and Mailpit distributions are managed separately by the app. Existing application data remains at `%LOCALAPPDATA%\PhpDevTray`; setup does not overwrite it, and uninstall does not delete runtimes, configuration or captured email. User-enabled PHP PATH integration and its retained launcher live in that data folder and are preserved as well.

Exit the manager through **Exit and stop services** before installing an update or uninstalling. Setup detects the running-instance mutex and does not force termination. Closing the normal window merely hides it to the tray.

## Build

Publish the current app with `scripts/build.ps1 -Publish`, then run:

```powershell
.\scripts\build-installer.ps1 -CompilerPath 'C:\Path\To\Inno Setup 7\ISCC.exe'
```

The script checks the app/CLI/notices payload and rejects a publish/source version mismatch before compiling `installer/PhpDevTray.iss`. Output version and checksum are generated automatically. No signing identity is configured: the resulting installer is unsigned.

GitHub Actions also builds this installer on branch pushes and pull requests. The separate release workflow signs it with SignPath, publishes the signed installer and its new checksum to GitHub Releases, and submits stable releases to WinGet. See [release configuration and instructions](RELEASING.md) for the required credentials, signing policy and package metadata.

For this build, the official Inno Setup 7.1.1 x64 compiler was downloaded from its upstream release and its Authenticode publisher was validated as Pyrsys B.V. The compiler was extracted in portable mode under the workspace; it is not installed as a product on this machine. Compiler downloads and licensing information: https://jrsoftware.org/isdl.php . Portable-mode behavior: https://github.com/jrsoftware/issrc/blob/main/isportable.iss . Setup uses the compiler's standard wizard and uninstall engine.

Silent installation is supported through standard Inno Setup options, for example `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`. Those options do not start the application automatically.

## Verification result

Built with Inno Setup 7.1.1. The installer is 70.8 MiB and unsigned; its SHA-256 sidecar accompanies it.

The isolated test installed into `work/installer-verification/application` without shortcuts or automatic post-install launch. It passed:

- Fresh install and same-version reinstall.
- Byte-for-byte verification of all 598 published payload files, including the CLI bundle.
- Per-user Installed apps registration, install location and version.
- Launching the installed self-contained executable with an isolated profile, dark theme, rendered UI checks and the normal-window/tray lifecycle test.
- Uninstall removes program files and its product registration while preserving the separate profile, its generated settings and a data-retention marker.

Installer/uninstaller registry operations were tested with sandbox permission because HKCU registration is blocked inside the restricted execution environment. Test registration was removed afterward. The interactive wizard and real Start menu/desktop shortcut clicks were not exercised; the compiled script defines those entries. Setup does not enable startup-at-login or Windows PATH integration.
