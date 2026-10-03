# Pitfalls

> Accumulated project-specific gotchas. Dated entries, newest at the bottom. If this file exceeds ~200 lines, split by area (`pitfalls-<area>.md`) and update the CLAUDE.md index.

## Starter safety

This starter must not ship maintainer-only checkout paths, private workflow
rules, secrets, or local-machine assumptions. Put those in untracked personal
instructions or in a private fork-specific memory file instead.

Worktree changes are isolated. Before claiming a template change is available
somewhere else, verify the exact branch or checkout the user asked about. Do not
merge, pull into another checkout, or touch paths outside the current workspace
unless the user explicitly asks in the current session.

## 2026-07-14: SecureWall safety and build

- `dotnet build` fails at `ResolveComReference`; use Visual Studio Build Tools' .NET Framework `MSBuild.exe`.
- In sandboxed sessions, `dotnet` may use another account's empty global package cache. Never commit a machine-specific cache path; set `NUGET_PACKAGES` only as a local workaround.
- A real firewall install can sever remote access. Synthetic preview/build success is not WFP verification.
- WFP callbacks have no PID. Correlate only exact 5157 filter/path/protocol/tuple/time matches; `svchost.exe` without exact service attribution must remain non-allowable.
- Publish promptable filter IDs only after transaction commit and only in Normal mode.
- Do not restore audit flags to NONE blindly. Lease the original flags and restore that exact snapshot.
- Do not use `new TcpUdpPolicy(true)` for prompt allows; it opens inbound listeners. Set only remote TCP/UDP connect ports.
- Prompt allow IPC accepts only an opaque service-issued token. Never accept a controller-supplied path/SID/service as authority.
- SecureWall and TinyWall should not be installed together; both manage host firewall behavior.
- Never swallow default-block registration failures. Both persistent and boot-time copies are required so the enclosing WFP transaction rolls back fail-closed.
- Network Activity status wording is evidence-scoped: WFP permit/drop observations are `Allowed`/`Blocked`; an open socket is `Listening (local endpoint)` and is not proof of external reachability.

## 2026-09-07: Hardening branch

- Checkouts under long paths (worktrees below `AppData\Local\Temp\claude\...`) exceed MAX_PATH for MSBuild and the test harness; map the tree to a drive letter first (`subst W: "<path>"`) and build from there.
- Never `git clean` this tree; delete `bin/` and `obj/` by path instead.
- Superseded on 2026-09-12: the baseline is strict external deny at `DefaultBlock - 2`, with no DHCP/DNS recovery permits. Service loss can prevent address renewal and name resolution; retain local-console recovery.
- `AuditPolicyLease` marks a subcategory journaled only after the registry write succeeds; do not reorder the write and the `AuditSetSystemPolicy` call.
- The Allow existence recheck applies only to Win32-form paths; `System` and unmapped NT-form subjects skip it on purpose.

## 2026-09-12: pre-switch fixes

- MSI defaults are Program Files payload in `data-defaults`; do not reintroduce MSI ProgramData creation, copying or deletion before the shared guard. After validating the full tree, SYSTEM brings only the shipped data files (profiles.json, hosts.bck) to the data-defaults content; config, pwd, hosts.orig and journals are never written. Reject unsafe existing trees without changing them.
- A controller may start while a protected temporary policy file disappears during replacement. Revalidate its parent before tolerating absence; root, ACL and reparse errors must still reject access.
- A persistent write can replace the file and then throw during the installed-file flush. Compensate on attempted writes, retain recovery evidence on failure, and withdraw runtime grants when recovery fails.
- Content flushing and process-failure recovery do not prove power-loss ordering of journal rename/delete or physical-media durability. Do not describe these as verified crash-safe filesystem commits.
- Existing corrupt or unreadable configuration must fail closed; only confirmed absence selects defaults. Hosts restoration failure must remain visible and prevent successful teardown; absent original backup alone is valid.
- Windows_Update must retain its exact `wuauserv` service rule without an executable-wide `svchost.exe` web allow. Actual update functionality, strict boot DNS/DHCP blocking and all failure paths remain VM checks.

## 2026-09-12: controller repair and user logs

Controller recovery must not call SYSTEM-only `/install`. Only the installing plus LocalSystem path creates registration and configures health; other callers validate the existing protected image, LocalSystem account, dedicated service type and pending-deletion state before start. Nonadmin elevation uses the system directory's sc.exe, then observes Running through SCM. Missing registration requires MSI recovery (full removal and fresh install).

User controller logs use LocalApplicationData/SecureWall/logs. Actual privileged/noninteractive/impersonating context and service/installer roles force guarded machine logs, with no user fallback. Do not use the log label alone as a privilege decision. Legacy writable ProgramData trees are intentionally rejected; preserve them as evidence and require reviewed local-console recovery. MSI stderr diagnostics do not guarantee a specific installer dialog.

## 2026-09-29: no deliberate crash experiments

2026-09-29: reviewers and tests on this machine must never deliberately crash processes (unhandled thread exceptions, FailFast, Debug.Assert) to test exit semantics; Visual Studio is the registered JIT debugger, so each crash opens blocking dialogs on the owner's desktop. Reason from documentation instead.

## 2026-09-30: self-test and preview switches are Debug-only

2026-09-30: `/protocolselftest`, `/pipeintegrationtest` and `/promptpreview` exist only in Debug builds. A Release `SecureWall.exe` given them starts the real controller, shows the machine-data-guard dialog on the desktop and writes Application event 1000. Run them against `bin\Debug` only. From Git Bash, set `MSYS_NO_PATHCONV=1` or the leading `/` is rewritten into a file path and the exe starts with an unknown argument.

## 2026-09-30: core test registration

2026-09-30: the core test harness has no discovery. A new test class runs only after its `Cases` are concatenated in `tests/SecureWall.Core.Tests/Program.cs`, and product sources it exercises need a `Compile Include` link in the test csproj; an unregistered class compiles and is silently skipped. Parallel branches collide in both files: when merging, keep every registration and link, then confirm the passing count equals the sum of both sides.

## 2026-10-02: WFP engine options and dynamic sessions

- `FwpmEngineSetOption0` fails with `FWP_E_DYNAMIC_SESSION_IN_PROGRESS` (0x8032000B) on a `FWPM_SESSION_FLAG_DYNAMIC` handle. The runtime session is dynamic, so net-event options go through `ApplyNetEventOptions`, which opens a short-lived ordinary session; the service restores the values it found at start when it stops, because they are shared with other WFP consumers and persist across reboots. Option reads, net-event subscription, transactions and non-persistent filter adds work on the dynamic handle. v0.4.0 shipped with the option set on the dynamic handle and crashed every service start on a real install; build, tests and preview never open a WFP session, so only an install shows this class of bug.
- To see setup pages without installing, run `tools\installer\Preview-SetupPages.ps1` (commands.md, "MSI packages"); it never runs the execute sequence. Running the real MSI with TinyWall installed only reaches the failure page, and UI Automation from the agent session did not find msiexec's dialogs (cause unconfirmed). Captures of a window created offscreen come out half white because its compositor surface is never painted, and plain WM_PRINT drops MSI's transparent text, so the tool fades pages in place instead of moving them. Give msiexec a backslash path; a forward-slash path fails with "This installation package could not be opened". From Git Bash, quote Windows paths passed to PowerShell scripts; unquoted backslashes are stripped.
