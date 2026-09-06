[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExpectedComputerName,

    [Parameter(Mandatory = $true)]
    [string]$SnapshotReference,

    [Parameter(Mandatory = $true)]
    [string]$TargetAddress,

    [ValidateRange(1, 65535)]
    [int]$TargetPort = 443,

    [ValidateSet('x86', 'x64', 'arm64')]
    [string]$Architecture = 'x64',

    [switch]$ConfirmIsolatedVm,
    [switch]$KeepInstalled
)

$ErrorActionPreference = 'Stop'
$providerGuid = '053FC8F9-9052-4B2F-9B24-7DE3A2BED6E0'
$auditSubcategory = '{0CCE9226-69AE-11D9-BED3-505054503030}'
$started = Get-Date
$resultRoot = Join-Path $PSScriptRoot ('results\' + $started.ToString('yyyyMMdd-HHmmss'))
$app = $null
$msi = Join-Path $PSScriptRoot "installers\SecureWall_$Architecture.msi"
$allowProbe = Join-Path $PSScriptRoot 'probes\SecureWall.AllowProbe.exe'
$ignoreProbe = Join-Path $PSScriptRoot 'probes\SecureWall.IgnoreProbe.exe'
$controller = $null
$installedByScript = $false
$testFailure = $null
$cleanupFailures = @()

function Write-EvidenceText {
    param([string]$Name, [object[]]$Value)
    $Value | Out-File -LiteralPath (Join-Path $resultRoot $Name) -Encoding utf8 -Width 4096
}

function Save-WfpState {
    param([string]$Name)
    $path = Join-Path $resultRoot $Name
    & "$env:SystemRoot\System32\netsh.exe" wfp show state "file=$path" | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Could not capture WFP state to '$path'."
    }
}

function Invoke-Probe {
    param([string]$Path)
    $process = Start-Process -FilePath $Path -ArgumentList @($TargetAddress, $TargetPort, 15000) -WindowStyle Hidden -Wait -PassThru
    return $process.ExitCode
}

if (-not $ConfirmIsolatedVm) {
    throw 'Pass -ConfirmIsolatedVm only after confirming snapshot and local console recovery.'
}
if (-not [string]::Equals($ExpectedComputerName, $env:COMPUTERNAME, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Computer-name guard failed. Expected '$ExpectedComputerName'; actual '$env:COMPUTERNAME'."
}
if ([string]::IsNullOrWhiteSpace($SnapshotReference)) {
    throw 'SnapshotReference cannot be empty.'
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell console inside the VM.'
}

$computer = Get-CimInstance Win32_ComputerSystem
$vmDescription = "$($computer.Manufacturer) $($computer.Model)"
if ($vmDescription -notmatch 'Virtual|VMware|VirtualBox|KVM|QEMU|Xen|HVM|Hyper-V|Parallels') {
    throw "VM guard failed for manufacturer/model: $vmDescription"
}

$parsedAddress = $null
if (-not [Net.IPAddress]::TryParse($TargetAddress, [ref]$parsedAddress) -or
    [Net.IPAddress]::IsLoopback($parsedAddress)) {
    throw 'TargetAddress must be a non-loopback IP address reachable from the VM.'
}

foreach ($path in @($msi, $allowProbe, $ignoreProbe, (Join-Path $PSScriptRoot 'bundle-manifest.json'))) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing validation file: $path"
    }
}
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'bundle-manifest.json') -Raw | ConvertFrom-Json
$msiHash = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash
if ($msiHash -ne $manifest.InstallerSha256.$Architecture) { throw 'MSI hash differs from the bundle manifest.' }
foreach ($probe in @($allowProbe, $ignoreProbe)) {
    if ((Get-FileHash -LiteralPath $probe -Algorithm SHA256).Hash -ne $manifest.ProbeSha256) {
        throw "Probe hash differs from the bundle manifest: $probe"
    }
}
if (Get-Service -Name TinyWall -ErrorAction SilentlyContinue) {
    throw 'TinyWall is installed in this VM. Use a clean snapshot; the script will not layer firewalls.'
}
if (Get-Service -Name SecureWall -ErrorAction SilentlyContinue) {
    throw 'SecureWall is already installed. Revert to the clean snapshot first.'
}

New-Item -ItemType Directory -Force -Path $resultRoot | Out-Null
$preflight = [ordered]@{
    StartedUtc = $started.ToUniversalTime().ToString('O')
    ComputerName = $env:COMPUTERNAME
    SnapshotReference = $SnapshotReference
    VmDescription = $vmDescription
    Target = "${TargetAddress}:$TargetPort"
    InstallerSha256 = $msiHash
    Architecture = $Architecture
}
$preflight | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $resultRoot 'preflight.json') -Encoding UTF8
Write-EvidenceText 'audit-before.csv' @(& "$env:SystemRoot\System32\auditpol.exe" /get "/subcategory:$auditSubcategory" /r)
Save-WfpState 'wfp-before.xml'
$firewallBefore = Get-NetFirewallRule | Select-Object Name, DisplayName, Group, Enabled, Direction, Action
$firewallBefore | Export-Clixml -LiteralPath (Join-Path $resultRoot 'firewall-rules-before.xml')
$profilesBefore = Get-NetFirewallProfile | Select-Object Name, NotifyOnListen
$profilesBefore | Export-Clixml -LiteralPath (Join-Path $resultRoot 'firewall-profiles-before.xml')
$hostsPath = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
$hostsHashBefore = (Get-FileHash -LiteralPath $hostsPath -Algorithm SHA256).Hash

