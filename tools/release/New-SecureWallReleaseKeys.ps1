#Requires -Version 7.2
<#
.SYNOPSIS
Creates SecureWall's release signing keys on the owner's PC and writes their public halves
to tools\release\keys.

.DESCRIPTION
Run once, by the owner, in PowerShell 7 on the PC that will sign releases. It creates:

- The primary key, in your Windows user key store (Microsoft Software Key Storage
  Provider). It cannot be exported, and Windows asks for the password you choose here every
  time it signs.
- The backup key, written to -BackupDirectory as a password-encrypted PKCS#8 file. Put that
  folder on removable media and keep it offline and away from this PC. It carries the same
  authority as the primary key.
- The updater test key (unless -SkipTestKey), written unencrypted to -TestKeyDirectory. Only
  the UpdaterTest build trusts it; it signs releases for the VM test harness and nothing else.

Then it writes primary.pub, backup.pub and test.pub to tools\release\keys. Commit those three
files. SecureWall.exe embeds primary and backup; the core tests check that the embedded keys
equal these files.

It never overwrites a key or a public key file that already exists.
#>
[CmdletBinding()]
param(
    # A folder on removable media, for the encrypted backup key.
    [Parameter(Mandatory)]
    [string]$BackupDirectory,

    [string]$TestKeyDirectory = (Join-Path $env:USERPROFILE 'SecureWall-updater-test-key'),

    [switch]$SkipTestKey
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$keysDirectory = Join-Path $PSScriptRoot 'keys'

# Shared with Sign-SecureWallRelease.ps1.
$primaryKeyName = 'SecureWall Release Signing (primary)'
$backupFileName = 'securewall-release-backup.key.pem'
$testFileName = 'securewall-updater-test.key.pem'

function Write-PublicKeyFile {
    param([string]$Name, [byte[]]$X, [byte[]]$Y)
    if ($X.Length -ne 32 -or $Y.Length -ne 32) { throw "The $Name public key is not a P-256 point." }
    $point = [byte[]]::new(64)
    [Array]::Copy($X, 0, $point, 0, 32)
    [Array]::Copy($Y, 0, $point, 32, 32)
    $path = Join-Path $keysDirectory "$Name.pub"
    $text = "# SecureWall release signing public key: $Name`n" +
        "# ECDSA P-256. Base64 of the 64-byte public point, X then Y.`n" +
        [Convert]::ToBase64String($point) + "`n"
    [System.IO.File]::WriteAllText($path, $text, [System.Text.Encoding]::ASCII)
    Write-Host "Wrote $path"
}

function Read-NewPassword {
    param([string]$Purpose)
    while ($true) {
        $first = Read-Host -AsSecureString "Password for the $Purpose (at least 16 characters)"
        $second = Read-Host -AsSecureString 'Type it again'
        $a = [System.Net.NetworkCredential]::new('', $first).Password
        $b = [System.Net.NetworkCredential]::new('', $second).Password
        if ($a -ne $b) { Write-Warning 'The passwords differ. Try again.'; continue }
        if ($a.Length -lt 16) { Write-Warning 'Use at least 16 characters.'; continue }
        return $a
    }
}

if (-not $IsWindows) { throw 'Run this on Windows: the primary key lives in the Windows key store.' }

$BackupDirectory = [System.IO.Path]::GetFullPath($BackupDirectory)
if (-not (Test-Path -LiteralPath $BackupDirectory -PathType Container)) {
    throw "Backup folder not found: $BackupDirectory"
}
if ($BackupDirectory.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The backup key must not be written inside the repository.'
}
$backupDrive = [System.IO.DriveInfo]::new([System.IO.Path]::GetPathRoot($BackupDirectory))
if ($backupDrive.DriveType -ne [System.IO.DriveType]::Removable) {
    Write-Warning "$BackupDirectory is not on removable media. Move the backup key to offline storage after this script finishes."
}

$backupPath = Join-Path $BackupDirectory $backupFileName
$testPath = Join-Path $TestKeyDirectory $testFileName
New-Item -ItemType Directory -Force -Path $keysDirectory | Out-Null
foreach ($existing in @(
    (Join-Path $keysDirectory 'primary.pub'),
    (Join-Path $keysDirectory 'backup.pub'),
    $backupPath)) {
    if (Test-Path -LiteralPath $existing) { throw "Refusing to overwrite $existing." }
}
if (-not $SkipTestKey) {
    foreach ($existing in @((Join-Path $keysDirectory 'test.pub'), $testPath)) {
        if (Test-Path -LiteralPath $existing) { throw "Refusing to overwrite $existing." }
    }
}
if ([System.Security.Cryptography.CngKey]::Exists($primaryKeyName)) {
    throw "A key named '$primaryKeyName' already exists in your key store. Rotating keys is a manual step (.claude/reference/deployment.md)."
}

# Primary: non-exportable, strong protection. Windows shows its own dialog to set the
# password now and asks for it on every signature.
Write-Host 'Creating the primary key. Windows asks you to choose its password.'
$parameters = [System.Security.Cryptography.CngKeyCreationParameters]::new()
$parameters.Provider = [System.Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
$parameters.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::None
$parameters.KeyUsage = [System.Security.Cryptography.CngKeyUsages]::Signing
$parameters.UIPolicy = [System.Security.Cryptography.CngUIPolicy]::new(
    [System.Security.Cryptography.CngUIProtectionLevels]::ForceHighProtection,
    'SecureWall release signing',
    'Signs SecureWall release manifests (SHA256SUMS.txt.sig).')
$primary = [System.Security.Cryptography.CngKey]::Create(
    [System.Security.Cryptography.CngAlgorithm]::ECDsaP256, $primaryKeyName, $parameters)
try {
    # BCRYPT_ECCKEY_BLOB: 4-byte magic, 4-byte length, then X and Y.
    $blob = $primary.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    if ($blob.Length -ne 72 -or [BitConverter]::ToInt32($blob, 4) -ne 32) { throw 'Unexpected primary public key format.' }
    Write-PublicKeyFile 'primary' $blob[8..39] $blob[40..71]
}
finally {
    $primary.Dispose()
}

# Backup: exportable by necessity, so it is only ever stored encrypted, offline.
$password = Read-NewPassword 'backup key file'
$backup = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try {
    $pbe = [System.Security.Cryptography.PbeParameters]::new(
        [System.Security.Cryptography.PbeEncryptionAlgorithm]::Aes256Cbc,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256, 600000)
    $pem = $backup.ExportEncryptedPkcs8PrivateKeyPem($password.ToCharArray(), $pbe)
    [System.IO.File]::WriteAllText($backupPath, $pem + "`n", [System.Text.Encoding]::ASCII)
    Write-Host "Wrote $backupPath"
    $q = $backup.ExportParameters($false).Q
    Write-PublicKeyFile 'backup' $q.X $q.Y
}
finally {
    $backup.Dispose()
    $password = $null
}

if (-not $SkipTestKey) {
    New-Item -ItemType Directory -Force -Path $TestKeyDirectory | Out-Null
    $test = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try {
        [System.IO.File]::WriteAllText($testPath, $test.ExportPkcs8PrivateKeyPem() + "`n", [System.Text.Encoding]::ASCII)
        Write-Host "Wrote $testPath (VM tests only)"
        $q = $test.ExportParameters($false).Q
        Write-PublicKeyFile 'test' $q.X $q.Y
    }
    finally {
        $test.Dispose()
    }
}

Write-Host ''
Write-Host 'Done. Commit the .pub files in tools\release\keys, then move the backup key offline.'
