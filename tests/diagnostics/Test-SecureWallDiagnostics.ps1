#Requires -Version 5.1
# Fixture tests only: no service, WFP, audit policy, event-log or network queries.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) { throw 'Run these fixtures with Windows PowerShell 5.1.' }
Write-Output ('Windows PowerShell ' + $PSVersionTable.PSVersion.ToString())
$helpers = Join-Path $PSScriptRoot '..\..\tools\diagnostics\Diagnostics.Helpers.ps1'
. $helpers
$script:passed = 0
function Assert-True { param([bool]$Condition, [string]$Name) if (-not $Condition) { throw "FAIL: $Name" }; $script:passed++; Write-Output "PASS: $Name" }
function Assert-Rejected {
    param([scriptblock]$Action, [string]$Name)
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-True $rejected $Name
}
function New-Record {
    param([long]$Sequence = 1, [string]$Event = 'service_start', [string]$Result = 'success')
    return [ordered]@{ schema = 1; run_id = 'a4d7441c-4e58-45fb-9b55-6dfd66142ac0'; process_id = 42; sequence = $Sequence;
        utc = '2026-09-13T12:00:00.0000000Z'; uptime_ms = $Sequence; event = $Event; result = $Result; hresult = 0;
        dropped_records = 0; write_failures = 0; observed_allow = 0; observed_drop = 0; audit_available = -1 }
}
function Convert-Record { param($Record) return ConvertTo-Json -InputObject $Record -Depth 5 -Compress }
function New-RecordV2 {
    param([long]$Sequence = 1, [string]$Event = 'hosts_update', [string]$Result = 'success')
    $record = New-Record $Sequence $Event $Result
    $record.schema = 2
    $record.observed_port_blocklist_drop = 0L
    return $record
}
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('SecureWall-diagnostic-fixtures-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixtureRoot)
$utf8 = New-Object Text.UTF8Encoding $false
try {
    $record = ConvertTo-SafeJournalRecord (Convert-Record (New-Record))
    Assert-True ($record.sequence -eq 1 -and $record.event -eq 'service_start') 'valid schema survives'
    Assert-True ($null -eq $record.PSObject.Properties['observed_port_blocklist_drop']) 'legacy schema never invents a port-blocklist counter'
    $v2 = New-RecordV2
    $v2.observed_port_blocklist_drop = 12345678901L
    $safeV2 = ConvertTo-SafeJournalRecord (Convert-Record $v2)
    Assert-True ($safeV2.schema -eq 2 -and $safeV2.observed_port_blocklist_drop -eq 12345678901L) 'schema 2 preserves Int64 port-blocklist counter'
    $badV2 = New-RecordV2
    $badV2.Remove('observed_port_blocklist_drop')
    Assert-Rejected { ConvertTo-SafeJournalRecord (Convert-Record $badV2) } 'schema 2 requires the new counter'
    $badV2 = New-RecordV2
    $badV2.secret = 'private-registration-command'
    Assert-Rejected { ConvertTo-SafeJournalRecord (Convert-Record $badV2) } 'schema 2 rejects unknown fields'
    foreach ($invalidCounter in @(-1, 1.5, '1', $null, $true)) {
        $badV2 = New-RecordV2
        $badV2.observed_port_blocklist_drop = $invalidCounter
        Assert-Rejected { ConvertTo-SafeJournalRecord (Convert-Record $badV2) } 'schema 2 rejects invalid counter type or range'
    }
    $badV2 = New-RecordV2
    $badV2.schema = 1
    Assert-Rejected { ConvertTo-SafeJournalRecord (Convert-Record $badV2) } 'schema 1 rejects schema 2 fields'
    $badV2 = New-RecordV2
    $badV2.schema = 3
    Assert-Rejected { ConvertTo-SafeJournalRecord (Convert-Record $badV2) } 'unknown future schema rejected'
    Assert-Rejected { ConvertTo-SafeJournalRecord (Convert-Record (New-Record 1 'hosts_update')) } 'legacy enum contract unchanged'
    Assert-Rejected { ConvertTo-SafeJournalRecord (Convert-Record (New-Record 1 'service_start' 'enabled')) } 'legacy result contract unchanged'
    $expandedEvents = @('hosts_backup', 'hosts_update', 'hosts_install', 'hosts_restore', 'hosts_restore_verify', 'hosts_protection',
        'dns_flush', 'port_blocklist_state', 'hosts_blocklist_state', 'port_blocklist_rules', 'configuration_load',
        'database_load', 'wfp_subscribe', 'wfp_unsubscribe', 'windows_firewall_start', 'windows_firewall_stop', 'rule_expiry',
        'audit_lease_start', 'audit_lease_stop', 'audit_subscribe', 'audit_unsubscribe', 'audit_health',
        'audit_record_error', 'audit_recovery', 'prompt_suppression', 'attribution_snapshot', 'unavailable_rule_paths')
    $expandedRecords = @()
    foreach ($event in $expandedEvents) {
        $expandedRecords += ConvertTo-SafeJournalRecord (Convert-Record (New-RecordV2 ($expandedRecords.Count + 1) $event))
    }
    $expandedCoverage = Get-DiagnosticCoverage -Records $expandedRecords
    Assert-True (@($expandedCoverage.paths | Where-Object { $_.path -in $expandedEvents -and $_.status -eq 'historical_observation' }).Count -eq 27) 'every expanded event survives sanitation and receives coverage'
    $stateRecords = @()
    foreach ($result in @('enabled', 'disabled', 'absent', 'present', 'fallback', 'observed')) {
        $stateRecords += ConvertTo-SafeJournalRecord (Convert-Record (New-RecordV2 ($stateRecords.Count + 1) 'hosts_blocklist_state' $result))
    }
    $stateCoverage = (Get-DiagnosticCoverage -Records $stateRecords).paths | Where-Object path -eq 'hosts_blocklist_state'
    Assert-True ($stateCoverage.reported_enabled -eq 1 -and $stateCoverage.reported_disabled -eq 1 -and
        $stateCoverage.reported_absent -eq 1 -and $stateCoverage.reported_present -eq 1 -and
        $stateCoverage.reported_fallback -eq 1 -and $stateCoverage.reported_observed -eq 1) 'state and fallback observations counted explicitly'
    $mixedCoverage = Get-DiagnosticCoverage -Records @($record, $safeV2)
    Assert-True ($mixedCoverage.runs[0].port_blocklist_counter_records -eq 1 -and
        $mixedCoverage.runs[0].observed_port_blocklist_drop -eq 12345678901L) 'mixed schema coverage retains port-blocklist evidence'
    Assert-True ($null -eq (Get-DiagnosticCoverage -Records @($record)).runs[0].observed_port_blocklist_drop) 'legacy coverage counter unknown rather than zero'
    $attempts = @(
        ConvertTo-SafeJournalRecord (Convert-Record (New-RecordV2 1 'hosts_install' 'success'))
        ConvertTo-SafeJournalRecord (Convert-Record (New-RecordV2 2 'hosts_install' 'attempt'))
        ConvertTo-SafeJournalRecord (Convert-Record (New-RecordV2 3 'hosts_install' 'attempt'))
        ConvertTo-SafeJournalRecord (Convert-Record (New-RecordV2 4 'hosts_install' 'success'))
    )
    $attemptCoverage = (Get-DiagnosticCoverage -Records $attempts).paths | Where-Object path -eq 'hosts_install'
    Assert-True ($attemptCoverage.status -eq 'incomplete' -and $attemptCoverage.attempts_without_completion -eq 1) 'earlier success cannot complete later attempt; repeated attempts remain visible'
    $otherRun = New-RecordV2 1 'hosts_install' 'success'
    $otherRun.run_id = 'b4d7441c-4e58-45fb-9b55-6dfd66142ac0'
    $attemptCoverage = (Get-DiagnosticCoverage -Records @($attempts[1], (ConvertTo-SafeJournalRecord (Convert-Record $otherRun)))).paths | Where-Object path -eq 'hosts_install'
    Assert-True ($attemptCoverage.attempts_without_completion -eq 1) 'completion in another run cannot hide an incomplete attempt'
    $normalLifecycle = @(
        ConvertTo-SafeJournalRecord (Convert-Record (New-Record 1 'service_start' 'attempt'))
        ConvertTo-SafeJournalRecord (Convert-Record (New-Record 2 'service_ready' 'success'))
        ConvertTo-SafeJournalRecord (Convert-Record (New-Record 3 'service_stop_requested' 'attempt'))
        ConvertTo-SafeJournalRecord (Convert-Record (New-Record 4 'service_shutdown' 'success'))
    )
    $lifecycleCoverage = Get-DiagnosticCoverage -Records $normalLifecycle
    Assert-True (@($lifecycleCoverage.paths | Where-Object { $_.path -in @('service_start', 'service_stop_requested') -and
        $_.status -eq 'historical_observation' -and $_.attempts_without_completion -eq 0 }).Count -eq 2) 'ready and shutdown complete lifecycle attempts within their run'
    $lifecycleCoverage = Get-DiagnosticCoverage -Records @($normalLifecycle[0])
    Assert-True (@($lifecycleCoverage.paths | Where-Object { $_.path -eq 'service_start' -and $_.status -eq 'incomplete' }).Count -eq 1) 'interrupted startup stays incomplete'
    $skippedCoverage = Get-DiagnosticCoverage -Records @(
        ConvertTo-SafeJournalRecord (Convert-Record (New-RecordV2 1 'hosts_restore_verify' 'attempt'))
        ConvertTo-SafeJournalRecord (Convert-Record (New-RecordV2 2 'hosts_restore_verify' 'skipped'))
    )
    Assert-True (@($skippedCoverage.paths | Where-Object { $_.path -eq 'hosts_restore_verify' -and $_.status -eq 'incomplete' -and
        $_.reported_skipped -eq 1 -and $_.attempts_without_completion -eq 0 }).Count -eq 1) 'skipped verification remains incomplete despite terminal observation'
    $registration = Get-SafeServiceRegistration ([pscustomobject]@{ StartName = 'LocalSystem'; ServiceType = 'Own Process'; PathName = 'private-command' })
    Assert-True ($registration.local_system_account_expected -and $registration.dedicated_win32_own_process -and $registration.Count -eq 2) 'expected registration emits exactly two booleans'
    $registration = Get-SafeServiceRegistration ([pscustomobject]@{ StartName = 'secret-account'; ServiceType = 'Share Process'; PathName = 'private-command' })
    Assert-True (-not $registration.local_system_account_expected -and -not $registration.dedicated_win32_own_process -and
        (ConvertTo-Json $registration) -notmatch 'secret-account|private-command|Share Process') 'unexpected registration stays bounded without account or command data'
    $extra = New-Record
    $extra.secret = 'password=never-export-this'
    Assert-Rejected { ConvertTo-SafeJournalRecord (Convert-Record $extra) } 'unknown secret-bearing field rejects entire record'
    $bad = New-Record
    $bad.event = 'C:\Users\alice\private.txt'
    Assert-Rejected { ConvertTo-SafeJournalRecord (Convert-Record $bad) } 'path text cannot become event'
    $bad = New-Record
    $bad.sequence = '1'
    Assert-Rejected { ConvertTo-SafeJournalRecord (Convert-Record $bad) } 'numeric strings rejected'
    $bad.sequence = -1
    Assert-Rejected { ConvertTo-SafeJournalRecord (Convert-Record $bad) } 'negative sequence rejected'
    $bad = New-Record
    $bad.observed_allow = 1.5
    Assert-Rejected { ConvertTo-SafeJournalRecord (Convert-Record $bad) } 'fractional counters rejected'
    $bad = New-Record
    $bad.utc = 'alice@example.invalid'
    Assert-Rejected { ConvertTo-SafeJournalRecord (Convert-Record $bad) } 'arbitrary time string rejected'
    $bad = New-Record
    $bad.result = 'SUCCESS'
    Assert-Rejected { ConvertTo-SafeJournalRecord (Convert-Record $bad) } 'case-sensitive enum validation'
    Assert-Rejected { ConvertTo-SafeJournalRecord ('x' * 2049) } 'record byte bound'
    Assert-Rejected { Assert-DiagnosticPath '\\server\share\runtime.jsonl' } 'UNC input rejected'
    Assert-Rejected { Assert-DiagnosticPath ($fixtureRoot + ':secret') } 'alternate data stream input rejected'
    Assert-Rejected { Assert-DiagnosticAttributes ([IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint) } 'reparse attribute rejected'
    Assert-Rejected { Read-DiagnosticJournal $fixtureRoot -MaxFileBytes 1048577 } 'caller cannot raise file bound'
    Assert-Rejected { Read-DiagnosticJournal $fixtureRoot -MaxRecords 20001 } 'caller cannot raise record bound'
    $missing = Read-DiagnosticJournal (Join-Path $fixtureRoot 'absent')
    Assert-True (@($missing.sources | Where-Object status -eq 'absent').Count -eq 3 -and
        $missing.sources[-1].status -eq 'missing' -and $missing.records.Count -eq 0) 'absent generations and missing active journal explicit'
    Assert-True ((Get-DiagnosticJournalStatus $missing.sources) -eq 'partial') 'all missing files leave collection partial'
    try { throw (New-Object UnauthorizedAccessException 'C:\Users\alice\secret') } catch { $denied = Get-DiagnosticFailure $_ }
    Assert-True ($denied -eq 'access_denied') 'denied exception classified without identity or path'
    $path = Join-Path $fixtureRoot 'runtime.jsonl'
    $lines = (Convert-Record (New-Record)) + "`n" + (Convert-Record $extra) + "`n" + (Convert-Record (New-Record 3 'policy_publish')) + "`n" + '{"incomplete":'
    [IO.File]::WriteAllText($path, $lines, $utf8)
    # Unrelated recovery/configuration data must never be read or copied.
    [IO.File]::WriteAllText((Join-Path $fixtureRoot 'profiles.json'), 'credential-canary', $utf8)
    $journal = Read-DiagnosticJournal $fixtureRoot
    Assert-True ($journal.records.Count -eq 2 -and $journal.sources[-1].rejected -eq 1 -and $journal.sources[-1].truncated) 'sanitization and incomplete tail are explicit'
    Assert-True ((ConvertTo-Json $journal -Depth 10) -notmatch 'never-export|credential-canary|incomplete') 'sensitive fixture bytes absent from result'
    $coverage = Get-DiagnosticCoverage -Records $journal.records -Sources $journal.sources -Commands @(@{ command = 'service'; status = 'timeout' })
    Assert-True ($coverage.runs[0].sequence_gaps -eq 1 -and -not $coverage.runs[0].shutdown_observed) 'gap and missing shutdown remain observations'
    Assert-True (@($coverage.paths | Where-Object { $_.path -eq 'policy_publish' -and $_.status -eq 'historical_observation' }).Count -eq 1) 'publish labeled historical observation'
    Assert-True ($coverage.command_outcomes[0].status -eq 'timeout' -and $coverage.unverified.Count -eq 7) 'partial collection preserves failures and unverified cases'
    $lifecycle = @(ConvertTo-SafeJournalRecord (Convert-Record (New-Record 1 'service_failure' 'failure'));
        ConvertTo-SafeJournalRecord (Convert-Record (New-Record 2 'service_stop_requested' 'attempt')))
    $coverage = Get-DiagnosticCoverage -Records $lifecycle
    Assert-True (@($coverage.paths | Where-Object { $_.path -eq 'service_failure' -and $_.status -eq 'incomplete' -and $_.reported_failure -eq 1 }).Count -eq 1) 'service failure leaves coverage incomplete'
    Assert-True (@($coverage.paths | Where-Object { $_.path -eq 'service_stop_requested' -and $_.status -eq 'incomplete' -and $_.attempts_without_completion -eq 1 }).Count -eq 1) 'service stop request without completion leaves coverage incomplete'
    $disabled = @(ConvertTo-SafeJournalRecord (Convert-Record (New-Record 1 'diagnostics_enabled')); ConvertTo-SafeJournalRecord (Convert-Record (New-Record 2 'diagnostics_disabled')))
    $coverage = Get-DiagnosticCoverage -Records $disabled
    Assert-True ($coverage.runs[0].last_enablement_marker -eq 'diagnostics_disabled' -and $coverage.interpretation -match 'unknown') 'disable marker does not establish current enablement'
    $limited = Read-DiagnosticJournal $fixtureRoot -MaxRecords 1
    Assert-True ($limited.records.Count -eq 1 -and $limited.sources[-1].truncated) 'record-count bound'
    [IO.File]::WriteAllText($path, ('x' * 1048577), $utf8)
    $large = Read-DiagnosticJournal $fixtureRoot
    Assert-True ($large.sources[-1].status -eq 'rejected_input' -and $large.records.Count -eq 0) 'oversized file rejected before reading'
    [IO.File]::WriteAllBytes($path, [byte[]]@(255, 255, 10))
    $encodingFailure = Read-DiagnosticJournal $fixtureRoot
    Assert-True ($encodingFailure.sources[-1].status -eq 'failed' -and $encodingFailure.records.Count -eq 0) 'invalid UTF-8 never copied'
    [IO.File]::WriteAllText($path, (Convert-Record (New-Record)) + "`n", $utf8)
    $locked = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { $partial = Read-DiagnosticJournal $fixtureRoot } finally { $locked.Dispose() }
    Assert-True ($partial.sources[-1].status -eq 'failed' -and $partial.records.Count -eq 0) 'locked source leaves explicit failure'
    Assert-Rejected { Get-BoundedDiagnosticHash $path 1 } 'binary hashing enforces byte bound'
    $hash = Get-BoundedDiagnosticHash $path 1048576
    Assert-True ($hash.hash -eq (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash) 'bounded hash matches native SHA256'
    $output = Join-Path $fixtureRoot 'fixture-result.json'
    Write-DiagnosticJson $output $journal
    Assert-Rejected { Write-DiagnosticJson $output @{ content = ('x' * 100) } -MaxBytes 10 } 'output size bound'
    # Exercise the real worker boundary under Windows PowerShell, restricted to fixture journal reads.
    $shell = Join-Path ([Environment]::GetFolderPath('Windows')) 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $worker = Join-Path $PSScriptRoot '..\..\tools\diagnostics\Diagnostics.Worker.ps1'
    & $shell -NoLogo -NoProfile -NonInteractive -File $worker -Kind journal -OutputFile $output -JournalDirectory $fixtureRoot
    Assert-True ($LASTEXITCODE -eq 0) 'PowerShell 5.1 journal worker exits successfully'
    $workerResult = ConvertFrom-Json ([IO.File]::ReadAllText($output))
    Assert-True ($workerResult.status -eq 'success' -and $workerResult.data.records.Count -eq 1 -and
        @($workerResult.data.sources | Where-Object status -eq 'absent').Count -eq 3) 'worker succeeds with active file and explicitly absent generations'
    $coverage = Get-DiagnosticCoverage -Records $workerResult.data.records -Sources $workerResult.data.sources
    Assert-True (@($coverage.journal_sources | Where-Object status -eq 'absent').Count -eq 3 -and
        -not $coverage.runs[0].shutdown_observed) 'successful collection preserves source absence and observation gaps'
    [IO.File]::WriteAllText($path, (Convert-Record (New-Record)) + "`n" + (Convert-Record $v2) + "`n", $utf8)
    & $shell -NoLogo -NoProfile -NonInteractive -File $worker -Kind journal -OutputFile $output -JournalDirectory $fixtureRoot
    $workerResult = ConvertFrom-Json ([IO.File]::ReadAllText($output))
    Assert-True ($LASTEXITCODE -eq 0 -and $workerResult.status -eq 'success' -and $workerResult.data.records.Count -eq 2 -and
        $workerResult.data.records[1].observed_port_blocklist_drop -eq 12345678901L) 'real worker accepts mixed schema generations without dropping the new counter'
    & $shell -NoLogo -NoProfile -NonInteractive -File $worker -Kind journal -OutputFile $output -JournalDirectory (Join-Path $fixtureRoot 'absent')
    $workerResult = ConvertFrom-Json ([IO.File]::ReadAllText($output))
    Assert-True ($LASTEXITCODE -eq 0 -and $workerResult.status -eq 'partial' -and $workerResult.data.records.Count -eq 0) 'worker reports all missing journal as partial'

    # Synthetic denied-read seam: exercise the reader and status aggregator without changing ACLs.
    $rotatedPath = Join-Path $fixtureRoot 'runtime.1.jsonl'
    [IO.File]::WriteAllText($rotatedPath, (Convert-Record (New-Record)) + "`n", $utf8)
    $originalPathCheck = ${function:Assert-DiagnosticPath}
    try {
        function Assert-DiagnosticPath {
            param([string]$Path, [switch]$AllowMissingLeaf)
            if ([IO.Path]::GetFileName($Path) -eq 'runtime.1.jsonl') { throw (New-Object UnauthorizedAccessException 'synthetic denial') }
            & $originalPathCheck -Path $Path -AllowMissingLeaf:$AllowMissingLeaf
        }
        $deniedRotated = Read-DiagnosticJournal $fixtureRoot
    } finally { ${function:Assert-DiagnosticPath} = $originalPathCheck }
    Assert-True ($deniedRotated.sources[2].status -eq 'access_denied' -and
        $deniedRotated.sources[-1].status -eq 'success' -and
        (Get-DiagnosticJournalStatus $deniedRotated.sources) -eq 'partial') 'denied existing rotated journal stays partial alongside valid active file'

    foreach ($case in @(
        @{ name = 'rejected'; text = ('x' * 1048577); status = 'rejected_input' },
        @{ name = 'truncated'; text = '{"incomplete":'; status = 'partial' },
        @{ name = 'invalid'; text = (Convert-Record $extra) + "`n"; status = 'partial' }
    )) {
        [IO.File]::WriteAllText($rotatedPath, $case.text, $utf8)
        & $shell -NoLogo -NoProfile -NonInteractive -File $worker -Kind journal -OutputFile $output -JournalDirectory $fixtureRoot
        $workerResult = ConvertFrom-Json ([IO.File]::ReadAllText($output))
        Assert-True ($LASTEXITCODE -eq 0 -and $workerResult.status -eq 'partial' -and
            $workerResult.data.sources[2].status -eq $case.status -and $workerResult.data.sources[-1].status -eq 'success') ($case.name + ' rotated file keeps worker partial')
    }
    Write-Output ("{0} fixture assertions passed; no live host collection performed." -f $script:passed)
} finally {
    # Delete only this newly created fixture tree, after resolving and checking its exact root.
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved) -match '^SecureWall-diagnostic-fixtures-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    } else { throw 'Fixture cleanup path validation failed.' }
}
