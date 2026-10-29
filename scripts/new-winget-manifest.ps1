param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]*(\.[A-Za-z0-9][A-Za-z0-9-]*)+$')][string]$PackageIdentifier,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$License,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository,
    [Parameter(Mandatory)][string]$Tag,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$version = [string]([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $Tag -cne "v$version") { throw 'WinGet requires a stable tag matching Version.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts/winget' }
$installer = Get-Item -LiteralPath $InstallerPath -ErrorAction Stop
$expectedName = "PhpDevTray-$version-Setup-x64.exe"
if ($installer.Name -cne $expectedName) { throw "Expected installer named $expectedName." }
$signature = Get-AuthenticodeSignature -LiteralPath $installer.FullName
if ($signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate) {
    throw 'WinGet requires a valid, timestamped production installer signature.'
}
$installerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($installer.FullName)
$expectedVersion = [version]$version
if ($installerVersion.FileMajorPart -ne $expectedVersion.Major -or
    $installerVersion.FileMinorPart -ne $expectedVersion.Minor -or
    $installerVersion.FileBuildPart -ne $expectedVersion.Build -or $installerVersion.FilePrivatePart -ne 0) {
    throw "Installer version $($installerVersion.FileVersion) does not match $version."
}
$hash = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash
$setup = Get-Content -LiteralPath (Join-Path $projectRoot 'installer/PhpDevTray.iss')
function Get-SetupValue([string]$Name) {
    $matches = @($setup | Where-Object { $_.StartsWith("$Name=") })
    if ($matches.Count -ne 1) { throw "Expected exactly one $Name in the installer source." }
    return $matches[0].Substring($Name.Length + 1)
}
function ConvertTo-YamlString([string]$Value) {
    if ($Value -match '[\r\n\x00-\x1f]') { throw 'Manifest metadata must be a single line without control characters.' }
    return "'" + $Value.Replace("'", "''") + "'"
}
$packageName = ConvertTo-YamlString (Get-SetupValue 'AppName')
$publisher = ConvertTo-YamlString (Get-SetupValue 'AppPublisher')
$appId = Get-SetupValue 'AppId'
if ($appId -notmatch '^\{\{[0-9A-Fa-f-]{36}\}$') { throw 'Expected a GUID AppId in the installer source.' }
$productCode = ConvertTo-YamlString ($appId.Substring(1) + '_is1')
$identifier = ConvertTo-YamlString $PackageIdentifier
$packageVersion = ConvertTo-YamlString $version
$licenseValue = ConvertTo-YamlString $License
$repositoryUrl = "https://github.com/$Repository"
$installerUrl = ConvertTo-YamlString "$repositoryUrl/releases/download/$Tag/$expectedName"
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$common = "PackageIdentifier: $identifier`nPackageVersion: $packageVersion"
$versionManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.12.0.schema.json
$common
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.12.0
"@
$installerManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.12.0.schema.json
$common
InstallerType: inno
Scope: user
InstallModes:
  - interactive
  - silent
  - silentWithProgress
UpgradeBehavior: install
InstallerSwitches:
  Custom: /NORESTART
AppsAndFeaturesEntries:
  - DisplayName: $packageName
    Publisher: $publisher
    DisplayVersion: $packageVersion
    ProductCode: $productCode
Installers:
  - Architecture: x64
    InstallerUrl: $installerUrl
    InstallerSha256: $hash
ManifestType: installer
ManifestVersion: 1.12.0
"@
$localeManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.12.0.schema.json
$common
PackageLocale: en-US
Publisher: $publisher
PublisherUrl: '$repositoryUrl'
PublisherSupportUrl: '$repositoryUrl/issues'
PackageName: $packageName
PackageUrl: '$repositoryUrl'
License: $licenseValue
ShortDescription: Manage local PHP development on Windows with PHP versions, Nginx sites and captured email.
Tags:
  - php
  - development
  - nginx
  - local-server
ReleaseNotesUrl: '$repositoryUrl/releases/tag/$Tag'
ManifestType: defaultLocale
ManifestVersion: 1.12.0
"@
foreach ($manifest in @(
    @{ Name = "$PackageIdentifier.yaml"; Content = $versionManifest },
    @{ Name = "$PackageIdentifier.installer.yaml"; Content = $installerManifest },
    @{ Name = "$PackageIdentifier.locale.en-US.yaml"; Content = $localeManifest }
)) {
    $path = Join-Path $OutputDirectory $manifest.Name
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($path), $manifest.Content + "`n", [Text.UTF8Encoding]::new($false))
}
Write-Output "WinGet manifests: $([IO.Path]::GetFullPath($OutputDirectory))"