try {
    if ((Invoke-Probe $allowProbe) -ne 0 -or (Invoke-Probe $ignoreProbe) -ne 0) {
        throw 'Baseline probes cannot reach the target before SecureWall installation.'
    }

    $installLog = Join-Path $resultRoot 'msi-install.log'
    $install = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList @('/i', "`"$msi`"", '/qn', '/norestart', '/L*v', "`"$installLog`"") -WindowStyle Hidden -Wait -PassThru
    if ($install.ExitCode -ne 0 -and $install.ExitCode -ne 3010) {
        throw "SecureWall MSI install failed with exit code $($install.ExitCode)."
    }
    $installedByScript = $true

    $registryView = if ($Architecture -eq 'x86') { [Microsoft.Win32.RegistryView]::Registry32 } else { [Microsoft.Win32.RegistryView]::Registry64 }
    $machineKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $registryView)
    try {
        $installKey = $machineKey.OpenSubKey('Software\SecureWall')
        try { $installDirectory = $installKey.GetValue('InstallDir') } finally { if ($installKey) { $installKey.Dispose() } }
    } finally { $machineKey.Dispose() }
    if ([string]::IsNullOrWhiteSpace($installDirectory)) { throw 'MSI did not register its installation directory.' }
    $app = Join-Path $installDirectory 'SecureWall.exe'
    if ((Get-Item -LiteralPath $app).VersionInfo.ProductName -ne 'SecureWall') { throw 'Installed executable identity check failed.' }

    $service = Get-Service -Name SecureWall -ErrorAction Stop
    $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(120))
    Save-WfpState 'wfp-installed.xml'
    $providerPresent = Select-String -LiteralPath (Join-Path $resultRoot 'wfp-installed.xml') -SimpleMatch $providerGuid -Quiet
    if (-not $providerPresent) {
        throw 'SecureWall WFP provider was not found after service startup.'
    }

    $controller = Start-Process -FilePath $app -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 2

    $allowInitial = Invoke-Probe $allowProbe
    if ($allowInitial -eq 0) {
        throw 'Unknown AllowProbe connected before an allow rule existed.'
    }
    $allowConfirmation = Read-Host 'Click Allow outgoing on the AllowProbe popup, then type ALLOWED'
    if ($allowConfirmation -cne 'ALLOWED') {
        throw 'Allow confirmation was not provided.'
    }
    if ((Invoke-Probe $allowProbe) -ne 0) {
        throw 'AllowProbe remained blocked after Allow outgoing.'
    }

    $ignoreInitial = Invoke-Probe $ignoreProbe
    if ($ignoreInitial -eq 0) {
        throw 'Unknown IgnoreProbe connected before an allow rule existed.'
    }
    $ignoreConfirmation = Read-Host 'Click Ignore on the IgnoreProbe popup, then type IGNORED'
    if ($ignoreConfirmation -cne 'IGNORED') {
        throw 'Ignore confirmation was not provided.'
    }
    if ((Invoke-Probe $ignoreProbe) -eq 0) {
        throw 'IgnoreProbe connected after Ignore; policy should remain blocked.'
    }

    $events = Get-WinEvent -FilterHashtable @{ LogName = 'Security'; Id = 5157; StartTime = $started } -ErrorAction SilentlyContinue
    $events | Select-Object TimeCreated, Id, RecordId, ProviderName, Message | Export-Csv -LiteralPath (Join-Path $resultRoot 'security-5157.csv') -NoTypeInformation -Encoding UTF8
    $eventText = ($events.Message -join [Environment]::NewLine)
    if ($eventText -notmatch 'SecureWall\.AllowProbe\.exe' -or
        $eventText -notmatch 'SecureWall\.IgnoreProbe\.exe') {
        throw 'Security log does not contain 5157 evidence for both validation probes.'
    }
    if (Test-Path -LiteralPath "$env:ProgramData\SecureWall") {
        Copy-Item -LiteralPath "$env:ProgramData\SecureWall" -Destination (Join-Path $resultRoot 'programdata') -Recurse -Force
    }

    [ordered]@{
        AllowInitialExit = $allowInitial
        AllowRetryExit = 0
        IgnoreInitialExit = $ignoreInitial
        IgnoreRetryBlocked = $true
        ProviderPresent = $providerPresent
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $resultRoot 'prompt-results.json') -Encoding UTF8
}
catch {
    $testFailure = $_
}
finally {
    if ($controller -and -not $controller.HasExited) {
        try {
            Stop-Process -Id $controller.Id -Force -ErrorAction Stop
        }
        catch {
            $cleanupFailures += "Could not stop controller process: $($_.Exception.Message)"
        }
    }

    $serviceBeforeCleanup = Get-Service -Name SecureWall -ErrorAction SilentlyContinue
    if (-not $KeepInstalled -and ($installedByScript -or $serviceBeforeCleanup)) {
        try {
            $uninstallLog = Join-Path $resultRoot 'msi-uninstall.log'
            $uninstall = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList @('/x', "`"$msi`"", '/qn', '/norestart', '/L*v', "`"$uninstallLog`"") -WindowStyle Hidden -Wait -PassThru
            Write-EvidenceText 'uninstall.txt' @("ExitCode=$($uninstall.ExitCode)")
            if ($uninstall.ExitCode -ne 0 -and $uninstall.ExitCode -ne 3010) {
                $cleanupFailures += "SecureWall MSI uninstall failed with exit code $($uninstall.ExitCode)."
            }
        }
        catch {
            $cleanupFailures += "Could not run SecureWall MSI uninstall: $($_.Exception.Message)"
        }
    }

    try {
        Write-EvidenceText 'audit-after.csv' @(& "$env:SystemRoot\System32\auditpol.exe" /get "/subcategory:$auditSubcategory" /r)
        if ($LASTEXITCODE -ne 0) {
            throw "auditpol exited with code $LASTEXITCODE."
        }
    }
    catch {
        $cleanupFailures += "Could not capture final audit policy: $($_.Exception.Message)"
    }

    try {
        Save-WfpState 'wfp-after.xml'
    }
    catch {
        $cleanupFailures += "Could not capture final WFP state: $($_.Exception.Message)"
    }

    $remainingService = Get-Service -Name SecureWall -ErrorAction SilentlyContinue
    $providerRemaining = $false
    $wfpAfterPath = Join-Path $resultRoot 'wfp-after.xml'
    if (Test-Path -LiteralPath $wfpAfterPath -PathType Leaf) {
        $providerRemaining = Select-String -LiteralPath $wfpAfterPath -SimpleMatch $providerGuid -Quiet
    }

    $auditRestored = $null
    $auditBeforePath = Join-Path $resultRoot 'audit-before.csv'
    $auditAfterPath = Join-Path $resultRoot 'audit-after.csv'
    if ((Test-Path -LiteralPath $auditBeforePath -PathType Leaf) -and
        (Test-Path -LiteralPath $auditAfterPath -PathType Leaf)) {
        $auditRestored = (Get-Content -Raw -LiteralPath $auditBeforePath) -ceq
            (Get-Content -Raw -LiteralPath $auditAfterPath)
    }

    if (-not $KeepInstalled) {
        if ($remainingService) {
            $cleanupFailures += 'SecureWall service remains after cleanup.'
        }
        if ($providerRemaining) {
            $cleanupFailures += 'SecureWall WFP provider remains after cleanup.'
        }
        if ($auditRestored -ne $true) {
            $cleanupFailures += 'Audit policy was not proven to match its pre-test state.'
        }
        try {
            $firewallAfter = Get-NetFirewallRule | Select-Object Name, DisplayName, Group, Enabled, Direction, Action
            $firewallAfter | Export-Clixml -LiteralPath (Join-Path $resultRoot 'firewall-rules-after.xml')
            if (Compare-Object $firewallBefore $firewallAfter -Property Name, DisplayName, Group, Enabled, Direction, Action) {
                $cleanupFailures += 'Windows Firewall rules differ from the pre-test snapshot.'
            }
            $profilesAfter = Get-NetFirewallProfile | Select-Object Name, NotifyOnListen
            if (Compare-Object $profilesBefore $profilesAfter -Property Name, NotifyOnListen) {
                $cleanupFailures += 'Windows Firewall notification settings differ from the pre-test snapshot.'
            }
            if ((Get-FileHash -LiteralPath $hostsPath -Algorithm SHA256).Hash -ne $hostsHashBefore) {
                $cleanupFailures += 'Hosts file differs from the pre-test snapshot.'
            }
            if (Get-ScheduledTask -TaskName 'SecureWall Controller' -ErrorAction SilentlyContinue) {
                $cleanupFailures += 'SecureWall controller task remains after cleanup.'
            }
        } catch { $cleanupFailures += "Could not verify complete host restoration: $($_.Exception.Message)" }
    }

    Write-EvidenceText 'postflight.txt' @(
        "SecureWallServicePresent=$([bool]$remainingService)",
        "SecureWallProviderPresent=$providerRemaining",
        "AuditPolicyRestored=$auditRestored",
        "KeepInstalled=$KeepInstalled",
        "CleanupFailures=$($cleanupFailures.Count)"
    )
}

if ($testFailure -or $cleanupFailures.Count -gt 0) {
    $failureText = @()
    if ($testFailure) {
        $failureText += ($testFailure | Out-String).Trim()
    }
    $failureText += $cleanupFailures
    $failureText | Set-Content -LiteralPath (Join-Path $resultRoot 'failure.txt') -Encoding UTF8
    throw ($failureText -join [Environment]::NewLine)
}

Write-Output "PASS. Evidence: $resultRoot"
