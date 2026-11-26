# Microsoft Store releases

PHP DevTray is packaged as a self-contained Windows x64 MSIX for submission to Microsoft Store. Microsoft signs the package after certification and distributes app updates. No SignPath account, signing certificate, installer hosting, or WinGet submission is used. This repository prepares an upload artifact; it does not automatically submit or publish in Partner Center.

## One-time setup

1. Register a [Microsoft Store developer account](https://developer.microsoft.com/microsoft-store/register/) and reserve the app name in Partner Center. Choose the **MSIX/AppX** submission route. The MSI/EXE route still requires publisher-managed signing and is not used here.
2. The reserved app identity is already recorded in `packaging/AppxManifest.xml`: package name `LilianMoulanier.PHPDevTray`, publisher `CN=737AB8E3-B8A7-4EB3-B41A-E15E782D42DB`, and publisher display name `Lilian Moulanier`. These are public identifiers. Keep them consistent with **Product management > Product identity** in Partner Center.
3. Create a GitHub Actions environment named **store**. Restrict deployment refs to release tags (`v*`) and configure reviewers if desired. No variables or secrets are required with the committed identity. The workflow also supports these optional environment variables to override it:

| Variable | Partner Center value |
| --- | --- |
| `STORE_PACKAGE_NAME` | `Package/Identity/Name` |
| `STORE_PUBLISHER` | `Package/Identity/Publisher`, including `CN=` |
| `STORE_PUBLISHER_DISPLAY_NAME` | `Package/Properties/PublisherDisplayName` |

No Store API credentials are needed for the manual upload flow. Old `release` environment secrets and variables used for SignPath and WinGet can be removed from GitHub separately; deleting the workflows does not delete server-side configuration.

## Versioning

`Directory.Build.props` contains two related versions:

- `Version` and `InformationalVersion` describe the app, currently `0.8.0` / `0.8.0-preview`. The Git tag must match `v` plus `InformationalVersion` without build metadata: currently `v0.8.0-preview`.
- `StorePackageVersion` is the four-part numeric MSIX identity version, currently `1.0.1.0`. Its first part must be nonzero; its fourth part must stay `0` for Store use. Increment this version for each new package version sent to the Store, including previews. The Store version does not mean the app has reached feature version 1.0.

Preview suffixes cannot be encoded in an MSIX identity version. Use a private audience/package flight in Partner Center to test a preview before public availability; a `-preview` Git tag by itself does not create a private Store release. The workflow only builds the package.

## Build and submit

1. Set the versions, commit and push, then push a matching release tag, or manually run **Prepare Microsoft Store submission** with an existing tag.
2. The workflow uses a GitHub-hosted `windows-2025` runner, the SDK selected by `global.json`, and the runner's Windows SDK `MakeAppx.exe`. It builds the app, runs the existing checks, publishes both the WPF app and CLI self-contained, validates the source/payload version, generates icons and the manifest, and packages with MakeAppx validation enabled.
3. Download the **microsoft-store-submission** workflow artifact. Upload its `.msix` to the app's **Packages** page in Partner Center. The checksum and JSON identity summary are for verification and are not uploaded as packages.
4. Complete the listing, screenshots, privacy policy, support information, age ratings, pricing/availability and certification notes in Partner Center. Set the intended audience/release schedule, then submit for certification. Microsoft signs accepted MSIX packages; do not upload the old Inno Setup EXE or a development-identity package.
5. Customers install and update through Microsoft Store after approval/publication. GitHub Actions success means the upload artifact is ready, not that the app is live.

Branch pushes and pull requests run **Build Microsoft Store package**, using a clearly marked development identity and no Store configuration. Its **development-msix** artifact is only for packaging verification; it cannot be submitted as this app's Store package.

## Local build

Requirements: Windows x64, .NET 10 SDK, PowerShell, and Windows SDK with `MakeAppx.exe` (10.0.19041 or newer). The script discovers an installed x64 Windows SDK, or accepts `-MakeAppxPath` explicitly.

```powershell
.\scripts\build.ps1 -Publish
.\scripts\build-store-package.ps1
```

The package uses the committed Partner Center identity by default. `-PackageName`, `-Publisher` and `-PublisherDisplayName` can override it. To check packaging with a separate development identity instead:

```powershell
.\scripts\build-store-package.ps1 -DevelopmentIdentity
```

Outputs are under ignored `artifacts/store/`: the unsigned `.msix`, SHA-256 sidecar, identity summary, and a fresh `layout-<id>` directory for inspection. The existing application logo has one vector source, `src/PhpDevTray.App/Assets/AppLogo.xaml`. `scripts/new-app-icons.ps1` renders it into the native multi-size ICO during every app build and into Store PNGs through `scripts/new-store-assets.ps1`. Window/taskbar and notification icons use this same logo; notification status is a small badge, rendered at the taskbar monitor's DPI. The full app and `cli/` bundle are included; Caddy, FrankenPHP, PHP and Mailpit distributions and user data are not packaged.

To export the same logo for the Store listing (300px/1024px PNG and SVG), run `scripts/new-app-icons.ps1 -OutputDirectory artifacts/store-listing -ListingAssets`.

The unsigned package is intended for Store upload, not installation by customers. For local packaged testing, use Developer Mode and register its layout with `Add-AppxPackage -Register <absolute-layout-path>\AppxManifest.xml`, or sign a copy with a locally trusted test certificate. Do not add the special unsigned-testing publisher OID to a Store submission identity. Test-only signing/trust and Developer Mode are not enabled by the build scripts.

## Certification and packaged acceptance

The app is a full-trust desktop application (`packagedClassicApp`, `mediumIL`) and declares **runFullTrust**. In Partner Center, explain that it manages explicitly downloaded/imported Caddy, FrankenPHP, PHP and Mailpit processes for local development, reads selected site folders, opens loopback listeners, and uses Windows Job Objects to contain owned service processes. The tray runs without elevation. The optional Microsoft Visual C++ installer can request elevation to update the shared Windows runtime. Microsoft reviews capability requests and the app's runtime downloads; packaging alone does not establish Store eligibility.

Store workspace data is stored under `%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalState\PhpDevTray`. It survives app updates, but Windows removes package-owned data on uninstall/reset. Export anything you want to retain first. Unpackaged development builds use `%LOCALAPPDATA%\PhpDevTray`; existing development profiles are not automatically migrated.

Command-line PHP uses a package execution alias, **php.exe**, managed in Windows Settings > App execution aliases. The alias and manager read the same package-owned selection; CLI processes remain independent of the tray's service Jobs. Store builds do not modify user PATH. Another PHP installation earlier in PATH can take precedence; use `where.exe php` to diagnose. The alias uses the default Store profile, even if the manager is launched with a custom `--data-directory` for testing.

Before a public submission, run Windows App Certification Kit on the package and test an installed package, ideally through a private Store flight:

- Start menu launch, tray restore, minimize/close preserving services, and explicit Exit stopping them.
- Enable/disable **Settings > Startup > Start PHP DevTray with Windows**, sign out/in, and verify that configured services start with only the tray icon. Confirm Windows Startup settings can disable it, that the app reports this state, and that a manual launch opens the manager. Repeat after a package update.
- Runtime downloads/imports, Visual C++ prerequisites, PHP extensions, FrankenPHP site access outside package storage, Mailpit capture, and PHP patch activation/recovery.
- `php -v`, arguments, working directory, stdin/stdout/stderr, exit codes and CLI lifetime after tray Exit; enable/disable the alias and test conflicts with another PHP installation.
- Update an installed package to a higher `StorePackageVersion`, confirm workspace retention, and verify uninstall/reset behavior using a disposable profile. Exit the tray app and stop independent CLI sessions before testing an update.

These packaged-runtime checks require an installed package and are separate from the existing unpackaged process/UI regression checks and MakeAppx schema validation.

References: [Store signing](https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options), [package requirements and versioning](https://learn.microsoft.com/windows/apps/publish/publish-your-app/package-version-numbering?pivots=store-installer-msix), [desktop packaging behavior](https://learn.microsoft.com/windows/msix/desktop/desktop-to-uwp-behind-the-scenes), [app execution aliases](https://learn.microsoft.com/windows/apps/desktop/modernize/desktop-to-uwp-extensions#start-your-application-by-using-an-alias), and [Store policies](https://learn.microsoft.com/windows/apps/publish/store-policies).
