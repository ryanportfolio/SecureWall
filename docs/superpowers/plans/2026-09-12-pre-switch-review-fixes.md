# Pre-switch review fixes implementation plan

> Agentic execution uses the user-requested Long Horizon workflow: fresh executors, separate fresh auditors, and manager-owned state in `.tmp/long-horizon/pre-switch-fixes/state.md`. No Git publication or live firewall activation is authorized.

**Goal:** resolve the source defects found in the September 12 pre-switch review and establish reproducible local evidence without claiming unrun packet-level acceptance.

**Architecture:** retain the LocalSystem-owned dynamic WFP session and restrictive persistent baseline. Security transitions fail closed; storage and identity checks happen at shared production boundaries. Preserve the existing lightweight regression harness and native .NET Framework build.

**Tech stack:** C# 9, .NET Framework 4.8, WFP, WinForms, WiX 3.14.1; net9.0 pure test harness.

## Round 1: durable persistence

Files: `TinyWall/SerializationHelper.cs`, `TinyWall/AtomicFileWriter.cs`, `pylorak.Utilities/AtomicFileUpdater.cs`, and `tests/SecureWall.Core.Tests/AtomicFileWriterTests.cs` plus test wiring if required.

- [ ] Exercise successful encrypted round trip, failed serialization preserving the old file, and encryption finalization before flush using the production adapter.
- [ ] Keep the encrypted stream open through `FlushFinalBlock()`, then call the underlying `FileStream.Flush(true)` before replacement. Preserve the target ACL and same-directory temporary-file behavior. Current adapters flush content before replacement and the installed file afterward; write-through rename/delete ordering and physical-media guarantees remain unverified.
- [ ] Inspect journal create/replace/clear ordering and document the actual durability limit; avoid asserting power-loss behavior solely from a mock callback test.
- [ ] Run local regression tests, then obtain a fresh audit of the actual diff.

## Round 2: enforcement state transitions

Files: `TinyWall/TinyWallService.cs`, `TinyWall/Prompting/EnforcementPolicy.cs`, `tests/SecureWall.Core.Tests/EnforcementHardeningTests.cs`.

- [ ] Add regressions for missing versus corrupt stored config, environmental reload failure, worker-local inactivity locking, and incremental/full-load application identity parity.
- [ ] Replace catch-all defaults with defaults only on confirmed absence; propagate existing-file load failures.
- [ ] Guard required environmental reloads with failure-triggered `FailClosed()` and retain coherent published state.
- [ ] Replace worker-side `Q.Add(LOCK)` with direct lock handling; inspect all other worker-to-self enqueue paths.
- [ ] Normalize executable identities at the shared rule-install boundary, including inherited raw-socket rules.
- [ ] Run regression tests and native build; obtain a fresh audit.

## Round 3: protect machine data

Files: new machine-data guard beside `TinyWall/Installer/InstallationSafety.cs`, `TinyWall/Utils.cs`, installer/startup entry points, `MsiSetup/Product.wxs`, and lifecycle tests.

- [ ] Test trusted and untrusted owner/write permissions, unsafe ancestors and reparse paths, existing unsafe directories, and safe creation.
- [ ] Establish a protected machine-data directory at privileged setup, and verify it before service policy reads/writes. Do not silently bless an attacker-controlled existing tree.
- [ ] Ensure ordinary controller access still works and fresh installation does not depend on an unprivileged process creating protected state.
- [ ] Check MSI ordering before payload/service use and preserve diagnostic evidence on rejection.
- [ ] Run local tests/source checks and build; obtain a fresh audit. Actual precreated-directory MSI attack remains a VM gate.

## Round 4: restoration and diagnostics

Files: `TinyWall/HostsFileManager.cs`, `TinyWall/TinyWallDoctor.cs`, `TinyWall/FirewallLogWatcher.cs`, `TinyWall/ExecutableRiskProbe.cs`, `TinyWall/ServicePidMap.cs`, service call sites, and focused regression helpers/tests.

- [ ] Correct hosts success/failure contracts; make genuine failure throw or propagate into configuration/uninstall failure before service deletion; missing original backup must remain a valid never-enabled case.
- [ ] Log audit subscription failures and expose degraded health through an existing controller notification path; retry conservatively if the API permits it.
- [ ] Count/coalesce buffer suppression diagnostics with bounded output.
- [ ] Include executable file ACL/ownership and parent replacement rights in risk classification.
- [ ] Avoid enumerating all services per drop while revalidating selected identity against current evidence; no stale PID-to-service authority.
- [ ] Run targeted regressions and obtain a fresh audit.

