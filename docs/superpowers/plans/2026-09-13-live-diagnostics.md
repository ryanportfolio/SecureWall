# Live diagnostics implementation plan

> For agentic workers: execute bounded steps with the available Long Horizon executor and independent auditor tools. No installation, deliberate fault injection, or Git publication is part of this implementation step.

**Goal:** Let the owner collect useful evidence during normal use on a physical PC without treating missing observations as successful firewall tests.

**Architecture:** A bounded structured service journal records lifecycle and enforcement outcomes without network payloads or secret-bearing objects. A read-only PowerShell collector packages the journal, selected Windows evidence and a coverage report. Collection failures and observation gaps remain explicit.

**Tech stack:** Existing .NET Framework 4.8 service, pure .NET test harness and Windows PowerShell 5.1. No new dependencies.

## 1. Runtime journal

- [ ] Add a persisted `EnableDiagnosticLogging` checkbox to Settings, off by default. Apply it only after a successful configuration commit. Disabling stops new detailed records and retains existing evidence; ordinary error logging remains available.
- [ ] Add a testable bounded JSONL journal under `TinyWall/Prompting` and its guarded adapter under `TinyWall`.
- [ ] Record UTC time, process/run identity, sequence, monotonic uptime, event, result and explicit numeric counters. Use fixed field names and bounded scalar values. Never serialize configuration, credentials, prompt tokens, user paths, remote endpoints or exception messages.
- [ ] Keep all disk work off WFP callbacks. Bound queue, file size, retained generations and individual records. Include dropped-record and write-failure counters in subsequent health records; diagnostic failure must not affect policy outcomes or locks.
- [ ] Use the existing validated machine-data boundary with no user-directory fallback for SYSTEM. Record startup, baseline registration outcome, policy save/apply/rollback/publish stages, fail-closed attempts/results, normal shutdown, audit health and aggregate observed allow/drop counts. A missing shutdown record is only an observation gap.
- [ ] Exercise rotation, failure isolation, concurrency/overflow, serialization privacy and lifecycle evidence in the pure harness. The source adapter must build with the native .NET Framework toolchain.

## 2. Read-only collection and interpretation

- [ ] Add `tools/diagnostics/Collect-SecureWallDiagnostics.ps1` and a README. Create a new output directory and ZIP with hashes, capture start/end, exact binary identities, OS/build, SecureWall service state, recent relevant SCM/Application errors and the structured journal.
- [ ] Bound duration, event counts, source bytes and output size; reject unsafe/reparse log inputs. Do not copy encrypted policy, recovery files, hosts contents, passwords or API settings. Make sensitive extended WFP/network inventories explicit opt-in and disclose their contents.
- [ ] Record each collector command's success/failure; never alter audit policy, service, firewall rules, registry or hosts. Do not automatically upload anything.
- [ ] Produce a coverage report: observed policy commits/recovery, startup/shutdown and audit availability versus untested boot/crash leakage, hostile ACL/SYSTEM scenarios, install fault rollback and power-loss durability. Counters and successful WFP API calls do not prove packet delivery or absence of leakage.
- [ ] Add fixture tests for sanitization, bounded inputs, gap detection, missing/denied files and partial collection. Run the read-only collector against the host only if useful, label SecureWall absence correctly and preserve its existing firewall configuration.

## 3. Integrated verification and delivery

- [ ] Run `dotnet run --project tests/SecureWall.Core.Tests/SecureWall.Core.Tests.csproj --no-restore`.
- [ ] Build Debug and Release with Visual Studio Build Tools MSBuild on `TinyWall/TinyWall.csproj`, `/t:Build /p:Configuration=<configuration> /p:RestorePackages=false /v:minimal`.
- [ ] Run safe Debug `/protocolselftest`, PowerShell collector fixture tests, installer source checks, and `git diff --check`.
- [ ] Fresh independent audit checks changed code, privacy, bounds, protected logging paths, policy invariance and evidence labels. Resolve actionable findings and repeat only affected checks.
- [ ] Document a staged local-console trial and a recovery preparation step. Installation and deliberate outage tests require a separate concrete deployment step; no host activation occurs while implementing diagnostics.

## Acceptance limits

Normal-use logs can establish that particular code paths ran and what they reported. They cannot replace external packet observation during service absence, prove unused failure paths, make unsigned artifacts signed, or establish physical-media power-loss guarantees. Do not call an unobserved case a pass.
