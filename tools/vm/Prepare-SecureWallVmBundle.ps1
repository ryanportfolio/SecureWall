[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'artifacts\vm-validation'
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$bundleRoot = Join-Path $OutputRoot "SecureWall-vm-test-$stamp"
$appSource = Join-Path $repoRoot "TinyWall\bin\$Configuration"
$probeSource = Join-Path $repoRoot "tests\SecureWall.NetworkProbe\bin\$Configuration"
$appDestination = Join-Path $bundleRoot 'app'
$probeDestination = Join-Path $bundleRoot 'probes'

$required = @(
    (Join-Path $appSource 'SecureWall.exe'),
    (Join-Path $appSource 'SecureWall.exe.config'),
    (Join-Path $probeSource 'SecureWall.NetworkProbe.exe'),
    (Join-Path $PSScriptRoot 'Run-SecureWallVmValidation.ps1'),
    (Join-Path $PSScriptRoot 'README.md')
)
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing bundle input: $path"
    }
}

New-Item -ItemType Directory -Force -Path $appDestination, $probeDestination | Out-Null
Copy-Item -Path (Join-Path $appSource '*') -Destination $appDestination -Recurse -Force
Copy-Item -LiteralPath (Join-Path $probeSource 'SecureWall.NetworkProbe.exe') -Destination (Join-Path $probeDestination 'SecureWall.AllowProbe.exe')
Copy-Item -LiteralPath (Join-Path $probeSource 'SecureWall.NetworkProbe.exe') -Destination (Join-Path $probeDestination 'SecureWall.IgnoreProbe.exe')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Run-SecureWallVmValidation.ps1') -Destination (Join-Path $bundleRoot 'Run-Validation.ps1')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $bundleRoot 'README.md')

$manifest = [ordered]@{
    CreatedUtc = (Get-Date).ToUniversalTime().ToString('O')
    Configuration = $Configuration
    SecureWallSha256 = (Get-FileHash -LiteralPath (Join-Path $appDestination 'SecureWall.exe') -Algorithm SHA256).Hash
    ProbeSha256 = (Get-FileHash -LiteralPath (Join-Path $probeDestination 'SecureWall.AllowProbe.exe') -Algorithm SHA256).Hash
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $bundleRoot 'bundle-manifest.json') -Encoding UTF8

$zipPath = "$bundleRoot.zip"
Compress-Archive -LiteralPath $bundleRoot -DestinationPath $zipPath -CompressionLevel Optimal
Write-Output "Bundle=$bundleRoot"
Write-Output "Zip=$zipPath"
