[CmdletBinding()]
param(
    [ValidateSet('x86', 'x64', 'arm64')]
    [string[]]$Platforms = @('x86', 'x64', 'arm64'),

    [string]$WixRoot,
    [string]$MsBuildPath,
    [string]$OutputDirectory,
    [string[]]$PackageSource,
    [switch]$SuppressValidation
)

# Builds the in-place upgrade fault-test packages (docs/HARDENING-VALIDATION.md,
# "In-place upgrade") into artifacts\upgrade-fault-test\<version>, so the old and
# the new version's fault builds from two checkouts sit side by side. They carry fault points
# and visible stamps, are for testing only and are never released. The
# release builder refuses them and the installer contract test fails on them.

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$configuration = 'UpgradeFaultTest'

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
$WixRoot = [System.IO.Path]::GetFullPath($WixRoot)
if ($OutputDirectory -and [System.IO.Path]::GetFullPath($OutputDirectory) -match '\\artifacts\\release(\\|$)') {
    throw 'Fault-test packages must not be written to artifacts\release.'
}

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

$restoreArguments = @('restore', (Join-Path $repoRoot 'TinyWall\TinyWall.csproj'))
foreach ($source in $PackageSource) {
    $restoreArguments += @('--source', $source)
}
Invoke-NativeCommand -FilePath 'dotnet' -ArgumentList $restoreArguments

Invoke-NativeCommand -FilePath $MsBuildPath -ArgumentList @(
    (Join-Path $repoRoot 'TinyWall\TinyWall.csproj'),
    '/t:Rebuild',
    "/p:Configuration=$configuration",
    '/p:RestorePackages=false',
    '/v:minimal'
)

$application = Join-Path $repoRoot "TinyWall\bin\$configuration\SecureWall.exe"
if ((Get-Item -LiteralPath $application).VersionInfo.ProductVersion -notmatch 'upgrade-fault-test') {
    throw "Fault-test build is not stamped: $application"
}
if (-not $OutputDirectory) {
    $fileVersion = [Version](Get-Item -LiteralPath $application).VersionInfo.FileVersion
    $OutputDirectory = Join-Path $repoRoot ('artifacts\upgrade-fault-test\{0}.{1}.{2}' -f $fileVersion.Major, $fileVersion.Minor, $fileVersion.Build)
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

& (Join-Path $repoRoot 'MsiSetup\PrepareSources.ps1') -Configuration $configuration

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$installerProject = Join-Path $repoRoot 'MsiSetup\MsiSetup.wixproj'
foreach ($platform in $Platforms) {
    $installerArguments = @(
        $installerProject,
        '/t:Rebuild',
        "/p:Configuration=$configuration",
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
    Copy-Item -LiteralPath (Join-Path $repoRoot "MsiSetup\bin\$configuration\$name") -Destination (Join-Path $OutputDirectory $name) -Force
}

# The staged payload now holds the fault-test executable. Rebuild the release
# staging before any release packaging; Build-SecureWallRelease.ps1 does so itself.
Write-Output "SecureWall upgrade fault-test packages: $OutputDirectory"
Get-ChildItem -LiteralPath $OutputDirectory -File | Sort-Object Name | Select-Object Name, Length
