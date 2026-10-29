param(
    [string]$DotNetPath = 'dotnet',
    [switch]$Publish,
    [switch]$SkipChecks
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
Push-Location -LiteralPath $projectRoot
try {
    & $DotNetPath build LocalServerManager.slnx -c Release --nologo --disable-build-servers "-p:RestoreConfigFile=$projectRoot\NuGet.Config"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $SkipChecks) {
        & $DotNetPath run --project tests/LocalServerManager.Checks -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Verification failed.' }
        & $DotNetPath run --project tests/LocalServerManager.UiChecks -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'UI resource verification failed.' }
    }
    if ($Publish) {
        $version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
        $publishPath = Join-Path $projectRoot "artifacts/releases/$version"
        $cliPath = Join-Path $publishPath 'cli'
        & $DotNetPath publish src/LocalServerManager.App -c Release -r win-x64 --self-contained true -o $publishPath --nologo --disable-build-servers "-p:RestoreConfigFile=$projectRoot\NuGet.Config"
        if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
        & $DotNetPath publish src/LocalServerManager.Cli -c Release -r win-x64 --self-contained true -o $cliPath --nologo --disable-build-servers "-p:RestoreConfigFile=$projectRoot\NuGet.Config"
        if ($LASTEXITCODE -ne 0) { throw 'PHP launcher publish failed.' }
        Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $publishPath
        Write-Output "Run: $publishPath\LocalServerManager.exe"
    }
}
finally { Pop-Location }
