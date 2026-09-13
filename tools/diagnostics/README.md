# Collect live diagnostics

Use this collector to save evidence from normal SecureWall use for local review.
It reads the machine and creates a new directory and ZIP. It does not install or
start SecureWall, change firewall or audit policy, edit settings or upload data.

Detailed diagnostics are off by default. Select **Enable diagnostic logging** on
the **General** tab in SecureWall settings before the trial. Disable it later to stop new detailed
records; existing records remain available. A disable marker is best effort.
See [the live testing guide](../../docs/LIVE-TESTING.md) for trial preparation and [diagnostic coverage](../../docs/DIAGNOSTIC-COVERAGE.md) for the operation-by-operation evidence map.

## Run the collector

Open Windows PowerShell and run from the repository root. Choose an existing
local output directory you control; the example uses your current directory.

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\tools\diagnostics\Collect-SecureWallDiagnostics.ps1 -OutputParent .
```

`-ExecutionPolicy Bypass` applies only to that PowerShell process and its children.
It does not change the machine or user execution-policy setting. If your
organization enforces a policy, use its approved script-execution process.
An elevated console can make protected journal and event-log reads available;
an ordinary console records denied sources explicitly. The collector never
requests elevation itself.

The result prints the directory, ZIP path, ZIP SHA-256 and command outcomes.
The ZIP contains the same payload as the directory:

| File | Evidence |
| --- | --- |
| `manifest.json` | Capture UTC start/end, bounds, command outcomes and each payload file's SHA-256 and size |
| `system.json` | OS version/build/type, architecture and last boot time |
| `service.json` | SecureWall service state, startup mode, PID, exit code and expected account/service-type checks |
| `binary.json` | SCM-registered executable's SHA-256, byte size, numeric file/product versions and Authenticode result |
| `events.json` | Bounded recent relevant SCM/Application error and warning metadata |
| `journal.json` | Sanitized structured records plus outcomes for each known journal generation |
| `coverage.json` | Historical paths, outcome counts, incomplete attempts, run gaps, loss counters and observation limits |

The binary identity describes the registered file on disk. It does not prove that
an already-running process loaded those exact bytes. `NotSigned` and other
signature failures remain visible; successful collection is separate from a valid
signature. The manifest excludes its own hash. Bundle hashes detect later changes;
they do not authenticate who originally wrote a journal record.

Missing service registration is `unavailable` with `service_absent`. Missing,
denied, rejected, timed-out and failed reads remain distinct from `success`.
`success` means the collection operation completed. A missing active journal is
`missing`; a missing rotated generation is `absent`. Journal collection succeeds
when the active file is read successfully and each rotated generation is read
successfully or absent. Denied, rejected, truncated or invalid existing generations
leave the result `partial`. Absent files remain listed in coverage and do not
establish complete history; sequence gaps and other observation limits still apply.

## Bounds and privacy

Defaults are 20 seconds per worker and 120 seconds across workers. Each worker
runs in a separate hidden Windows PowerShell process and is terminated on timeout.
Local JSON processing, hashing and ZIP creation add filesystem time after that
worker budget. Collection can leave a partial directory if local packaging fails.

The collector scans at most 200 recent events per log, within the last 24 hours.
Only metadata for entries whose bounded insertion values mention SecureWall or
TinyWall is retained. Message bodies, insertion values, usernames, machine names,
provider text and command lines are omitted. The scan cap can omit older relevant
events; an empty selection does not establish an error-free interval.

Journal input is restricted to `runtime.jsonl` and `runtime.1.jsonl` through
`runtime.3.jsonl`, with a maximum of 1 MiB per file, 2,048 bytes per record and
20,000 accepted records in total. Local fixed drives and regular files are required;
UNC/device paths, alternate streams and reparse ancestors are rejected. Files are
read with rotation-compatible sharing. A truncated final line, invalid UTF-8,
unknown schema, unknown field or invalid value is rejected and counted or reported.
No arbitrary directory tree is copied. Concurrent rotation can create duplicates
or gaps, which coverage reports preserve.

Default collection never opens policy/configuration, API settings, encrypted
recovery data, raw hosts files, controller text logs or network payloads. Journal
strings use strict event/result enums, UTC timestamps and run GUIDs; other exported
journal fields are numeric. Sources remain untrusted observations. The collector's
path checks and fixture tests do not establish resistance to a hostile actor
changing filesystem paths concurrently.

Each worker output is capped at 8 MiB, payloads at 16 MiB before the small manifest,
and the registered binary at 128 MiB for hashing. Output inherits the selected
parent directory's permissions. Keep that directory private and review the ZIP
before sharing it.

Available bounds:

```powershell
.\tools\diagnostics\Collect-SecureWallDiagnostics.ps1 `
  -OutputParent C:\MyDiagnostics `
  -RecentHours 24 -MaxEvents 200 `
  -CommandTimeoutSeconds 20 -CollectionTimeoutSeconds 120
```

`RecentHours` accepts 1 through 168; `MaxEvents` accepts 1 through 1,000 per log;
command timeout accepts 5 through 60 seconds; worker budget accepts 10 through
300 seconds. `-JournalDirectory` selects a different local directory, still using
only the four fixed journal filenames and the same validation and privacy rules.
The default is `%ProgramData%\SecureWall\logs`.

## Optional network inventory

`-IncludeNetworkInventory` explicitly adds local and remote IP addresses, ports,
connection states and owning process IDs to `network.json`. This can reveal other
applications' activity. The console prints a disclosure when enabled. The TCP and
UDP snapshots each retain at most 256 entries and run under the worker timeout.
No process command lines, account names, packet contents or WFP state dump are
collected. Leave the switch off for the default privacy scope.

## Interpret coverage

Coverage distinguishes unobserved paths, historical observations and incomplete observations. Attempts without a completion in the same service run remain unresolved; concurrent operations do not carry individual operation IDs. Counts of
reported success/failure describe software outcomes. A successful policy publish,
prompt response, recovery or WFP registration is useful evidence that its code
path ran. Allow/drop counters cover periods when diagnostics were enabled and
do not prove packet delivery or the absence of escaped packets.

The collector accepts schema 1 and schema 2 records with their exact allowed fields. Schema 2 adds the aggregate count of observed drops attributed to committed port-blocklist filters. That counter is unavailable for legacy-only runs, rather than reported as zero. A hosts-based domain block cannot produce a WFP domain-drop counter.

The service registration checks describe SCM data, not a verification of the running token or a hostile identity test.

The collector deliberately does not read settings to learn the current checkbox
state. Last enable/disable markers are historical; missing records can reflect
disabled logging, rotation, startup before trusted opt-in, write failures or
collection failures. Missing shutdown is an observation gap, not a crash verdict.

Boot-gap behavior, real SYSTEM identity rejection, hostile ACL tests, deliberate
crash behavior, installer rollback and power-loss durability remain unverified by
this bundle. Review any separate direct evidence independently.

## Fixture verification

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\tests\diagnostics\Test-SecureWallDiagnostics.ps1
```

Fixtures exercise strict field sanitation, malformed/oversized input, bounded
reads and output, missing and locked files, access-denied classification, reparse
attribute rejection, run gaps, historical disable markers, partial collection,
SHA-256 and the Windows PowerShell 5.1 journal worker. They create and remove only
their temporary fixture directory. They do not query live services, event logs or
network state. Access denial and reparse rejection use synthetic inputs; these
tests do not alter real ACLs or establish a hostile filesystem security boundary.
