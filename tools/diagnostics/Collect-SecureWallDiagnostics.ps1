#Requires -Version 5.1
<#
.SYNOPSIS
Collects bounded read-only SecureWall evidence into a new directory and ZIP.
.DESCRIPTION
The default bundle omits settings, policy, recovery files, accounts, command lines,
event messages, hosts contents and network endpoints. No changes to host policy or
services are made. IncludeNetworkInventory explicitly includes sensitive addresses,
ports and process IDs. No collection data is uploaded.
#>
[CmdletBinding()]
param(
    [string]$OutputParent = (Get-Location).Path,
    [string]$JournalDirectory = (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'SecureWall\logs'),
    [ValidateRange(1, 168)][int]$RecentHours = 24,
    [ValidateRange(1, 1000)][int]$MaxEvents = 200,
    [ValidateRange(5, 60)][int]$CommandTimeoutSeconds = 20,
    [ValidateRange(10, 300)][int]$CollectionTimeoutSeconds = 120,
    [switch]$IncludeNetworkInventory
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Diagnostics.Helpers.ps1')
$start = [DateTime]::UtcNow
$timer = [Diagnostics.Stopwatch]::StartNew()
$resolvedParent = Resolve-Path -LiteralPath $OutputParent
if ($resolvedParent.Provider.Name -ne 'FileSystem') { throw 'OutputParent must be a filesystem directory.' }
$parent = Assert-DiagnosticPath $resolvedParent.ProviderPath
if (-not [IO.Directory]::Exists($parent)) { throw 'OutputParent must be an existing local directory.' }
$name = 'SecureWall-diagnostics-' + $start.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$destination = Join-Path $parent $name
if ([IO.Directory]::Exists($destination) -or [IO.File]::Exists($destination)) { throw 'Output directory already exists.' }
[void][IO.Directory]::CreateDirectory($destination)
$worker = Join-Path $PSScriptRoot 'Diagnostics.Worker.ps1'
$shell = Join-Path ([Environment]::GetFolderPath('Windows')) 'System32\WindowsPowerShell\v1.0\powershell.exe'
$commands = New-Object 'Collections.Generic.List[object]'
$journal = $null
$kinds = @('system', 'service', 'binary', 'events', 'journal')
if ($IncludeNetworkInventory) {
    Write-Warning 'Network inventory includes local and remote IP addresses, ports and process IDs. Review the bundle before sharing.'
    $kinds += 'network'
}
foreach ($kind in $kinds) {
    $remaining = $CollectionTimeoutSeconds - $timer.Elapsed.TotalSeconds
    if ($remaining -le 0) {
        $commands.Add([pscustomobject]@{ command = $kind; status = 'skipped_deadline'; elapsed_ms = 0 })
        continue
    }
    $file = Join-Path $destination ($kind + '.json')
    $step = [Diagnostics.Stopwatch]::StartNew()
    $process = $null
    $status = 'failed'
    try {
        # EncodedCommand avoids shell interpolation of caller-supplied paths.
        $quote = { param($value) "'" + $value.Replace("'", "''") + "'" }
        $command = '& ' + (& $quote $worker) + ' -Kind ' + (& $quote $kind) + ' -OutputFile ' + (& $quote $file) +
            ' -JournalDirectory ' + (& $quote $JournalDirectory) + ' -RecentHours ' + $RecentHours + ' -MaxEvents ' + $MaxEvents
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        $info = New-Object Diagnostics.ProcessStartInfo
        $info.FileName = $shell
        $info.Arguments = '-NoLogo -NoProfile -NonInteractive -EncodedCommand ' + $encoded
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $process = [Diagnostics.Process]::Start($info)
        $milliseconds = [int]([Math]::Min($remaining, $CommandTimeoutSeconds) * 1000)
        if (-not $process.WaitForExit($milliseconds)) {
            $process.Kill()
            if (-not $process.WaitForExit(2000)) { throw 'WorkerTerminationFailed' }
            $status = 'timeout'
            # The only partial file is the worker's JSON envelope. Replace it without reading it.
            Write-DiagnosticJson $file @{ status = 'timeout' }
        } elseif (-not [IO.File]::Exists($file)) {
            $status = 'failed_no_output'
            Write-DiagnosticJson $file @{ status = $status }
        } else {
            $outputInfo = Get-Item -LiteralPath $file
            if ($outputInfo.Length -gt 8388608) { throw 'OutputTooLarge' }
            $output = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($file))
            $status = [string]$output.status
            if ($kind -eq 'journal' -and $null -ne $output.PSObject.Properties['data']) { $journal = $output.data }
        }
    } catch {
        $status = Get-DiagnosticFailure $_
        Write-DiagnosticJson $file @{ status = $status }
    } finally {
        if ($null -ne $process) { if (-not $process.HasExited) { $process.Kill() }; $process.Dispose() }
    }
    $commands.Add([pscustomobject]@{ command = $kind; status = $status; elapsed_ms = $step.ElapsedMilliseconds })
}
$records = @()
$sources = @()
if ($null -ne $journal) { $records = @($journal.records); $sources = @($journal.sources) }
$coverage = Get-DiagnosticCoverage -Records $records -Sources $sources -Commands @($commands.ToArray())
Write-DiagnosticJson (Join-Path $destination 'coverage.json') $coverage
$files = @()
$total = 0L
foreach ($file in @(Get-ChildItem -LiteralPath $destination -File)) {
    $total += $file.Length
    if ($total -gt 16777216) { throw 'Bundle exceeds 16 MiB. Partial directory retained; no ZIP created.' }
    $files += @{ file = $file.Name; bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
}
$manifest = @{
    schema = 1; capture_start_utc = $start.ToString('o'); capture_end_utc = [DateTime]::UtcNow.ToString('o')
    collector_version = 1; files = $files; commands = @($commands.ToArray()); sensitive_network_inventory = [bool]$IncludeNetworkInventory
    limits = @{ command_seconds = $CommandTimeoutSeconds; collection_seconds = $CollectionTimeoutSeconds;
        events_scanned_per_log = $MaxEvents; recent_hours = $RecentHours; journal_files = 4; source_bytes_per_journal = 1048576;
        journal_record_bytes = 2048; journal_records = 20000; payload_bytes = 16777216 }
    note = 'The collection deadline bounds worker execution. Local packaging adds filesystem time. Manifest hashes cover every payload file; the manifest itself is excluded. Hashes detect later changes, not source authenticity.'
}
Write-DiagnosticJson (Join-Path $destination 'manifest.json') $manifest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = $destination + '.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($destination, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
[pscustomobject]@{ directory = $destination; zip = $zip; zip_sha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash;
    commands = @($commands.ToArray()) }
