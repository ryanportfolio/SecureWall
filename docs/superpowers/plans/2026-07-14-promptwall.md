# PromptWall Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fork TinyWall 3.5.1 into PromptWall and add secure, deduplicated Allow-outgoing/Ignore notifications for newly default-blocked outbound applications and services.

**Architecture:** Keep TinyWall's LocalSystem service, persistent/boot-time WFP filters, weighted rule precedence, transactional reloads, and unprivileged tray controller. Add a service-owned prompt queue keyed by opaque tokens, enrich WFP drop events with event 5157 process/service context, and render a non-activating WinForms popup from the controller.

**Tech Stack:** C# 9, .NET Framework 4.8, Windows Forms, Windows Filtering Platform P/Invoke, Windows Event Log, named pipes, SDK-style projects, dependency-free `net9.0` console test harness.

---

### Task 1: Import and identify the GPLv3 fork

**Files:**
- Create from pinned upstream: `TinyWall/`, `pylorak.Utilities/`, `pylorak.Windows/`, `pylorak.Windows.Services/`, `pylorak.Windows.WFP/`, `Microsoft.Samples/`, `MsiSetup/`, `TinyWall.sln`, `.editorconfig`, `Changelog.txt`
- Replace: `LICENSE`
- Create: `UPSTREAM.md`
- Modify: `README.md`
- Modify: `TinyWall/TinyWall.csproj`
- Modify: `TinyWall/TinyWallService.cs`
- Modify: `TinyWall/TinyWallDoctor.cs`

- [ ] **Step 1: Copy the exact upstream files**

Copy from commit `1df71b146d01d734d5b5a45a814b29e6a073f4d0`, excluding its `.git` directory and GitHub workflow files. Record the source URL, commit, version `3.5.1`, and import date in `UPSTREAM.md`.

- [ ] **Step 2: Apply licensing and attribution**

Use upstream `LICENSE.txt` as root `LICENSE`. State in `README.md` that PromptWall is a modified GPLv3 fork, retain Károly Pados's copyright, and mark PromptWall changes dated 2026-07-14.

- [ ] **Step 3: Establish distinct runtime identity**

Set assembly/product/title to `PromptWall`, service name to `PromptWall`, display name to `PromptWall Service`, controller pipe to `PromptWallController`, scheduled task to `PromptWall Controller`, and use a new WFP provider GUID. Preserve internal namespace names initially to minimize security-sensitive churn.

- [ ] **Step 4: Verify import identity**

Run:

```powershell
rg -n "<Product>TinyWall|<AssemblyTitle>TinyWall|SERVICE_NAME = \"TinyWall\"|new\(\"TinyWallController\"\)" TinyWall
```

Expected: no runtime-identity matches.

### Task 2: Add a dependency-free test harness

**Files:**
- Create: `tests/PromptWall.Core.Tests/PromptWall.Core.Tests.csproj`
- Create: `tests/PromptWall.Core.Tests/Program.cs`
- Create: `tests/PromptWall.Core.Tests/AssertEx.cs`

- [ ] **Step 1: Create the console test project**

Target `net9.0`, enable nullable reference types, and link pure production files under `TinyWall/Prompting/` instead of referencing the `net48` WinForms project.

- [ ] **Step 2: Add a minimal deterministic runner**

`Program.cs` executes named `Action` tests, prints `PASS <name>` for success, prints exception details for failure, and exits nonzero if any test fails.

- [ ] **Step 3: Prove the empty harness runs**

Run:

```powershell
dotnet run --project tests/PromptWall.Core.Tests/PromptWall.Core.Tests.csproj
```

Expected: exit 0 with a summary containing `0 failed`.

### Task 3: Build prompt identities and outbound-only allow policy using TDD

**Files:**
- Create: `TinyWall/Prompting/PromptIdentity.cs`
- Create: `TinyWall/Prompting/BlockedConnectionPrompt.cs`
- Create: `TinyWall/Prompting/PromptAllowPolicy.cs`
- Modify: `tests/PromptWall.Core.Tests/Program.cs`

