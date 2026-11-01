#Requires -Version 7.4
<#
.SYNOPSIS
Signs a published SecureWall release's SHA256SUMS.txt so Check for updates offers it.

.DESCRIPTION
Run by the owner, in PowerShell 7 on the PC that holds the release keys, after release.yml
has attached the installers and SHA256SUMS.txt to the release:

    pwsh -File tools\release\Sign-SecureWallRelease.ps1 -Tag v0.6.0 -Commit <reviewed commit SHA>

It refuses unless:
- the tag points at the reviewed commit and the release is published (not a draft);
- the manifest's header names this repository, the tag, the version TinyWall.csproj has at
  that commit, the release's prerelease flag and the commit;
- every installer's SHA-256 matches its manifest line;
- the SecureWall.exe inside each installer embeds the public keys in
  tools\release\keys\primary.pub and backup.pub and not the updater test key (the key that
  signs is also checked against its .pub file).
It checks that those keys are present, not that no other key is.

Then it signs the exact manifest bytes (Windows asks for the primary key's password) and
uploads SHA256SUMS.txt.sig. Until that file exists the updater skips the release. If
release.yml runs again after signing, the installers and hashes change and the old signature
no longer matches; delete SHA256SUMS.txt.sig from the release and sign again.

It parses the manifest with TinyWall\Prompting\SignedReleaseManifest.cs, the same code the
updater uses, and needs gh signed in with access to the repository. Extracting the installers
uses msiexec /a (an administrative image in a temporary folder); nothing is installed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+$')]
    [string]$Tag,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$Commit,

    # Sign with the offline backup key (password-encrypted PKCS#8 file) instead of the primary.
    [string]$BackupKeyFile
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$repository = 'ryanportfolio/SecureWall'
$primaryKeyName = 'SecureWall Release Signing (primary)'
$platforms = @('x86', 'x64', 'arm64')

function Fail([string]$Message) { throw "Not signed: $Message" }

function Read-KeyLine([string]$Name) {
    $path = Join-Path $PSScriptRoot "keys\$Name.pub"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Fail "$path is missing." }
    $key = [pylorak.TinyWall.Prompting.SignedReleaseManifest]::ReadPublicKeyFile([System.IO.File]::ReadAllText($path))
    if (-not $key) { Fail "$path does not hold one key line." }
    return $key
}

# C# string constants sit in the assembly's user-string heap as UTF-16, at an even or odd
# offset, so the file is read as UTF-16 from both.
function Test-ContainsUtf16([byte[]]$Haystack, [string]$Text) {
    $even = [System.Text.Encoding]::Unicode.GetString($Haystack)
    $odd = [System.Text.Encoding]::Unicode.GetString($Haystack, 1, $Haystack.Length - 1)
    return $even.Contains($Text, [StringComparison]::Ordinal) -or $odd.Contains($Text, [StringComparison]::Ordinal)
}

if (-not $IsWindows) { Fail 'run this on Windows.' }

# The updater's own parser and verifier. Its types are internal to SecureWall, so they are
# compiled here as public.
$parserSource = [System.IO.File]::ReadAllText((Join-Path $repoRoot 'TinyWall\Prompting\SignedReleaseManifest.cs'))
$parserSource = [regex]::Replace($parserSource, '\binternal\b', 'public')
Add-Type -TypeDefinition $parserSource -Language CSharp

$primaryKey = Read-KeyLine 'primary'
$backupKey = Read-KeyLine 'backup'
$testKeyPath = Join-Path $PSScriptRoot 'keys\test.pub'
$testKey = if (Test-Path -LiteralPath $testKeyPath -PathType Leaf) {
    [pylorak.TinyWall.Prompting.SignedReleaseManifest]::ReadPublicKeyFile([System.IO.File]::ReadAllText($testKeyPath))
} else { $null }

# 1. The tag names the reviewed commit, and the release is published.
$ref = gh api "repos/$repository/git/ref/tags/$Tag" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { Fail "tag $Tag was not found." }
$tagged = $ref.object.sha
if ($ref.object.type -eq 'tag') {
    $tagged = (gh api "repos/$repository/git/tags/$tagged" | ConvertFrom-Json).object.sha
}
if ($tagged -ne $Commit) { Fail "$Tag points at $tagged, not at $Commit." }
$release = gh release view $Tag --repo $repository --json isDraft,isPrerelease,assets | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { Fail "release $Tag was not found." }
if ($release.isDraft) { Fail "release $Tag is a draft." }
if ($release.assets.name -contains 'SHA256SUMS.txt.sig') {
    Fail "release $Tag already has SHA256SUMS.txt.sig. Delete it first to sign again."
}

