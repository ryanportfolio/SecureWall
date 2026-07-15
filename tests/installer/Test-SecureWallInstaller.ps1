[CmdletBinding()]
param(
    [string]$ArtifactsDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:Failures = New-Object System.Collections.Generic.List[string]
$script:PassCount = 0

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if ($Condition) {
        $script:PassCount++
        Write-Host "PASS $Message"
    }
    else {
        $script:Failures.Add($Message)
        Write-Host "FAIL $Message"
    }
}

function Read-RepoFile {
    param([string]$RelativePath)

    $path = Join-Path $repoRoot $RelativePath
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "exists: $RelativePath"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return ''
    }

    return [System.IO.File]::ReadAllText($path)
}

function Get-MsiProperty {
    param(
        [__ComObject]$Installer,
        [string]$MsiPath,
        [string]$PropertyName
    )

    $database = $Installer.OpenDatabase($MsiPath, 0)
    $view = $database.OpenView("SELECT `Value` FROM `Property` WHERE `Property`='$PropertyName'")
    $null = $view.Execute()
    $record = $view.Fetch()
    if ($null -eq $record) {
        return $null
    }

    $value = $record.StringData(1)
    return [string]$value
}

$project = Read-RepoFile 'TinyWall\TinyWall.csproj'
Assert-True ($project -match '<AssemblyName>SecureWall</AssemblyName>') 'application assembly is SecureWall.exe'
Assert-True ($project -match '<Product>SecureWall</Product>') 'application product metadata is SecureWall'
Assert-True ($project -match '<AssemblyTitle>SecureWall</AssemblyTitle>') 'application title is SecureWall'
Assert-True ($project -match '<Version>0\.1\.1</Version>') 'application version is 0.1.1'

$product = Read-RepoFile 'MsiSetup\Product.wxs'
Assert-True ($product -match '<\?define ProductName="SecureWall" \?>') 'MSI product name is SecureWall'
Assert-True ($product -match 'InstallScope="perMachine"') 'MSI installs per machine'
Assert-True ($product -match 'Name="SecureWall"') 'MSI installs under SecureWall directories'
Assert-True ($product -match 'SecureWall\.exe') 'MSI carries SecureWall.exe'
Assert-True ($product -match 'Software\\SecureWall') 'MSI writes SecureWall registry identity'
Assert-True ($product -match 'TINYWALLINSTALLDIR32') 'MSI detects 32-bit TinyWall installation'
Assert-True ($product -match 'TINYWALLINSTALLDIR64') 'MSI detects 64-bit TinyWall installation'
Assert-True ($product -match "ExeCommand='/install'") 'MSI invokes guarded application install lifecycle'
Assert-True ($product -match "ExeCommand='/uninstall'") 'MSI invokes application uninstall lifecycle'
Assert-True ($product -match "Execute='rollback'") 'MSI has rollback cleanup action'

$installerProject = Read-RepoFile 'MsiSetup\MsiSetup.wixproj'
Assert-True ($installerProject -match '<OutputName>SecureWall</OutputName>') 'MSI filenames use SecureWall'

$prepareSources = Read-RepoFile 'MsiSetup\PrepareSources.ps1'
Assert-True ($prepareSources -match "'SecureWall\.exe'") 'staging requires SecureWall.exe'
Assert-True ($prepareSources -match 'ProgramFiles\\SecureWall') 'staging targets SecureWall payload directory'

$productConstants = Read-RepoFile 'TinyWall\SecureWallProduct.cs'
Assert-True ($productConstants -match 'internal const string Name = "SecureWall"') 'runtime product name is SecureWall'
Assert-True ($productConstants -match 'ControllerPipeName = "SecureWallController"') 'named pipe identity is SecureWall'
Assert-True ($productConstants -match 'ServiceMutexName = @"Global\\SecureWallService"') 'service mutex identity is SecureWall'

$service = Read-RepoFile 'TinyWall\TinyWallService.cs'
Assert-True ($service -match 'SERVICE_NAME = "SecureWall"') 'Windows service identity is SecureWall'
Assert-True ($service -match 'TinyWall') 'TinyWall conflict and upstream compatibility code remains present'