- [ ] **Step 1: Write failing identity tests**

Cover case-insensitive executable keys, package SID precedence, exact service name plus path, sorted ambiguous service names, and rejection of empty identity.

- [ ] **Step 2: Run RED**

Run the test project. Expected: compile failure because `PromptIdentity` does not exist.

- [ ] **Step 3: Implement minimal immutable identity records**

Expose `Kind`, canonical `Key`, executable path, optional package SID, optional exact service name, and read-only ambiguous service names.

- [ ] **Step 4: Run GREEN**

Expected: identity tests pass.

- [ ] **Step 5: Write failing allow-policy tests**

Verify executable, package, and exact-service identities produce outbound TCP/UDP wildcard ports while all listener fields remain null; ambiguous services and missing paths are rejected.

- [ ] **Step 6: Run RED, implement, then run GREEN**

Implement a pure `PromptAllowPolicy` DTO/factory. Production integration later converts it to TinyWall `FirewallExceptionV3` and `TcpUdpPolicy`.

### Task 4: Implement bounded prompt queue and token lifecycle using TDD

**Files:**
- Create: `TinyWall/Prompting/IClock.cs`
- Create: `TinyWall/Prompting/PromptQueue.cs`
- Modify: `tests/PromptWall.Core.Tests/Program.cs`

- [ ] **Step 1: Write failing queue tests**

Cover first enqueue, three-second deduplication, bounded capacity 32, two-minute expiry, five-minute Ignore cooldown, single-use Allow token, unknown token, ambiguous token rejection, and deterministic oldest-first delivery.

- [ ] **Step 2: Run RED**

Expected: compile failure because `PromptQueue` does not exist.

- [ ] **Step 3: Implement minimal queue**

Use a lock-protected dictionary by token, dictionary by identity key, FIFO token queue, injected clock, and immutable snapshots. Queue overflow drops the new candidate.

- [ ] **Step 4: Run GREEN and refactor**

Expected: all queue tests pass with no warnings.

### Task 5: Track only default-block WFP runtime IDs

**Files:**
- Create: `TinyWall/Prompting/PromptableFilterSet.cs`
- Modify: `TinyWall/TinyWallService.cs`
- Modify: `tests/PromptWall.Core.Tests/Program.cs`

- [ ] **Step 1: Write failing filter-set tests**

Verify atomic replacement, membership, old-set removal, and thread-safe snapshots.

- [ ] **Step 2: Run RED, implement, run GREEN**

Implement `PromptableFilterSet` with an immutable `HashSet<ulong>` snapshot replaced under a lock.

- [ ] **Step 3: Integrate filter IDs**

Change `InstallWfpFilter` to return both persistent and boot-time runtime IDs. During `ConstructFilter`, collect IDs only when `RuleDef.Action == Block` and `RuleDef.Weight == DefaultBlock`. Publish the collected set only after the surrounding WFP transaction commits.

- [ ] **Step 4: Static verification**

Search all `InstallWfpFilter` callers and verify built-in blocklist, user block, raw-socket block, port-scan, and WSL block paths cannot enter the promptable set.

### Task 6: Capture and restore audit policy safely

**Files:**
- Create: `TinyWall/AuditPolicyLease.cs`
- Modify: `TinyWall/FirewallLogWatcher.cs`
- Modify: `TinyWall/FirewallLogEntry.cs`

- [ ] **Step 1: Add parser tests before parser code**

Extract pure event-field parsing into a method that accepts a name/value dictionary. Test event 5157 outbound direction, decimal PID, protocol, tuple, and 64-bit `FilterRTID`.

- [ ] **Step 2: Run RED, implement parser, run GREEN**

Parse by event-data names rather than positional indexes to tolerate Windows 11's extra fields.

- [ ] **Step 3: Implement audit lease**