# 2. The version TinyWall.csproj has at that commit.
$project = gh api "repos/$repository/contents/TinyWall/TinyWall.csproj?ref=$Commit" | ConvertFrom-Json
$projectText = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($project.content))
$versionMatch = [regex]::Match($projectText, '<Version>([0-9]+\.[0-9]+\.[0-9]+)</Version>')
if (-not $versionMatch.Success) { Fail "TinyWall.csproj at $Commit has no three-field version." }
if ("v$($versionMatch.Groups[1].Value)" -ne $Tag) { Fail "TinyWall.csproj at $Commit is version $($versionMatch.Groups[1].Value), not $Tag." }

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("securewall-sign-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    # 3. The manifest and installers as GitHub serves them.
    gh release download $Tag --repo $repository --dir $work --pattern 'SHA256SUMS.txt' --pattern 'SecureWall_*.msi'
    if ($LASTEXITCODE -ne 0) { Fail 'the release assets could not be downloaded.' }
    $manifestBytes = [System.IO.File]::ReadAllBytes((Join-Path $work 'SHA256SUMS.txt'))
    $manifest = $null
    $parseError = ''
    if (-not [pylorak.TinyWall.Prompting.SignedReleaseManifest]::TryParse($manifestBytes, [ref]$manifest, [ref]$parseError)) {
        Fail "SHA256SUMS.txt is not a release manifest: $parseError"
    }
    if ($manifest.Tag -ne $Tag) { Fail "the manifest names $($manifest.Tag)." }
    if ($manifest.Commit -ne $Commit) { Fail "the manifest was built from $($manifest.Commit)." }
    if ($manifest.Prerelease -ne [bool]$release.isPrerelease) {
        Fail "the manifest says Prerelease: $($manifest.Prerelease.ToString().ToLowerInvariant()), but the GitHub release says $($release.isPrerelease)."
    }

    foreach ($platform in $platforms) {
        $name = "SecureWall_$platform.msi"
        $expected = $manifest.HashFor($platform)
        if (-not $expected) { Fail "the manifest has no line for $name." }
        $msi = Join-Path $work $name
        $actual = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) { Fail "$name does not match its manifest line." }

        # 4. The keys the shipped SecureWall.exe trusts.
        $image = Join-Path $work "admin-$platform"
        $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') `
            -ArgumentList @('/a', "`"$msi`"", '/qn', "TARGETDIR=`"$image`"") -Wait -PassThru
        if ($process.ExitCode -ne 0) { Fail "$name could not be extracted (msiexec exit code $($process.ExitCode))." }
        $exe = Get-ChildItem -LiteralPath $image -Recurse -File -Filter 'SecureWall.exe' | Select-Object -First 1
        if (-not $exe) { Fail "$name contains no SecureWall.exe." }
        $exeBytes = [System.IO.File]::ReadAllBytes($exe.FullName)
        if (-not (Test-ContainsUtf16 $exeBytes $primaryKey) -or -not (Test-ContainsUtf16 $exeBytes $backupKey)) {
            Fail "SecureWall.exe in $name does not embed the keys in tools\release\keys."
        }
        if ($testKey -and (Test-ContainsUtf16 $exeBytes $testKey)) {
            Fail "SecureWall.exe in $name embeds the updater test key."
        }
    }

    # 5. Sign the exact bytes GitHub serves.
    if ($BackupKeyFile) {
        $password = Read-Host -AsSecureString 'Backup key password'
        $signer = [System.Security.Cryptography.ECDsa]::Create()
        $signer.ImportFromEncryptedPem([System.IO.File]::ReadAllText($BackupKeyFile),
            [System.Net.NetworkCredential]::new('', $password).Password.ToCharArray())
        $expectedKey = $backupKey
    }
    else {
        $cng = [System.Security.Cryptography.CngKey]::Open($primaryKeyName)
        $signer = [System.Security.Cryptography.ECDsaCng]::new($cng)
        $expectedKey = $primaryKey
    }
    try {
        $q = $signer.ExportParameters($false).Q
        if ([Convert]::ToBase64String([byte[]]($q.X + $q.Y)) -ne $expectedKey) {
            Fail 'the signing key does not match its public key file in tools\release\keys.'
        }
        Write-Host 'Signing. Windows may ask for the key password.'
        $signature = $signer.SignData($manifestBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    }
    finally {
        $signer.Dispose()
    }
    $signatureText = [Convert]::ToBase64String($signature) + "`n"
    $signatureBytes = [System.Text.Encoding]::ASCII.GetBytes($signatureText)
    if (-not [pylorak.TinyWall.Prompting.SignedReleaseManifest]::VerifySignature($manifestBytes, $signatureBytes, [string[]]@($primaryKey, $backupKey))) {
        Fail 'the new signature does not verify.'
    }
    $signaturePath = Join-Path $work 'SHA256SUMS.txt.sig'
    [System.IO.File]::WriteAllBytes($signaturePath, $signatureBytes)

    # 6. Publish it.
    gh release upload $Tag $signaturePath --repo $repository
    if ($LASTEXITCODE -ne 0) { Fail 'SHA256SUMS.txt.sig could not be uploaded.' }
    Write-Host "Signed $Tag. Check for updates now offers it."
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
