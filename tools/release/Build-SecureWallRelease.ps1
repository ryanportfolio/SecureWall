[CmdletBinding()]
param(
    # UpgradeFaultTest builds the in-place upgrade VM test packages into
    # artifacts\upgrade-fault-test. They are never attached to a release.
    [ValidateSet('Debug', 'Release', 'UpgradeFaultTest')]
    [string]$Configuration = 'Release',

    [ValidateSet('x86', 'x64', 'arm64')]
    [string[]]$Platforms = @('x86', 'x64', 'arm64'),

    [string]$WixRoot,
    [string]$MsBuildPath,
    [string]$OutputDirectory,
    [string[]]$PackageSource,
    [switch]$SuppressValidation,

    # CI builds the packages as a later version so ICE validates the
    # Upgrade row that builds at or below 0.5.0 leave out. Those packages go
    # to artifacts\version-check and are never attached to a release. The
    # shared outputs in TinyWall\bin, MsiSetup\Sources and MsiSetup\bin keep
    # this version until the next build without it; the bundle builders in
    # tools\manual and tools\vm refuse such an executable.
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+\z')]
    [string]$ValidationVersion
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
    $OutputDirectory = if ($ValidationVersion) {
        Join-Path $repoRoot 'artifacts\version-check'
    }
    elseif ($Configuration -eq 'UpgradeFaultTest') {
        Join-Path $repoRoot 'artifacts\upgrade-fault-test'
    }
    else {
        Join-Path $repoRoot 'artifacts\release'
    }
}

$WixRoot = [System.IO.Path]::GetFullPath($WixRoot)
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if ($ValidationVersion -and
    $OutputDirectory.TrimEnd('\') -eq [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\release')).TrimEnd('\')) {
    throw '-ValidationVersion packages must not be written to artifacts\release.'
}
# MsiSetup.wixproj leaves out the Upgrade row at or below 0.5.0, so such a
# version would validate nothing this switch exists for.
if ($ValidationVersion -and [version]$ValidationVersion -le [version]'0.5.0') {
    throw '-ValidationVersion must be above 0.5.0; builds at or below it have no upgradeable row to validate.'
}
if ($ValidationVersion -and $SuppressValidation) {
    throw '-ValidationVersion exists for ICE validation; do not combine it with -SuppressValidation.'
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
$applicationArguments = @(
    $applicationProject,
    '/t:Rebuild',
    "/p:Configuration=$Configuration",
    '/p:RestorePackages=false',
    '/v:minimal'
)
if ($ValidationVersion) {
    $applicationArguments += "/p:Version=$ValidationVersion"
}
Invoke-NativeCommand -FilePath $MsBuildPath -ArgumentList $applicationArguments

$application = Join-Path $repoRoot "TinyWall\bin\$Configuration\SecureWall.exe"
if (-not (Test-Path -LiteralPath $application -PathType Leaf)) {
    throw "Application build did not produce $application"
}
# The MSI takes its ProductVersion from this file version.
if ($ValidationVersion -and (Get-Item -LiteralPath $application).VersionInfo.FileVersion -ne "$ValidationVersion.0") {
    throw "Built executable file version is not $ValidationVersion.0."
}
if ((Get-Item -LiteralPath $application).VersionInfo.ProductName -ne 'SecureWall') {
    throw 'Built executable ProductName is not SecureWall.'
}
# The UpgradeFaultTest configuration marks its informational version. Its fault
# points must never reach a Debug or Release package.
$isFaultBuild = (Get-Item -LiteralPath $application).VersionInfo.ProductVersion -match 'upgrade-fault-test'
if ($isFaultBuild -ne ($Configuration -eq 'UpgradeFaultTest')) {
    throw "Built executable fault-test marker does not match configuration $Configuration."
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
    if ($ValidationVersion) {
        # A global property overrides the version MsiSetup.wixproj reads from TinyWall.csproj.
        $installerArguments += "/p:SecureWallVersion=$ValidationVersion"
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