P/Invoke `AuditQuerySystemPolicy`, `AuditSetSystemPolicy`, and `AuditFree`. Snapshot the Filtering Platform Connection subcategory flags, OR in failure auditing, and restore the exact snapshot once on dispose. Never disable a flag that was enabled before PromptWall started.

- [ ] **Step 4: Integrate watcher lifecycle**

Watch only 5157 for prompt enrichment. Keep TinyWall learning-mode semantics by requesting success auditing only while Learning mode is active. Log failure and continue with degraded service attribution rather than weakening policy.

### Task 7: Correlate WFP drops with service PIDs using TDD

**Files:**
- Create: `TinyWall/Prompting/DropCandidate.cs`
- Create: `TinyWall/Prompting/DropCorrelator.cs`
- Create: `TinyWall/Prompting/ServiceAttribution.cs`
- Modify: `tests/PromptWall.Core.Tests/Program.cs`

- [ ] **Step 1: Write failing correlation tests**

Cover exact filter ID/path/protocol/tuple/time matching, mismatch rejection, one-second tolerance, one-service attribution, normal executable attribution, multiple-service ambiguity, UWP package precedence, and `svchost.exe` fail-closed behavior when PID data is missing.

- [ ] **Step 2: Run RED, implement, run GREEN**

Keep correlator pure. Inject service names already resolved for a PID; Windows SCM enumeration stays outside this class.

- [ ] **Step 3: Connect event sources**

WFP callback creates candidates only for promptable outbound classify drops. Event 5157 enriches matching candidates. Candidates finalize after enrichment or a short deadline and enter `PromptQueue`.

### Task 8: Add tokenized controller protocol using TDD

**Files:**
- Modify: `TinyWall/MessageType.cs`
- Modify: `TinyWall/Message.cs`
- Modify: `TinyWall/Controller.cs`
- Modify: `TinyWall/TinyWallService.cs`
- Modify: `TinyWall/SerializationHelper.cs` or its source-generation context declaration

- [ ] **Step 1: Write serialization round-trip tests**

Test request/response payloads for `READ_PENDING_PROMPTS`, `DISMISS_PROMPT`, and `ALLOW_PROMPT`, including immutable token and prompt details.

- [ ] **Step 2: Run RED, implement messages, run GREEN**

Assign read, unprivileged-dismiss, and privileged-allow values in the existing message security ranges.

- [ ] **Step 3: Implement service handlers**

Read returns queued snapshots. Dismiss consumes token and adds cooldown. Allow validates password state and token, derives subject from service-owned identity, creates only outbound TCP/UDP fields, merges into active profile, saves configuration, reloads filters transactionally, then consumes token.

- [ ] **Step 4: Test failure paths**

Unknown, expired, consumed, ambiguous, locked, and save/reload failure cases must return error or locked responses and remain blocked.

### Task 9: Add non-activating bottom-right popup

**Files:**
- Create: `TinyWall/Prompting/BlockedConnectionPopup.cs`
- Create: `TinyWall/Prompting/BlockedConnectionPopup.Designer.cs`
- Create: `TinyWall/Prompting/PromptDisplayCoordinator.cs`
- Modify: `TinyWall/TinyWallController.cs`
- Modify: `TinyWall/Resources/Messages.resx`
- Modify localized `TinyWall/Resources/Messages.*.resx` only where required for safe fallback behavior

- [ ] **Step 1: Test display coordinator before UI**

Test one-visible-at-a-time sequencing, Allow success, Allow failure retaining block, Ignore, close-as-ignore, timeout-as-ignore, and controller shutdown disposal.

- [ ] **Step 2: Run RED, implement coordinator, run GREEN**

- [ ] **Step 3: Implement popup form**

Use `FormBorderStyle.None`, `TopMost`, `ShowInTaskbar = false`, `ShowWithoutActivation`, accessible button names, DPI-aware layout, and `Screen.FromPoint(Cursor.Position).WorkingArea` bottom-right placement. Show app/service identity, path, destination, protocol, and warnings. Disable Allow for ambiguous services.

