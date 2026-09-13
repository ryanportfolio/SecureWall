# Windows PowerShell 5.1. Functions are side-effect free except explicit file reads/writes.
Set-StrictMode -Version 2.0

function Assert-DiagnosticAttributes {
    param([IO.FileAttributes]$Attributes)
    if (($Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'ReparsePointRejected' }
}

function Assert-DiagnosticPath {
    param([Parameter(Mandatory = $true)][string]$Path, [switch]$AllowMissingLeaf)
    if ($Path -notmatch '^[A-Za-z]:[\\/]' -or $Path.Substring(2).Contains(':')) { throw 'UnsafePath' }
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    $drive = New-Object IO.DriveInfo $root
    if ($drive.DriveType -ne [IO.DriveType]::Fixed) { throw 'UnsafeDrive' }
    $cursor = $full
    $leaf = $true
    while ($cursor) {
        try { $attrs = [IO.File]::GetAttributes($cursor) }
        catch [IO.FileNotFoundException] { if ($leaf -and $AllowMissingLeaf) { $attrs = 0 } else { throw } }
        catch [IO.DirectoryNotFoundException] { if ($leaf -and $AllowMissingLeaf) { $attrs = 0 } else { throw } }
        Assert-DiagnosticAttributes $attrs
        $parent = [IO.Directory]::GetParent($cursor)
        $cursor = if ($null -eq $parent) { $null } else { $parent.FullName }
        $leaf = $false
    }
    return $full
}

function Get-DiagnosticFailure {
    param($ErrorRecord)
    # Exception messages and paths can contain secrets. Only return a finite classification.
    $e = $ErrorRecord.Exception
    while ($null -ne $e.InnerException) { $e = $e.InnerException }
    if ($e -is [UnauthorizedAccessException] -or $e -is [System.Security.SecurityException]) { return 'access_denied' }
    if ($e -is [IO.FileNotFoundException] -or $e -is [IO.DirectoryNotFoundException]) { return 'missing' }
    if ($e -is [Management.Automation.CommandNotFoundException]) { return 'unavailable' }
    if ($e.Message -in @('UnsafePath', 'UnsafeDrive', 'ReparsePointRejected', 'NotRegularFile', 'InputTooLarge')) { return 'rejected_input' }
    return 'failed'
}

function Get-BoundedDiagnosticHash {
    param([string]$Path, [long]$MaxBytes)
    $stream = $null
    $sha = $null
    try {
        $safePath = Assert-DiagnosticPath $Path
        $stream = [IO.File]::Open($safePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        if ($stream.Length -gt $MaxBytes) { throw 'InputTooLarge' }
        $sha = [Security.Cryptography.SHA256]::Create()
        return @{ hash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', ''); bytes = $stream.Length }
    } finally {
        if ($null -ne $stream) { $stream.Dispose() }
        if ($null -ne $sha) { $sha.Dispose() }
    }
}

function Write-DiagnosticJson {
    param([string]$Path, $Value, [int]$MaxBytes = 8388608)
    $json = ConvertTo-Json -InputObject $Value -Depth 12 -Compress
    $encoding = New-Object Text.UTF8Encoding $false
    if ($encoding.GetByteCount($json) -gt $MaxBytes) { throw 'OutputTooLarge' }
    [IO.File]::WriteAllText($Path, $json, $encoding)
}

function Get-DiagnosticEvents {
    param([int]$Schema = 2)
    $events = @('service_start', 'service_ready', 'service_failure', 'service_stop_requested', 'service_shutdown',
        'baseline_register', 'policy_journal', 'policy_persist', 'policy_enforce', 'policy_rollback', 'policy_publish',
        'policy_recovery', 'policy_recovery_clear', 'fail_closed', 'heartbeat', 'diagnostics_enabled', 'diagnostics_disabled',
        'prompt_allow', 'prompt_ignore', 'network_reload', 'display_reload')
    if ($Schema -eq 2) {
        $events += @('hosts_backup', 'hosts_update', 'hosts_install', 'hosts_restore', 'hosts_restore_verify',
            'hosts_protection', 'dns_flush', 'port_blocklist_state', 'hosts_blocklist_state', 'port_blocklist_rules',
            'configuration_load', 'database_load', 'wfp_subscribe', 'wfp_unsubscribe',
            'windows_firewall_start', 'windows_firewall_stop', 'rule_expiry',
            'audit_lease_start', 'audit_lease_stop', 'audit_subscribe', 'audit_unsubscribe', 'audit_health',
            'audit_record_error', 'audit_recovery', 'prompt_suppression', 'attribution_snapshot', 'unavailable_rule_paths')
    }
    return $events
}

function Get-SafeServiceRegistration {
    param($Service)
    # Fixed booleans only; registration is not proof of the running process token.
    return @{ local_system_account_expected = ([string]$Service.StartName -ieq 'LocalSystem');
        dedicated_win32_own_process = ([string]$Service.ServiceType -ieq 'Own Process') }
}

function ConvertTo-SafeJournalRecord {
    param([string]$Line)
    if ([Text.Encoding]::UTF8.GetByteCount($Line) -gt 2048) { throw 'RecordTooLarge' }
    $r = ConvertFrom-Json -InputObject $Line -ErrorAction Stop
    $fields = @('schema', 'run_id', 'process_id', 'sequence', 'utc', 'uptime_ms', 'event', 'result', 'hresult',
        'dropped_records', 'write_failures', 'observed_allow', 'observed_drop', 'audit_available')
    if ($null -eq $r -or $null -eq $r.PSObject.Properties['schema']) { throw 'InvalidSchema' }
    if ($r.schema -eq 2) { $fields += 'observed_port_blocklist_drop' }
    # Discard entire records with unknown fields, rather than accidentally preserving future sensitive fields.
    if ($null -eq $r -or @($r.PSObject.Properties).Count -ne $fields.Count) { throw 'InvalidSchema' }
    foreach ($field in $fields) { if ($null -eq $r.PSObject.Properties[$field]) { throw 'InvalidSchema' } }
    $safe = [ordered]@{}
    foreach ($field in @($fields | Where-Object { $_ -notin @('run_id', 'utc', 'event', 'result') })) {
        $v = $r.$field
        if ($v -isnot [int] -and $v -isnot [long]) { throw 'InvalidNumber' }
        if ($field -notin @('hresult', 'audit_available') -and $v -lt 0) { throw 'InvalidNumber' }
        $safe[$field] = $v
    }
    if ($r.schema -notin @(1, 2) -or $r.sequence -lt 1 -or $r.process_id -lt 1 -or $r.process_id -gt [int]::MaxValue -or
        $r.hresult -lt [int]::MinValue -or $r.hresult -gt [int]::MaxValue -or $r.audit_available -notin @(-1, 0, 1)) { throw 'InvalidNumber' }
    if ($r.run_id -isnot [string] -or $r.run_id -notmatch '^[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}$') { throw 'InvalidRun' }
    if ($r.utc -isnot [string] -or $r.utc -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?Z$') { throw 'InvalidTime' }
    $date = [DateTime]::Parse($r.utc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
    $events = @(Get-DiagnosticEvents -Schema $r.schema)
    $results = @('attempt', 'success', 'failure', 'observed', 'absent', 'unknown_token', 'expired', 'not_allowable', 'locked')
    if ($r.schema -eq 2) { $results += @('enabled', 'disabled', 'present', 'fallback', 'skipped') }
    if ($r.event -isnot [string] -or $r.event -cnotin $events -or $r.result -isnot [string] -or
        $r.result -cnotin $results) { throw 'InvalidEnum' }
    $safe.run_id = $r.run_id.ToLowerInvariant()
    $safe.utc = $date.ToUniversalTime().ToString('o')
    $safe.event = $r.event
    $safe.result = $r.result
    return [pscustomobject]$safe
}

function Read-DiagnosticJournal {
    param([string]$Directory, [int]$MaxFileBytes = 1048576, [int]$MaxRecords = 20000)
    if ($MaxFileBytes -lt 1 -or $MaxFileBytes -gt 1048576 -or $MaxRecords -lt 1 -or $MaxRecords -gt 20000) { throw 'InvalidBounds' }
    $records = New-Object 'Collections.Generic.List[object]'
    $sources = New-Object 'Collections.Generic.List[object]'
    foreach ($name in @('runtime.3.jsonl', 'runtime.2.jsonl', 'runtime.1.jsonl', 'runtime.jsonl')) {
        $stream = $null
        $invalid = 0
        $accepted = 0
        $bytesRead = 0
        $truncated = $false
        $status = 'success'
        try {
            $path = Assert-DiagnosticPath (Join-Path $Directory $name)
            if (([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::Directory) -ne 0) { throw 'NotRegularFile' }
            $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            if ($stream.Length -gt $MaxFileBytes) { throw 'InputTooLarge' }
            $bytes = New-Object byte[] ($MaxFileBytes + 1)
            while ($bytesRead -lt $bytes.Length) {
                $read = $stream.Read($bytes, $bytesRead, $bytes.Length - $bytesRead)
                if ($read -eq 0) { break }
                $bytesRead += $read
            }
            if ($bytesRead -gt $MaxFileBytes) { throw 'InputTooLarge' }
            # Strict UTF-8; partial last record is explicitly dropped. Never copy raw input.
            $utf8 = New-Object Text.UTF8Encoding $false, $true
            $raw = $utf8.GetString($bytes, 0, $bytesRead)
            $lines = $raw.Split([char]10)
            $truncated = $bytesRead -gt 0 -and -not $raw.EndsWith("`n")
            for ($i = 0; $i -lt $lines.Length - 1; $i++) {
                if ($records.Count -ge $MaxRecords) { $truncated = $true; break }
                try {
                    $record = ConvertTo-SafeJournalRecord $lines[$i].TrimEnd([char]13)
                    $records.Add($record)
                    $accepted++
                } catch { $invalid++ }
            }
            if ($invalid -gt 0 -or $truncated) { $status = 'partial' }
        } catch {
            $status = Get-DiagnosticFailure $_
            if ($status -eq 'missing' -and $name -ne 'runtime.jsonl') { $status = 'absent' }
        }
        finally { if ($null -ne $stream) { $stream.Dispose() } }
        $sources.Add([pscustomobject]@{ file = $name; status = $status; bytes_read = $bytesRead; accepted = $accepted; rejected = $invalid; truncated = $truncated })
    }
    return [pscustomobject]@{ sources = @($sources.ToArray()); records = @($records.ToArray()) }
}

function Get-DiagnosticJournalStatus {
    param([object[]]$Sources)
    $active = @($Sources | Where-Object file -eq 'runtime.jsonl')
    if ($active.Count -ne 1 -or $active[0].status -ne 'success' -or
        @($Sources | Where-Object { $_.file -ne 'runtime.jsonl' -and $_.status -notin @('success', 'absent') }).Count) {
        return 'partial'
    }
    return 'success'
}

function Get-DiagnosticCoverage {
    param([object[]]$Records = @(), [object[]]$Sources = @(), [object[]]$Commands = @())
    $runs = @()
    foreach ($group in @($Records | Group-Object run_id)) {
        $ordered = @($group.Group | Sort-Object sequence)
        $gaps = 0
        $duplicates = 0
        $previous = 0L
        foreach ($r in $ordered) {
            if ($r.sequence -eq $previous) { $duplicates++ }
            elseif ($r.sequence -gt ($previous + 1)) { $gaps++ }
            $previous = $r.sequence
        }
        $runs += [pscustomobject]@{ run_id = $group.Name; first_utc = $ordered[0].utc; last_utc = $ordered[-1].utc;
            records = $ordered.Count; sequence_gaps = $gaps; duplicate_sequences = $duplicates;
            startup_observed = @($ordered | Where-Object event -eq 'service_start').Count -gt 0;
            shutdown_observed = @($ordered | Where-Object event -eq 'service_shutdown').Count -gt 0;
            dropped_records = ($ordered | Measure-Object dropped_records -Maximum).Maximum;
            write_failures = ($ordered | Measure-Object write_failures -Maximum).Maximum;
            observed_allow = ($ordered | Measure-Object observed_allow -Maximum).Maximum;
            observed_drop = ($ordered | Measure-Object observed_drop -Maximum).Maximum;
            port_blocklist_counter_records = @($ordered | Where-Object schema -eq 2).Count;
            observed_port_blocklist_drop = if (@($ordered | Where-Object schema -eq 2).Count) {
                $ordered | Where-Object schema -eq 2 | Sort-Object observed_port_blocklist_drop -Descending |
                    Select-Object -First 1 -ExpandProperty observed_port_blocklist_drop
            } else { $null };
            last_enablement_marker = (@($ordered | Where-Object { $_.event -in @('diagnostics_enabled', 'diagnostics_disabled') } | Select-Object -Last 1 | ForEach-Object event) -join '') }
    }
    $paths = @()
    foreach ($event in @(Get-DiagnosticEvents)) {
        $seen = @($Records | Where-Object event -eq $event)
        $completionEvents = @($event)
        if ($event -eq 'service_start') { $completionEvents += @('service_ready', 'service_failure') }
        if ($event -eq 'service_stop_requested') { $completionEvents += @('service_shutdown', 'service_failure') }
        $matching = @($Records | Where-Object { $_.event -in $completionEvents })
        # Match only within a run and in sequence order. No operation IDs exist, so this
        # is a conservative count of attempts lacking a later terminal observation.
        $pending = 0
        foreach ($run in @($matching | Group-Object run_id)) {
            $outstanding = 0
            foreach ($record in @($run.Group | Sort-Object sequence)) {
                if ($record.event -eq $event -and $record.result -eq 'attempt') { $outstanding++ }
                elseif ($record.result -in @('success', 'failure', 'absent', 'present', 'enabled', 'disabled', 'fallback', 'skipped', 'observed', 'unknown_token', 'expired', 'not_allowable', 'locked') -and $outstanding -gt 0) { $outstanding-- }
            }
            $pending += $outstanding
        }
        $failures = @($seen | Where-Object result -eq 'failure').Count
        $skipped = @($seen | Where-Object result -eq 'skipped').Count
        $paths += [pscustomobject]@{ path = $event; status = if ($failures -or $pending -or $skipped) { 'incomplete' } elseif ($seen.Count) { 'historical_observation' } else { 'unobserved' };
            records = $seen.Count; reported_success = @($seen | Where-Object result -eq 'success').Count;
            reported_failure = $failures; reported_attempt = @($seen | Where-Object result -eq 'attempt').Count;
            attempts_without_completion = $pending;
            reported_enabled = @($seen | Where-Object result -eq 'enabled').Count;
            reported_disabled = @($seen | Where-Object result -eq 'disabled').Count;
            reported_absent = @($seen | Where-Object result -eq 'absent').Count;
            reported_present = @($seen | Where-Object result -eq 'present').Count;
            reported_fallback = @($seen | Where-Object result -eq 'fallback').Count;
            reported_skipped = $skipped;
            reported_observed = @($seen | Where-Object result -eq 'observed').Count }
    }
    return [pscustomobject]@{
        interpretation = 'Historical software observations only, never tests passed. Recorded failures, skipped verification and attempts without later completion are incomplete. Matching is per event and run in sequence order without operation IDs; service_ready/service_failure complete start attempts, and service_shutdown/service_failure complete stop requests. Missing shutdown, sequence gaps, rotation, disabled logging and collection failures leave observation gaps. Counters are per-run maxima, not packet totals across runs; schema 1 has no port-blocklist counter. Current logging enablement is unknown; configuration is never read.'
        journal_sources = $Sources; command_outcomes = $Commands; runs = $runs; paths = $paths
        audit = @{ available_records = @($Records | Where-Object audit_available -eq 1).Count; unavailable_records = @($Records | Where-Object audit_available -eq 0).Count; unknown_records = @($Records | Where-Object audit_available -eq -1).Count }
        unverified = @('packet_delivery_or_absence_of_escaped_packets', 'real_SYSTEM_identity_rejection', 'boot_gap', 'hostile_ACL_tests', 'deliberate_crash', 'installer_rollback', 'power_loss_durability')
    }
}
