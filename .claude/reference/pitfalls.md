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

- MSI defaults are Program Files payload in `data-defaults`; do not reintroduce MSI ProgramData creation, copying or deletion before the shared guard. SYSTEM seeds only absent defaults after validating the full tree. Reject unsafe existing trees without changing them.
- A controller may start while a protected temporary policy file disappears during replacement. Revalidate its parent before tolerating absence; root, ACL and reparse errors must still reject access.
- A persistent write can replace the file and then throw during the installed-file flush. Compensate on attempted writes, retain recovery evidence on failure, and withdraw runtime grants when recovery fails.
- Content flushing and process-failure recovery do not prove power-loss ordering of journal rename/delete or physical-media durability. Do not describe these as verified crash-safe filesystem commits.
- Existing corrupt or unreadable configuration must fail closed; only confirmed absence selects defaults. Hosts restoration failure must remain visible and prevent successful teardown; absent original backup alone is valid.
- Windows_Update must retain its exact `wuauserv` service rule without an executable-wide `svchost.exe` web allow. Actual update functionality, strict boot DNS/DHCP blocking and all failure paths remain VM checks.

## 2026-09-12: controller repair and user logs

Controller recovery must not call SYSTEM-only `/install`. Only the installing plus LocalSystem path creates registration and configures health; other callers validate the existing protected image, LocalSystem account, dedicated service type and pending-deletion state before start. Nonadmin elevation uses the system directory's sc.exe, then observes Running through SCM. Missing registration requires MSI recovery (full removal and fresh install).

User controller logs use LocalApplicationData/SecureWall/logs. Actual privileged/noninteractive/impersonating context and service/installer roles force guarded machine logs, with no user fallback. Do not use the log label alone as a privilege decision. Legacy writable ProgramData trees are intentionally rejected; preserve them as evidence and require reviewed local-console recovery. MSI stderr diagnostics do not guarantee a specific installer dialog.