$activeExtensions = @('.cs', '.resx', '.csproj', '.wxs', '.wixproj', '.ps1', '.md', '.txt', '.html', '.config', '.manifest')
$oldBrandFiles = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'TinyWall'), (Join-Path $repoRoot 'MsiSetup'), (Join-Path $repoRoot 'tools'), (Join-Path $repoRoot 'tests') -Recurse -File |
    Where-Object { $_.FullName -ne $PSCommandPath } |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Where-Object { $activeExtensions -contains $_.Extension } |
    Where-Object { [System.IO.File]::ReadAllText($_.FullName) -match 'PromptWall' }
Assert-True (($oldBrandFiles | Measure-Object).Count -eq 0) 'active source contains no PromptWall identities'

$builder = Read-RepoFile 'tools\release\Build-SecureWallRelease.ps1'
Assert-True ($builder -match 'ValidateSet\(''x86'', ''x64'', ''arm64''\)') 'release builder validates supported architectures'
Assert-True ($builder -match 'SHA256SUMS\.txt') 'release builder emits SHA-256 manifest'
Assert-True ($builder -match '\[switch\]\$SuppressValidation') 'release builder exposes explicit sandbox ICE bypass'
Assert-True ($builder -notmatch '(?im)^\s*(Start-Process\s+msiexec|&\s*msiexec|msiexec\.exe)') 'release builder never installs generated MSI files'

$releaseWorkflow = Read-RepoFile '.github\workflows\release.yml'
Assert-True ($releaseWorkflow -match 'contents:\s*write') 'release workflow grants upload job write access'
Assert-True ($releaseWorkflow -match 'Build-SecureWallRelease\.ps1') 'release workflow uses repository release builder'
Assert-True ($releaseWorkflow -match 'SHA256SUMS\.txt') 'release workflow publishes hash manifest'
Assert-True ($releaseWorkflow -notmatch 'SuppressValidation') 'release workflow never bypasses MSI ICE validation'

if ($ArtifactsDirectory) {
    $artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $ArtifactsDirectory))
    $expectedMsi = @(
        'SecureWall_x86.msi',
        'SecureWall_x64.msi',
        'SecureWall_arm64.msi'
    )

    foreach ($name in $expectedMsi) {
        Assert-True (Test-Path -LiteralPath (Join-Path $artifactRoot $name) -PathType Leaf) "artifact exists: $name"
    }

    $hashManifest = Join-Path $artifactRoot 'SHA256SUMS.txt'
    Assert-True (Test-Path -LiteralPath $hashManifest -PathType Leaf) 'artifact exists: SHA256SUMS.txt'

    if ((Test-Path -LiteralPath $hashManifest -PathType Leaf) -and
        (($expectedMsi | Where-Object { -not (Test-Path -LiteralPath (Join-Path $artifactRoot $_) -PathType Leaf) }).Count -eq 0)) {
        $manifestText = [System.IO.File]::ReadAllText($hashManifest)
        foreach ($name in $expectedMsi) {
            $hash = (Get-FileHash -LiteralPath (Join-Path $artifactRoot $name) -Algorithm SHA256).Hash.ToLowerInvariant()
            Assert-True ($manifestText -match ([regex]::Escape("$hash  $name"))) "hash manifest matches $name"
        }

        $installer = New-Object -ComObject WindowsInstaller.Installer
        foreach ($name in $expectedMsi) {
            $msiPath = Join-Path $artifactRoot $name
            Assert-True ((Get-MsiProperty $installer $msiPath 'ProductName') -eq 'SecureWall') "$name ProductName is SecureWall"
            Assert-True ((Get-MsiProperty $installer $msiPath 'ProductVersion') -eq '0.1.1.0') "$name ProductVersion is 0.1.1.0"
            Assert-True ((Get-MsiProperty $installer $msiPath 'ALLUSERS') -eq '1') "$name is per-machine"
        }
    }
}

if ($script:Failures.Count -gt 0) {
    Write-Output ""
    Write-Output "$($script:Failures.Count) failed, $script:PassCount passed"
    foreach ($failure in $script:Failures) {
        Write-Output " - $failure"
    }
    exit 1
}

Write-Output ""
Write-Output "$script:PassCount passed, 0 failed"
