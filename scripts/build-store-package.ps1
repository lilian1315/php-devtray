param(
    [string]$PackageName,
    [string]$Publisher,
    [string]$PublisherDisplayName,
    [switch]$DevelopmentIdentity,
    [string]$MakeAppxPath,
    [string]$PublishDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$props = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup
[xml]$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'packaging/AppxManifest.xml') -Raw
$version = [string]$props.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be numeric major.minor.patch for the Store.' }
$storeVersion = [string]$props.StorePackageVersion
if ($storeVersion -notmatch '^\d+\.\d+\.\d+\.0$') { throw 'StorePackageVersion must have four numeric parts, with the last part reserved as 0.' }
$packageVersion = [version]$storeVersion
if ($packageVersion.Major -lt 1) { throw 'StorePackageVersion must have a nonzero major version.' }
foreach ($component in @($packageVersion.Major, $packageVersion.Minor, $packageVersion.Build)) {
    if ($component -gt 65535) { throw 'Store version components must be at most 65535.' }
}
if ($DevelopmentIdentity) {
    if ($PackageName -or $Publisher -or $PublisherDisplayName) { throw 'DevelopmentIdentity cannot be combined with Store identity arguments.' }
    $PackageName = 'PhpDevTray.Development'
    $Publisher = 'CN=PhpDevTray Development'
    $PublisherDisplayName = 'PHP DevTray Development'
}
else {
    if ([string]::IsNullOrWhiteSpace($PackageName)) { $PackageName = $manifest.Package.Identity.Name }
    if ([string]::IsNullOrWhiteSpace($Publisher)) { $Publisher = $manifest.Package.Identity.Publisher }
    if ([string]::IsNullOrWhiteSpace($PublisherDisplayName)) { $PublisherDisplayName = $manifest.Package.Properties.PublisherDisplayName }
    foreach ($name in @('PackageName', 'Publisher', 'PublisherDisplayName')) {
        if ([string]::IsNullOrWhiteSpace((Get-Variable -Name $name -ValueOnly))) {
            throw "Supply $name from Partner Center Product identity, or use -DevelopmentIdentity for a test package."
        }
    }
    if ($PackageName -eq 'PhpDevTray.Development' -or $Publisher -eq 'CN=PhpDevTray Development') {
        throw 'The development identity cannot be used for Store submission.'
    }
}
if ($PackageName -notmatch '^[A-Za-z0-9.-]{3,50}$') { throw 'PackageName must be 3-50 characters using letters, digits, dots or hyphens.' }
if ($Publisher -notmatch '^CN=' -or $Publisher -match '[\r\n]') { throw 'Publisher must be the CN= identity supplied by Partner Center.' }
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $projectRoot "artifacts/releases/$version" }
$publishPath = [IO.Path]::GetFullPath($PublishDirectory)
foreach ($required in @('PhpDevTray.exe', 'PhpDevTray.runtimeconfig.json', 'cli/php.exe', 'cli/PhpDevTray.Core.dll', 'THIRD-PARTY-NOTICES.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishPath $required) -PathType Leaf)) { throw "Missing published file: $required. Run scripts/build.ps1 -Publish first." }
}
$appVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishPath 'PhpDevTray.exe')).ProductVersion.Split('+')[0]
$expectedVersion = [string]$props.InformationalVersion
if (-not $expectedVersion) { $expectedVersion = $version }
if ($appVersion -ne $expectedVersion.Split('+')[0]) { throw "Published version $appVersion does not match source version $expectedVersion." }
if (-not $MakeAppxPath) {
    $sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $candidates = @(Get-ChildItem -LiteralPath $sdkBin -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^10\.0\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64/makeappx.exe' } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($candidates.Count) { $MakeAppxPath = $candidates[0] }
    else { $MakeAppxPath = 'makeappx.exe' }
}
$makeAppx = Get-Command $MakeAppxPath -ErrorAction Stop
$outputPath = Join-Path $projectRoot 'artifacts/store'
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
# A fresh layout prevents stale files from a previous package entering this one.
$layout = Join-Path $outputPath ("layout-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $layout | Out-Null
Get-ChildItem -LiteralPath $publishPath | Copy-Item -Destination $layout -Recurse
$manifest.Package.Identity.SetAttribute('Name', $PackageName)
$manifest.Package.Identity.SetAttribute('Publisher', $Publisher)
$manifest.Package.Identity.SetAttribute('Version', $packageVersion.ToString())
$manifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName
$manifest.Save((Join-Path $layout 'AppxManifest.xml'))
& (Join-Path $PSScriptRoot 'new-store-assets.ps1') -OutputDirectory (Join-Path $layout 'Assets')
$suffix = if ($DevelopmentIdentity) { '-development' } else { '' }
$package = Join-Path $outputPath "PhpDevTray-$packageVersion-x64$suffix.msix"
$packagingLog = "$package.log"
& $makeAppx.Source pack /d $layout /p $package /o > $packagingLog
if ($LASTEXITCODE -ne 0) {
    Get-Content -LiteralPath $packagingLog -Tail 30 | Write-Output
    throw "MSIX packaging failed. See $packagingLog."
}
$hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$package.sha256" -Value "$hash  $([IO.Path]::GetFileName($package))" -Encoding ascii
[ordered]@{
    packageName = $PackageName
    publisher = $Publisher
    publisherDisplayName = $PublisherDisplayName
    packageVersion = $packageVersion.ToString()
    informationalVersion = $expectedVersion
    developmentIdentity = [bool]$DevelopmentIdentity
    layout = $layout
    sha256 = $hash
} | ConvertTo-Json | Set-Content -LiteralPath "$package.json" -Encoding utf8
Write-Output "Unsigned MSIX: $package"
Write-Output "Development layout: $layout"
if ($DevelopmentIdentity) { Write-Warning 'Test identity only. Do not submit this package to Partner Center.' }
else { Write-Output 'Upload the MSIX to Partner Center. Microsoft signs it after certification.' }
