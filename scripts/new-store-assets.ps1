param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'new-app-icons.ps1') -OutputDirectory $OutputDirectory
