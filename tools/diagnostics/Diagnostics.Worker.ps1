param(
    [ValidateSet('system', 'service', 'binary', 'events', 'journal', 'network')][string]$Kind,
    [string]$OutputFile,
    [string]$JournalDirectory,
    [ValidateRange(1, 168)][int]$RecentHours = 24,
    [ValidateRange(1, 1000)][int]$MaxEvents = 200
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Diagnostics.Helpers.ps1')

function Get-RegisteredService {
    # Never return PathName, StartName, user identity, machine name or command line.
    return Get-CimInstance Win32_Service -Filter "Name='SecureWall'" -OperationTimeoutSec 10
}

function Get-BinaryIdentity {
    $service = Get-RegisteredService
    if ($null -eq $service) { return @{ status = 'unavailable'; reason = 'service_absent' } }
    $command = [string]$service.PathName
    # Parse SCM registration only. Never execute it or include the arguments in output.
    if ($command -match '^\s*"([^"\r\n]+\.exe)"(?:\s|$)') { $path = $Matches[1] }
    elseif ($command -match '^\s*([^\s"\r\n]+\.exe)(?:\s|$)') { $path = $Matches[1] }
    else { return @{ status = 'unavailable'; reason = 'ambiguous_registration_path' } }
    $path = Assert-DiagnosticPath $path
    $file = Get-Item -LiteralPath $path -Force
    if ($file.PSIsContainer -or $file.Length -gt 134217728) { throw 'InputTooLarge' }
    $before = Get-BoundedDiagnosticHash $path 134217728
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    $after = Get-BoundedDiagnosticHash $path 134217728
    if ($before.Hash -ne $after.Hash) { return @{ status = 'partial'; reason = 'binary_changed_during_capture' } }
    # Numeric version components avoid arbitrary version-resource strings and signer account names.
    return @{ status = 'success'; data = @{
        source = 'SCM_registered_executable_on_disk'; loaded_process_identity_verified = $false
        sha256 = $before.Hash; bytes = $before.bytes
        file_version = ('{0}.{1}.{2}.{3}' -f $version.FileMajorPart, $version.FileMinorPart, $version.FileBuildPart, $version.FilePrivatePart)
        product_version = ('{0}.{1}.{2}.{3}' -f $version.ProductMajorPart, $version.ProductMinorPart, $version.ProductBuildPart, $version.ProductPrivatePart)
        authenticode_status = $signature.Status.ToString()
        signature_valid = $signature.Status -eq [Management.Automation.SignatureStatus]::Valid
        signer_certificate_thumbprint = if ($null -ne $signature.SignerCertificate) { $signature.SignerCertificate.Thumbprint } else { $null }
    } }
}

function Get-RelevantEvents {
    $outcomes = @()
    $selected = New-Object 'Collections.Generic.List[object]'
    foreach ($log in @('System', 'Application')) {
        $filter = @{ LogName = $log; StartTime = (Get-Date).AddHours(-$RecentHours); Level = @(1, 2, 3) }
        if ($log -eq 'System') { $filter.ProviderName = 'Service Control Manager' }
        $scanned = 0
        $kept = 0
        try {
            # Limit the scanned events as well as the exported events. Newest first.
            $events = @(Get-WinEvent -FilterHashtable $filter -MaxEvents $MaxEvents -ErrorAction Stop)
            foreach ($event in $events) {
                $scanned++
                $relevant = $false
                foreach ($property in @($event.Properties | Select-Object -First 32)) {
                    if ($property.Value -is [string]) {
                        $value = [string]$property.Value
                        if ($value.Length -le 4096 -and $value -match '(?i)(^|[\\/\s])(?:SecureWall|TinyWall)(?:\.exe)?($|[\s\\/:])') { $relevant = $true; break }
                    }
                }
                if ($relevant) {
                    # Provider names are mapped to fixed categories; arbitrary provider text is not copied.
                    $selected.Add([pscustomobject]@{ log = $log; id = [int]$event.Id; level = [int]$event.Level;
                        record_id = [long]$event.RecordId; utc = $event.TimeCreated.ToUniversalTime().ToString('o');
                        provider_category = if ($log -eq 'System') { 'SCM' } else { 'Application' } })
                    $kept++
                }
            }
            $outcomes += @{ log = $log; status = 'success'; scanned = $scanned; selected = $kept; scan_limit_reached = $scanned -eq $MaxEvents }
        } catch {
            if ($_.FullyQualifiedErrorId -like 'NoMatchingEventsFound*') {
                $outcomes += @{ log = $log; status = 'success'; scanned = 0; selected = 0; scan_limit_reached = $false }
            } else { $outcomes += @{ log = $log; status = (Get-DiagnosticFailure $_); scanned = $scanned; selected = $kept } }
        }
    }
    return @{ status = if (@($outcomes | Where-Object { $_.status -ne 'success' }).Count) { 'partial' } else { 'success' };
        data = @{ logs = $outcomes; events = @($selected.ToArray()); note = 'Only bounded newest error/warning metadata mentioning SecureWall or TinyWall. Messages and insertion data are omitted. An empty selection does not establish absence of errors.' } }
}

try {
    $result = switch ($Kind) {
        'system' {
            $os = Get-CimInstance Win32_OperatingSystem -OperationTimeoutSec 10
            $osVersion = [version]::Parse([string]$os.Version).ToString()
            @{ status = 'success'; data = @{ version = $osVersion;
                build = [int]$os.BuildNumber; product_type = [int]$os.ProductType;
                operating_system_sku = [int]$os.OperatingSystemSKU;
                os_64_bit = [Environment]::Is64BitOperatingSystem; last_boot_utc = $os.LastBootUpTime.ToUniversalTime().ToString('o') } }
        }
        'service' {
            $service = Get-RegisteredService
            if ($null -eq $service) { @{ status = 'unavailable'; reason = 'service_absent' } }
            else {
                $state = if ($service.State -in @('Stopped', 'Start Pending', 'Stop Pending', 'Running', 'Continue Pending', 'Pause Pending', 'Paused', 'Unknown')) { $service.State } else { 'Unknown' }
                $mode = if ($service.StartMode -in @('Boot', 'System', 'Auto', 'Manual', 'Disabled')) { $service.StartMode } else { 'Unknown' }
                @{ status = 'success'; data = @{ state = $state; start_mode = $mode; process_id = [int]$service.ProcessId; exit_code = [uint32]$service.ExitCode } }
            }
        }
        'binary' { Get-BinaryIdentity }
        'events' { Get-RelevantEvents }
        'journal' {
            $journal = Read-DiagnosticJournal -Directory $JournalDirectory
            @{ status = (Get-DiagnosticJournalStatus -Sources $journal.sources); data = $journal }
        }
        'network' {
            # Explicit opt-in only. These address and port fields identify local and remote activity.
            $tcp = @(Get-NetTCPConnection -ErrorAction Stop | Select-Object -First 256 -Property State, LocalAddress, LocalPort, RemoteAddress, RemotePort, OwningProcess)
            $udp = @(Get-NetUDPEndpoint -ErrorAction Stop | Select-Object -First 256 -Property LocalAddress, LocalPort, OwningProcess)
            @{ status = 'success'; data = @{ tcp = $tcp; udp = $udp; limit_per_protocol = 256;
                may_be_truncated = ($tcp.Count -eq 256 -or $udp.Count -eq 256) } }
        }
    }
    Write-DiagnosticJson -Path $OutputFile -Value $result
    exit 0
} catch {
    try { Write-DiagnosticJson -Path $OutputFile -Value @{ status = (Get-DiagnosticFailure $_) } } catch { }
    exit 1
}