## Round 5: restrictive defaults and acceptance preparation

Files: source Windows Update database and packaged `profiles.json`, recovery policy/associated tests, `docs/SECURITY.md`, `docs/TESTING.md`, `docs/HARDENING-VALIDATION.md`, relevant reference documentation, and guarded VM tooling if needed.

- [ ] Remove the executable-wide `svchost.exe` TCP 80/443 exception; keep explicit service identities and document Windows Update live testing.
- [ ] Apply the selected crash/startup policy; the user selected strict external deny until service recovery, including DNS/DHCP with no recovery permits. Verify no broad baseline port-only grant survives.
- [ ] Add exact live test cases for config corruption, data-directory attacks, environmental reload failure, full queue, inherited identity, hosts failure, and changed networking defaults.
- [ ] Diagnose the synthetic pipe failure before retrying; fix production/test code only when evidence establishes the cause.
- [ ] Build Debug and Release, run core/protocol checks, and run existing installer checks with available tools. Record unavailable signing/package/VM requirements accurately.
- [ ] Obtain a fresh audit of this round.

## Round 6: integrated verification

- [ ] Freeze writers and record changed-file SHA-256 fingerprints.
- [ ] Fresh auditor inspects all final acceptance checks, baseline, actual integrated source, tests, and documented limitations without receiving executor verdicts.
- [ ] Repair findings in fresh bounded rounds and re-audit affected guarantees.
- [ ] Save final evidence and report implementation status separately from production switch readiness.

Run from the repository root:

```powershell
dotnet run --project tests\SecureWall.Core.Tests\SecureWall.Core.Tests.csproj --no-restore
& 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe' TinyWall\TinyWall.csproj /t:Build /p:Configuration=Debug /p:RestorePackages=false /v:minimal /nologo
& 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe' TinyWall\TinyWall.csproj /t:Build /p:Configuration=Release /p:RestorePackages=false /v:minimal /nologo
```

Expected: all registered tests pass, native builds return zero. The debug executable's `/protocolselftest` and `/pipeintegrationtest` must each return zero to count as passing. No synthetic result substitutes for the documented disposable-VM matrix, and no local edit supplies a production signing identity.

## R5 local scope and acceptance

R5 owns only the Windows_Update source/payload data, existing installer source checks and allowed documentation. Concurrent executors own all C# changes and shared builds. R5 does not dispatch agents or edit manager state.

The source contract places both defaults under protected INSTALLDIR/data-defaults. SYSTEM /install validates the full machine-data tree before seeding absent profiles.json and hosts.bck; MSI does not write or delete ProgramData. Existing staging source locations remain valid because WiX sets the protected destination directly.

R5 verification uses the installer script without an artifacts argument, JSON/XML parsing and Windows PowerShell 5.1 syntax parsing. Evidence lives in .tmp/long-horizon/pre-switch-fixes/r5-installer-source.log, r5-parse.log and r5-executor.md. Package builds and ICE checks remain unavailable pending WiX tooling; no downloads or shared builds are authorized for this executor.

The expanded VM matrix covers hostile data trees, atomic replacement during controller startup, corrupt configuration, post-swap write failure, environmental reload failure, saturation/expiry, child identity, hosts restoration, audit loss, service-specific Windows Update, strict boot/crash DNS/DHCP denial and startup/reconnect/IPv6. Every live case remains pending. Unsigned prerelease artifacts, code signing, independent review and the absent binary update feed remain adoption limits; source fixes do not establish switch readiness.

## R9B bounded source verification

- [x] Source-confirmed findings 8 and 10; replaced controller `/install` elevation with existing-service start-only repair and separated ordinary user logs from privileged storage.
- [x] Added pure decision and caller-wiring regression cases in LifecycleHardeningTests.cs; cases are authored, not executed.
- [x] Preserved finding 3 rejection and documented clean TinyWall-only install versus legacy SecureWall recovery, evidence retention and residual MSI UI limitation.
- [ ] Run R9B regressions and native Debug/Release builds after all writers stop and manager releases shared verification.
- [ ] Run authorized disposable-VM UAC/SCM/logging/legacy-MSI cases, installer/signing checks and power-loss gates. Existing unchecked broad rounds above remain unverified by this executor.

See root r9b-plan.md and r9b-executor.md for ownership and exact handoff. No agents, model processes, host changes, installs or Git publication were used by R9B.