- [ ] **Step 4: Integrate polling**

Poll every 750 ms from the existing WinForms service timer without blocking the UI. Enqueue unseen tokens into the display coordinator.

- [ ] **Step 5: Visual/manual verification without firewall activation**

Add a debug-only command-line switch that feeds synthetic prompt data to the popup without installing the service or WFP filters. Verify placement, scaling, keyboard navigation, screen reader names, and queue transition locally.

### Task 10: Configure project knowledge and operator docs

**Files:**
- Modify: `CLAUDE.md`
- Modify: `.claude/reference/architecture.md`
- Modify: `.claude/reference/commands.md`
- Modify: `.claude/reference/deployment.md`
- Modify: `.claude/reference/pitfalls.md`
- Modify: `.claude/reference/tech-stack.md`
- Modify: `.claude/settings.json`
- Modify: `.claude/skills/applying-best-practices/SKILL.md`
- Modify: `README.md`
- Create: `docs/SECURITY.md`
- Create: `docs/TESTING.md`

- [ ] **Step 1: Finish starter initialization**

Remove all `FILL IN` markers, identify profile as Windows desktop/service, document local build/test authority, and state that install/firewall activation requires explicit local-console approval.

- [ ] **Step 2: Document safety and recovery**

README and security docs must warn never to install over remote-only access, explain default deny, distinguish Ignore from Allow, explain GPLv3 provenance, and give uninstall/recovery steps.

- [ ] **Step 3: Sync Codex adapters**

Run:

```powershell
node .claude/scripts/sync-codex-skills.mjs --write
```

Expected: generated adapters match active project skills.

### Task 11: Restore, build, and verify without activating the firewall

**Files:**
- Generated only: `**/obj/`, `**/bin/` (ignored)

- [ ] **Step 1: Restore declared NuGet dependencies**

Run after approval:

```powershell
dotnet restore TinyWall/TinyWall.csproj
```

Expected: exit 0.

- [ ] **Step 2: Run full pure test suite**

```powershell
dotnet run --project tests/PromptWall.Core.Tests/PromptWall.Core.Tests.csproj
```

Expected: all named tests pass, `0 failed`.

- [ ] **Step 3: Build production project**

```powershell
dotnet build TinyWall/TinyWall.csproj -c Debug --no-restore
```

Expected: exit 0 with zero errors.

- [ ] **Step 4: Run source and policy checks**

Verify no `FILL IN`, no runtime `TinyWall` service/pipe/provider identity, no Allow path populating listener ports, all prompt tokens validated server-side, all promptable IDs originate only from default block rules, and GPL/upstream notices exist.

- [ ] **Step 5: Run synthetic popup mode**

Launch only the debug popup switch. Do not install the service and do not register WFP filters. Capture visual results for bottom-right placement and actions.

- [ ] **Step 6: Produce manual VM matrix**

Document exact local-console tests for executable, UWP, exact service, ambiguous service, Ignore, timeout, explicit block, password lock, reboot, controller exit, service restart, and uninstall. Mark real WFP behavior unverified until this matrix runs on an expendable Windows VM.

### Task 12: Requirement-by-requirement completion audit

**Files:**
- Modify: `docs/TESTING.md`

- [ ] **Step 1: Re-read design and user objective**

Map every explicit requirement and security invariant to code, automated test, build result, or manual VM evidence.

- [ ] **Step 2: Inspect the complete diff**

Use `rtk git diff` for overview and native `git diff --check` for exact whitespace/error verification. Preserve all unrelated user changes.

- [ ] **Step 3: Run fresh verification**

Re-run tests, production build, source checks, and synthetic popup verification in the same completion turn.

- [ ] **Step 4: Report only proven status**

Do not claim the firewall itself is production-verified unless the real WFP/manual VM matrix has passed. Do not install, activate, commit, push, or open a PR unless separately authorized.
