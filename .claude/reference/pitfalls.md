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

## 2026-07-14 — PromptWall safety and build

- `dotnet build` fails at `ResolveComReference`; use Visual Studio Build Tools' .NET Framework `MSBuild.exe`.
- In sandboxed sessions, `dotnet` may use another account's empty global package cache. Never commit a machine-specific cache path; set `NUGET_PACKAGES` only as a local workaround.
- A real firewall install can sever remote access. Synthetic preview/build success is not WFP verification.
- WFP callbacks have no PID. Correlate only exact 5157 filter/path/protocol/tuple/time matches; `svchost.exe` without exact service attribution must remain non-allowable.
- Publish promptable filter IDs only after transaction commit and only in Normal mode.
- Do not restore audit flags to NONE blindly. Lease the original flags and restore that exact snapshot.
- Do not use `new TcpUdpPolicy(true)` for prompt allows; it opens inbound listeners. Set only remote TCP/UDP connect ports.
- Prompt allow IPC accepts only an opaque service-issued token. Never accept a controller-supplied path/SID/service as authority.
- PromptWall and TinyWall should not be installed together; both manage host firewall behavior.
- Never swallow default-block registration failures. Both persistent and boot-time copies are required so the enclosing WFP transaction rolls back fail-closed.
- Network Activity status wording is evidence-scoped: WFP permit/drop observations are `Allowed`/`Blocked`; an open socket is `Listening (local endpoint)` and is not proof of external reachability.
