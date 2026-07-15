# Live Network Activity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make SecureWall's existing Connections window a live, explicit view of observed blocked, allowed, active, and listening network activity.

**Architecture:** Keep LocalSystem as telemetry authority and reuse its bounded WFP event log. Normalize firewall decision events in a pure helper, then merge those observations with Windows TCP/UDP endpoint tables in the existing unprivileged WinForms window. A UI timer refreshes once per second; it never installs filters or changes policy.

**Tech Stack:** C# 9, .NET Framework 4.8 WinForms, existing WFP net-event callback, existing NetStat wrappers, dependency-free net9.0 pure tests.

---

### Task 1: Exact status semantics

**Files:**
- Create: `TinyWall/Prompting/NetworkActivityStatus.cs`
- Modify: `tests/SecureWall.Core.Tests/Program.cs`

- [x] **Step 1: Write the failing test**

Add tests proving an observed permit becomes `Allowed`, an observed drop becomes `Blocked`, and listening remains explicitly local-only:

```csharp
AssertEx.Equal(NetworkActivityStatus.Allowed, NetworkActivityStatusClassifier.FromFirewallDecision(true));
AssertEx.Equal(NetworkActivityStatus.Blocked, NetworkActivityStatusClassifier.FromFirewallDecision(false));
AssertEx.Equal("Listening (local endpoint)", NetworkActivityStatusClassifier.ToDisplayText(NetworkActivityStatus.Listening));
```

- [x] **Step 2: Run test to verify it fails**

Run: `rtk test dotnet run --project tests\SecureWall.Core.Tests\SecureWall.Core.Tests.csproj`

Expected: compile failure because `NetworkActivityStatusClassifier` is undefined.

- [x] **Step 3: Write minimal implementation**

```csharp
internal enum NetworkActivityStatus { Allowed, Blocked, Listening }

internal static class NetworkActivityStatusClassifier
{
    internal static NetworkActivityStatus FromFirewallDecision(bool allowed) =>
        allowed ? NetworkActivityStatus.Allowed : NetworkActivityStatus.Blocked;

    internal static string ToDisplayText(NetworkActivityStatus status) => status switch
    {
        NetworkActivityStatus.Allowed => "Allowed",
        NetworkActivityStatus.Blocked => "Blocked",
        NetworkActivityStatus.Listening => "Listening (local endpoint)",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet run --project tests\SecureWall.Core.Tests\SecureWall.Core.Tests.csproj`

Expected: all tests pass.

### Task 2: Show observed allowed decisions

**Files:**
- Modify: `TinyWall/ConnectionsForm.cs`
- Modify: `TinyWall/ConnectionsForm.resx`

- [x] **Step 1: Normalize both decision families**

Change log filtering so `ALLOWED_*` entries normalize to `Allowed` and are retained when the existing active checkbox is enabled; `BLOCKED_*` entries normalize to `Blocked` and remain controlled by the blocked checkbox. Keep the five-minute bounded view and existing duplicate coalescing.

```csharp
bool include = newEntry.Event == EventLogEvent.ALLOWED
    ? chkShowActive.Checked
    : chkShowBlocked.Checked;
if (include && !filteredLog.Any(oldEntry => oldEntry.Equals(newEntry, false)))
    filteredLog.Add(newEntry);
```

- [x] **Step 2: Render unambiguous status text**

Use `Allowed`, `Blocked`, and `Listening (local endpoint)` for decision/listener rows. Preserve exact TCP states such as `Established` for OS endpoint-table rows, because those are transport states rather than firewall decisions.

- [x] **Step 3: Clarify visible labels**

Set the neutral-resource checkbox text to `Show active + allowed` and form title to `SecureWall Network Activity`. Keep localized resources unchanged so they continue to fall back safely.

### Task 3: Make the view live

**Files:**
- Modify: `TinyWall/ConnectionsForm.cs`

- [x] **Step 1: Add a contained UI timer**

```csharp
private readonly Timer RefreshTimer;

this.RefreshTimer = new Timer(this.components) { Interval = 1000 };
this.RefreshTimer.Tick += (_, _) => UpdateList();
```

- [x] **Step 2: Scope timer lifetime to the form**

Start after the initial load and stop before form teardown. WinForms timer ticks run serially on the UI thread, preventing overlapping refreshes.

- [x] **Step 3: Preserve manual refresh and filters**

Keep the existing Refresh button, column sorting, saved window state, and filter settings.

### Task 4: Verification and handoff

**Files:**
- Modify: `docs/TESTING.md`
- Modify: `.claude/reference/architecture.md`

- [x] **Step 1: Run automated checks**

Run pure tests, Debug/Release native MSBuild, `/protocolselftest`, and normal-token `/pipeintegrationtest`. Expected: all pass; inherited warnings may remain.

- [x] **Step 2: Verify UI resources without enforcement**

Compile the neutral resources and inspect the status contract without installing SecureWall or touching WFP. Do not claim live firewall behavior from static/resource verification.

- [x] **Step 3: Refresh packaging**

Build `SecureWall.NetworkProbe`, stage MSI sources, and regenerate the hash-manifested VM/manual-test bundle.

- [x] **Step 4: Document manual acceptance**

Require the tester to observe one allowed flow, one blocked flow, and one local listener in the live window, and verify that `Listening` does not claim external reachability.
