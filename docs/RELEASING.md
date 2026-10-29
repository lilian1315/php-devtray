# GitHub releases, SignPath and WinGet

The workflows build the self-contained Windows x64 application, run the existing checks, compile the Inno Setup installer, sign it through SignPath, publish its signed bytes and checksum to GitHub Releases, and submit stable releases to the WinGet community repository.

## One-time configuration

Push this repository to a public GitHub repository. Create a GitHub Actions environment named **release**. Restrict its allowed deployment refs to release tags (`v*`); configure required reviewers if appropriate for the project. The workflows use GitHub-hosted `windows-2025` runners, the SDK selected by `global.json`, and the runner's preinstalled Inno Setup 6 compiler. Pull requests and branch pushes only build unsigned installers and do not use release secrets.

Configure these environment secrets:

| Secret | Purpose |
| --- | --- |
| `SIGNPATH_API_TOKEN` | SignPath CI user token authorized for the project and production signing policy. |
| `WINGET_GITHUB_TOKEN` | GitHub **classic PAT** with `public_repo`, belonging to an account allowed to fork and submit pull requests to `microsoft/winget-pkgs`. The repository's `GITHUB_TOKEN` cannot submit cross-repository PRs. |

Configure these environment variables:

| Variable | Value |
| --- | --- |
| `SIGNPATH_ORGANIZATION_ID` | SignPath organization ID. |
| `SIGNPATH_PROJECT_SLUG` | SignPath project linked to this GitHub repository. |
| `SIGNPATH_SIGNING_POLICY_SLUG` | Production policy with a publicly trusted Authenticode certificate and timestamping. Test certificates fail the release checks. |
| `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` | Artifact configuration containing [this XML](../installer/signpath-artifact-configuration.xml). |
| `WINGET_PACKAGE_IDENTIFIER` | A unique identifier such as `YourPublisher.PhpDevTray`. Keep this identifier stable after the first WinGet submission. |
| `WINGET_LICENSE` | The application's actual license identifier or name. Decide the license and add its terms to the repository before publishing. There is currently no application license file; third-party notices do not license the application. |

In SignPath, install/authorize the SignPath GitHub App for this repository, add GitHub.com as a trusted build system, and link the project to the repository. Import `installer/signpath-artifact-configuration.xml` into the configured artifact configuration. It requires one matching installer EXE inside the ZIP produced by `actions/upload-artifact`. Configure signing permissions and any required signing approvals in the production policy. Signing waits up to 30 minutes for completion.

The GitHub release job has `actions: read` for SignPath provenance and `contents: write` for Releases. External actions are pinned to commit SHAs. The WinGet job receives its PAT only through environment variables; the token is never passed as a command-line argument. WinGet Create is downloaded from the pinned Microsoft release `v1.12.13.0`, checked against its GitHub asset SHA-256 digest, and checked for a valid Microsoft Authenticode signature before execution.

## Release a version

1. Set `Version` and `InformationalVersion` in `Directory.Build.props`. `Version` is a numeric `major.minor.patch` used by the installer. For a stable release, set `InformationalVersion` to that same value. For a preview, use a suffix such as `0.7.0-preview`.
2. Commit and push the changes. Create and push a tag matching `v` plus `InformationalVersion`, without build metadata. For the current source, this is `v0.7.0-preview`; a stable `v0.7.0` requires removing the preview suffix first.
3. The **Sign and release installer** workflow builds and checks the tagged source, compiles and uploads the unsigned installer for SignPath provenance, signs it, and rejects any signature that is invalid or lacks a timestamp.
4. It generates a new SHA-256 sidecar from the **signed** EXE, uploads the signed files as a workflow artifact, creates a draft release with both files attached, and publishes it. Preview tags produce GitHub prereleases and skip WinGet.
5. Stable releases call **Submit release to WinGet**. This downloads the public signed release asset, verifies its signature and version, generates and saves the three manifest files, and submits them using Microsoft's WinGet Create. That tool validates the YAML through its bundled WinGetUtil before submitting, without depending on a preinstalled `winget` CLI on the runner.

WinGet publication is a pull request to `microsoft/winget-pkgs`. Availability in `winget install` follows Microsoft's validation and merge; a successful workflow means the PR was submitted, not that it has already merged. The same generation path supports both the initial package submission and later versions. Package name, publisher and uninstall product code are read from the installer source so they match Installed apps registration. The manifest describes a per-user x64 Inno installer and hashes the signed release asset. PHP distributions remain separately managed by the app.

Only the outer installer is signed by this workflow. Signing the packaged application/CLI or the generated uninstaller would require additional signing stages.

## Retries and local verification

Run **Sign and release installer** manually with an existing tag to retry a failure before release creation. Published release files are never overwritten. If draft publication failed, inspect the draft and finish publishing it, or delete that unpublished draft before retrying; do not replace an already public installer because WinGet pins its hash.

Run **Submit release to WinGet** manually with an existing stable release tag to retry just the WinGet stage. Inspect existing submissions before retrying: WinGet accepts only one PR per package version. The generated `winget-manifests` artifact is retained even if validation or submission fails.

To generate manifests locally, use the signed release installer, then validate the result:

```powershell
.\scripts\new-winget-manifest.ps1 `
  -InstallerPath .\artifacts\signed\PhpDevTray-0.7.0-Setup-x64.exe `
  -PackageIdentifier YourPublisher.PhpDevTray `
  -License YourActualLicense `
  -Repository YourGitHubAccount/YourRepository `
  -Tag v0.7.0
winget validate --manifest .\artifacts\winget
```

The ordinary local installer build remains unsigned. Build/download outputs, tools and manifests belong under the ignored `artifacts/` directory.

References: [SignPath GitHub integration](https://docs.signpath.io/trusted-build-systems/github), [artifact configuration syntax](https://docs.signpath.io/artifact-configuration/syntax), [WinGet manifests](https://learn.microsoft.com/windows/package-manager/package/manifest), [WinGet Create submission](https://github.com/microsoft/winget-create/blob/main/doc/submit.md), and [CI token setup](https://github.com/microsoft/winget-create/blob/main/doc/token.md).
