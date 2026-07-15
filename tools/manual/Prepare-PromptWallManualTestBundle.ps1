[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'artifacts\manual-test'
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$bundleRoot = Join-Path $OutputRoot "PromptWall-manual-test-$stamp"
$appSource = Join-Path $repoRoot "TinyWall\bin\$Configuration"
$probeSource = Join-Path $repoRoot "tests\PromptWall.NetworkProbe\bin\$Configuration\PromptWall.NetworkProbe.exe"
$installerSource = Join-Path $repoRoot "MsiSetup\bin\$Configuration"
$appDestination = Join-Path $bundleRoot 'app'
$probeDestination = Join-Path $bundleRoot 'probes'
$installerDestination = Join-Path $bundleRoot 'installers'

$installers = @(
    'PromptWall_x86.msi',
    'PromptWall_x64.msi',
    'PromptWall_arm64.msi'
)

$required = @(
    (Join-Path $appSource 'PromptWall.exe'),
    (Join-Path $appSource 'PromptWall.exe.config'),
    $probeSource,
    (Join-Path $PSScriptRoot 'README.md')
)
$required += $installers | ForEach-Object { Join-Path $installerSource $_ }
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing bundle input: $path"
    }
}

New-Item -ItemType Directory -Force -Path $appDestination, $probeDestination, $installerDestination | Out-Null
Copy-Item -Path (Join-Path $appSource '*') -Destination $appDestination -Recurse -Force
Copy-Item -LiteralPath $probeSource -Destination (Join-Path $probeDestination 'PromptWall.AllowProbe.exe')
Copy-Item -LiteralPath $probeSource -Destination (Join-Path $probeDestination 'PromptWall.IgnoreProbe.exe')
foreach ($installer in $installers) {
    Copy-Item -LiteralPath (Join-Path $installerSource $installer) -Destination (Join-Path $installerDestination $installer)
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $bundleRoot 'README.md')

$manifest = [ordered]@{
    CreatedUtc = (Get-Date).ToUniversalTime().ToString('O')
    Configuration = $Configuration
    PromptWallSha256 = (Get-FileHash -LiteralPath (Join-Path $appDestination 'PromptWall.exe') -Algorithm SHA256).Hash
    AllowProbeSha256 = (Get-FileHash -LiteralPath (Join-Path $probeDestination 'PromptWall.AllowProbe.exe') -Algorithm SHA256).Hash
    IgnoreProbeSha256 = (Get-FileHash -LiteralPath (Join-Path $probeDestination 'PromptWall.IgnoreProbe.exe') -Algorithm SHA256).Hash
    InstallerSha256 = [ordered]@{
        x86 = (Get-FileHash -LiteralPath (Join-Path $installerDestination 'PromptWall_x86.msi') -Algorithm SHA256).Hash
        x64 = (Get-FileHash -LiteralPath (Join-Path $installerDestination 'PromptWall_x64.msi') -Algorithm SHA256).Hash
        arm64 = (Get-FileHash -LiteralPath (Join-Path $installerDestination 'PromptWall_arm64.msi') -Algorithm SHA256).Hash
    }
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $bundleRoot 'bundle-manifest.json') -Encoding UTF8

$zipPath = "$bundleRoot.zip"
Compress-Archive -LiteralPath $bundleRoot -DestinationPath $zipPath -CompressionLevel Optimal
Write-Output "Bundle=$bundleRoot"
Write-Output "Zip=$zipPath"
