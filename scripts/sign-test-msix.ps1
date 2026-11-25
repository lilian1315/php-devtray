param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$SignToolPath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem,System.Security
$projectRoot = Split-Path -Parent $PSScriptRoot
$source = (Get-Item -LiteralPath $PackagePath).FullName
$originalHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$unsigned = [IO.Compression.ZipFile]::OpenRead($source)
try {
    if ($unsigned.GetEntry('AppxSignature.p7x')) { throw 'Supply the unsigned Store package.' }
    $reader = [IO.StreamReader]::new($unsigned.GetEntry('AppxManifest.xml').Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
}
finally { $unsigned.Dispose() }
$subject = [string]$manifest.Package.Identity.Publisher
$friendlyName = 'PHP DevTray local MSIX test signing'
$certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
    $_.Subject -ceq $subject -and $_.FriendlyName -eq $friendlyName -and $_.HasPrivateKey -and
    $_.NotAfter -gt (Get-Date).AddDays(7) -and
    @($_.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
        ForEach-Object { $_.EnhancedKeyUsages } | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' }).Count -gt 0
} | Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $certificate) {
    $certificate = New-SelfSignedCertificate -Type Custom -Subject $subject -FriendlyName $friendlyName `
        -CertStoreLocation 'Cert:\CurrentUser\My' -KeyAlgorithm RSA -KeyLength 3072 `
        -HashAlgorithm SHA256 -KeyUsage DigitalSignature -KeyExportPolicy NonExportable `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
        -NotAfter (Get-Date).AddMonths(12)
}
$output = Join-Path $projectRoot 'artifacts/self-signed'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$packageName = [IO.Path]::GetFileNameWithoutExtension($source) + '-selfsigned.msix'
$signedPath = Join-Path $output $packageName
Copy-Item -LiteralPath $source -Destination $signedPath -Force
$signTool = Get-Command $SignToolPath -ErrorAction Stop
& $signTool.Source sign /fd SHA256 /s My /sha1 $certificate.Thumbprint /d 'PHP DevTray - local testing' $signedPath > "$signedPath.sign.log"
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath "$signedPath.sign.log"; throw 'MSIX test signing failed.' }
$certificatePath = Join-Path $output 'PhpDevTray-Test.cer'
Export-Certificate -Cert $certificate -FilePath $certificatePath -Type CERT -Force | Out-Null

$signed = [IO.Compression.ZipFile]::OpenRead($signedPath)
$unsigned = [IO.Compression.ZipFile]::OpenRead($source)
try {
    $signature = $signed.GetEntry('AppxSignature.p7x')
    if (-not $signature) { throw 'The signed package has no signature.' }
    $buffer = [IO.MemoryStream]::new()
    $signatureStream = $signature.Open()
    try { $signatureStream.CopyTo($buffer); $signatureBytes = $buffer.ToArray() }
    finally { $signatureStream.Dispose(); $buffer.Dispose() }
    if ([Text.Encoding]::ASCII.GetString($signatureBytes,0,4) -cne 'PKCX') { throw 'Unexpected MSIX signature format.' }
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
    $cms.Decode([byte[]]$signatureBytes[4..($signatureBytes.Length - 1)])
    $cms.CheckSignature($true) # Verify cryptographically without changing Windows trust.
    if ($cms.SignerInfos.Count -ne 1 -or $cms.SignerInfos[0].Certificate.Thumbprint -cne $certificate.Thumbprint) {
        throw 'The package signer does not match the exported test certificate.'
    }
    foreach ($entry in $unsigned.Entries | Where-Object { $_.FullName -ne '[Content_Types].xml' }) {
        $other = $signed.GetEntry($entry.FullName)
        if (-not $other) { throw "Signed payload is missing $($entry.FullName)." }
        $firstStream = $entry.Open(); $secondStream = $other.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            $firstHash = [Convert]::ToBase64String($sha.ComputeHash($firstStream))
            $secondHash = [Convert]::ToBase64String($sha.ComputeHash($secondStream))
            if ($firstHash -cne $secondHash) { throw "Signing changed payload file $($entry.FullName)." }
        }
        finally { $firstStream.Dispose(); $secondStream.Dispose(); $sha.Dispose() }
    }
}
finally { $signed.Dispose(); $unsigned.Dispose() }
if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -cne $originalHash) { throw 'The original Store package changed.' }
$hash = (Get-FileHash -LiteralPath $signedPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$signedPath.sha256" -Value "$hash  $packageName" -Encoding ascii
$thumbprint = $certificate.Thumbprint
$installer = @"
#Requires -RunAsAdministrator
`$ErrorActionPreference = 'Stop'
`$certificatePath = Join-Path `$PSScriptRoot 'PhpDevTray-Test.cer'
`$packagePath = Join-Path `$PSScriptRoot '$packageName'
`$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(`$certificatePath)
if (`$certificate.Thumbprint -cne '$thumbprint') { throw 'Unexpected test certificate.' }
if ((Get-FileHash -LiteralPath `$packagePath -Algorithm SHA256).Hash -ine '$hash') { throw 'MSIX checksum mismatch.' }
Import-Certificate -FilePath `$certificatePath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
Add-AppxPackage -Path `$packagePath -ErrorAction Stop
Write-Output 'PHP DevTray test package installed.'
"@
Set-Content -LiteralPath (Join-Path $output 'Install-TestPackage.ps1') -Value $installer -Encoding utf8
[ordered]@{
    package = $packageName
    publisher = $subject
    certificateThumbprint = $thumbprint
    certificateExpires = $certificate.NotAfter.ToUniversalTime().ToString('o')
    signatureVerified = $true
    trustInstalled = $false
    sourceSha256 = $originalHash.ToLowerInvariant()
    sha256 = $hash
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'test-signing.json') -Encoding utf8
Compress-Archive -LiteralPath @($signedPath,$certificatePath,(Join-Path $output 'Install-TestPackage.ps1'),"$signedPath.sha256") `
    -DestinationPath (Join-Path $output 'PhpDevTray-selfsigned.zip') -CompressionLevel NoCompression -Force
Write-Output "Self-signed MSIX: $signedPath"
Write-Output "Public test certificate: $certificatePath"
Write-Output "Certificate thumbprint: $thumbprint"
Write-Output 'PASS: cryptographic signature and unchanged payload verified. No machine trust was installed.'
