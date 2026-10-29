param(
    [string]$CompilerPath = 'ISCC.exe',
    [string]$PublishDirectory,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $projectRoot "artifacts/releases/$version" }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts/installers' }
$publishPath = [IO.Path]::GetFullPath($PublishDirectory)
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($required in @('PhpDevTray.exe','PhpDevTray.runtimeconfig.json','cli/php.exe','THIRD-PARTY-NOTICES.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishPath $required) -PathType Leaf)) { throw "Missing published file: $required. Publish the app first." }
}
$appVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishPath 'PhpDevTray.exe')).ProductVersion.Split('+')[0].Split('-')[0]
if ($appVersion -ne $version) { throw "Publish version $appVersion does not match source version $version." }
$compiler = Get-Command $CompilerPath -ErrorAction Stop
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
& $compiler.Source /Q "/DAppVersion=$version" "/DPublishDir=$publishPath" "/DInstallerOutput=$outputPath" (Join-Path $projectRoot 'installer/PhpDevTray.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$installer = Join-Path $outputPath "PhpDevTray-$version-Setup-x64.exe"
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($installer + '.sha256') -Value "$hash  $([IO.Path]::GetFileName($installer))" -Encoding ascii
Write-Output "Installer: $installer"
