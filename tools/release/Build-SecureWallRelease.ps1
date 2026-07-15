[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('x86', 'x64', 'arm64')]
    [string[]]$Platforms = @('x86', 'x64', 'arm64'),

    [string]$WixRoot,
    [string]$MsBuildPath,
    [string]$OutputDirectory,
    [string[]]$PackageSource,
    [switch]$SuppressValidation
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Invoke-NativeCommand {
    param(
        [string]$FilePath,
        [string[]]$ArgumentList
    )

    & $FilePath $ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $FilePath $($ArgumentList -join ' ')"
    }
}

if (-not $WixRoot) {
    $WixRoot = Join-Path $repoRoot '.tmp\tools\wix314'
}
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts\release'
}

$WixRoot = [System.IO.Path]::GetFullPath($WixRoot)
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

if (-not $MsBuildPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw 'Visual Studio Build Tools were not found. Install the MSBuild component or pass -MsBuildPath.'
    }

    $MsBuildPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
        Select-Object -First 1
}

if (-not $MsBuildPath -or -not (Test-Path -LiteralPath $MsBuildPath -PathType Leaf)) {
    throw "MSBuild was not found: $MsBuildPath"
}

$requiredWixFiles = @(
    'wix.targets',
    'WixTasks.dll',
    'candle.exe',
    'light.exe',
    'WixNetFxExtension.dll',
    'WixUIExtension.dll',
    'WixUtilExtension.dll'
)
foreach ($file in $requiredWixFiles) {
    $path = Join-Path $WixRoot $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "WiX 3.14.1 file is missing: $path"
    }
}

$restoreArguments = @('restore', (Join-Path $repoRoot 'TinyWall\TinyWall.csproj'))
foreach ($source in $PackageSource) {
    $restoreArguments += @('--source', $source)
}
Invoke-NativeCommand -FilePath 'dotnet' -ArgumentList $restoreArguments

$applicationProject = Join-Path $repoRoot 'TinyWall\TinyWall.csproj'
Invoke-NativeCommand -FilePath $MsBuildPath -ArgumentList @(
    $applicationProject,
    '/t:Rebuild',
    "/p:Configuration=$Configuration",
    '/p:RestorePackages=false',
    '/v:minimal'
)

$application = Join-Path $repoRoot "TinyWall\bin\$Configuration\SecureWall.exe"
if (-not (Test-Path -LiteralPath $application -PathType Leaf)) {
    throw "Application build did not produce $application"
}
if ((Get-Item -LiteralPath $application).VersionInfo.ProductName -ne 'SecureWall') {
    throw 'Built executable ProductName is not SecureWall.'
}

& (Join-Path $repoRoot 'MsiSetup\PrepareSources.ps1') -Configuration $Configuration

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
foreach ($name in @('SecureWall_x86.msi', 'SecureWall_x64.msi', 'SecureWall_arm64.msi', 'SHA256SUMS.txt')) {
    $stalePath = Join-Path $OutputDirectory $name
    if (Test-Path -LiteralPath $stalePath) {
        Remove-Item -LiteralPath $stalePath -Force
    }
}

$installerProject = Join-Path $repoRoot 'MsiSetup\MsiSetup.wixproj'
foreach ($platform in $Platforms) {
    $installerArguments = @(
        $installerProject,
        '/t:Rebuild',
        "/p:Configuration=$Configuration",
        "/p:Platform=$platform",
        "/p:WixTargetsPath=$(Join-Path $WixRoot 'wix.targets')",
        "/p:WixInstallPath=$WixRoot",
        "/p:WixToolPath=$WixRoot",
        "/p:WixExtDir=$WixRoot",
        "/p:WixTasksPath=$(Join-Path $WixRoot 'WixTasks.dll')",
        '/p:DefineSolutionProperties=false',
        '/v:minimal'
    )
    if ($SuppressValidation) {
        $installerArguments += '/p:SuppressValidation=true'
    }
    Invoke-NativeCommand -FilePath $MsBuildPath -ArgumentList $installerArguments

    $name = "SecureWall_$platform.msi"
    $source = Join-Path $repoRoot "MsiSetup\bin\$Configuration\$name"
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Installer build did not produce $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $OutputDirectory $name) -Force
}

$manifestPath = Join-Path $OutputDirectory 'SHA256SUMS.txt'
$manifestLines = foreach ($platform in $Platforms) {
    $name = "SecureWall_$platform.msi"
    $hash = (Get-FileHash -LiteralPath (Join-Path $OutputDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name"
}
[System.IO.File]::WriteAllLines($manifestPath, $manifestLines, [System.Text.Encoding]::ASCII)

Write-Output "SecureWall release artifacts: $OutputDirectory"
Get-ChildItem -LiteralPath $OutputDirectory -File | Sort-Object Name | Select-Object Name, Length
