# SecureWall design

## Goal

Build a Windows 10/11 desktop firewall that keeps TinyWall 3.5.1's secure-by-default behavior and low overhead, while adding one narrowly scoped interaction: a bottom-right notification for a newly blocked outbound application or service with **Allow outgoing** and **Ignore** actions.

## Chosen foundation

SecureWall is a GPLv3 fork of TinyWall 3.5.1, pinned initially to upstream commit `1df71b146d01d734d5b5a45a814b29e6a073f4d0` from `pylorak/TinyWall`.

This is preferable to a clean-room implementation because TinyWall already handles boot-time filtering, IPv4/IPv6, raw sockets, WSL, UWP/AppContainers, local subnets, Windows services, temporary exceptions, installer recovery, blocklists, and Windows path translation. Reimplementing those details would create a much larger attack and regression surface.

SecureWall uses a distinct product name and preserves upstream copyright, source notices, and GPLv3 terms. The repository records the upstream commit and marks SecureWall modifications by date.

## TinyWall research summary

TinyWall 3.5.1 is not a packet-filtering driver and does not install kernel code. Its LocalSystem service configures Windows Filtering Platform (WFP) directly through the Base Filtering Engine (BFE). Its tray process is only the controller UI.

The service:

- registers a persistent WFP provider and persistent per-layer sublayers;
- creates both persistent and boot-time copies of filters;
- installs filter changes inside WFP transactions;
- assigns explicit weights so malware blocks, raw-socket policy, user blocks, user permits, default permits, and default blocks have deterministic precedence;
- blocks non-loopback traffic globally in Normal mode, then adds only explicit allows;
- subscribes to WFP net events for allowed and dropped classifications;
- persists configuration under the machine application-data directory;
- communicates with the tray controller over a named pipe;
- repairs service start/recovery settings and launches the controller at interactive logon.

The tray controller:

- polls service state every two seconds;
- manages exceptions and modes;
- shows the system-tray UI;
- has no enforcement responsibility, so closing it does not disable filtering.

Relevant primary sources:

- TinyWall source and build instructions: <https://github.com/pylorak/TinyWall>
- TinyWall feature model: <https://tinywall.pados.hu/features.php>
- WFP architecture: <https://learn.microsoft.com/windows/win32/fwp/about-windows-filtering-platform>
- WFP blocked-connection event 5157: <https://learn.microsoft.com/previous-versions/windows/it-pro/windows-10/security/threat-protection/auditing/event-5157>
- Windows filter-origin auditing: <https://learn.microsoft.com/windows/security/operating-system-security/network-security/windows-firewall/filter-origin-documentation>

## Security invariants

1. Normal mode remains default-deny for inbound and outbound non-loopback traffic.
2. Enforcement stays in the LocalSystem service and Windows WFP; the UI cannot enforce or bypass policy by itself.
3. Ignore performs no firewall-policy write.
4. A prompt is emitted only for an outbound drop caused by SecureWall's default-block filter, never for an explicit user block, malware blocklist, raw-socket block, Windows Service Hardening block, or another provider's block.
5. Allow is authorized with an opaque, single-use service-issued token. The controller cannot turn an arbitrary path string into an allow through the prompt endpoint.
6. Allow creates only an outbound TCP/UDP exception. It does not open listeners or inbound traffic.
7. UWP traffic is allowed by AppContainer SID, not by a mutable display name.
8. A Windows service is allowed by executable path plus service name only when attribution is unambiguous. `svchost.exe` is never allowed globally from an ambiguous prompt.
9. Explicit block rules retain higher weight than prompt-created allows.
10. Prompt queues are bounded, deduplicated, rate-limited, expiring, and fail closed.
11. Password lock applies to Allow exactly as it applies to other privileged policy changes.
12. Filter installation and configuration persistence remain transactional or atomic to the same extent as upstream.
13. Uninstall removes SecureWall's WFP provider, filters, service, scheduled task, and audit-policy lease without disabling Windows Firewall.

## Prompt event pipeline

### 1. Identify promptable WFP filters

When constructing filters, the service records runtime filter IDs only for rules with all of these properties:

- action is Block;
- weight is `DefaultBlock`;
- mode is Normal;
- layer is an outbound-capable ALE authorization layer.

Both the persistent and boot-time runtime IDs are recorded. The set is replaced atomically after each successful WFP transaction.

### 2. Observe drops

The existing WFP net-event subscription remains the authoritative signal that WFP blocked traffic. A candidate must be a classify-drop event whose:

- runtime filter ID is in the current promptable set;
- direction is outbound;
- application identity is nonempty and not SecureWall itself;
- mode is still Normal.

WFP supplies the application ID, AppContainer SID when applicable, tuple, protocol, timestamp, and blocking filter ID. It does not supply a process ID.

### 3. Enrich service identity

Windows Security event 5157 supplies `ProcessID`, application path, direction, network tuple, protocol, and `FilterRTID`. SecureWall watches outbound 5157 failures and correlates them to recent WFP candidates by runtime filter ID, application path, protocol, tuple, and a short timestamp window.

The watcher leases only failure auditing for the `Filtering Platform Connection` subcategory. It first snapshots the existing system audit flags using `AuditQuerySystemPolicy`, adds failure auditing without removing existing flags, and restores exactly the captured flags during clean shutdown/uninstall. If auditing cannot be enabled, application and UWP prompts still work; service attribution safely degrades to ambiguous and cannot blanket-allow `svchost.exe`.

For a correlated PID, the service enumerates services hosted by that PID:

- one service: use `ServiceSubject(executablePath, serviceName)`;
- zero services: use executable identity;
- multiple services: mark ambiguous; show names for context but disable Allow.

### 4. Deduplicate and queue

Identity keys are case-insensitive and use:

- `package:<app-container-sid>`;
- `service:<canonical-exe>|<service-name>`;
- `exe:<canonical-exe>`;
- `ambiguous-service:<canonical-exe>|<sorted-service-names>`.

Defaults:

- maximum pending prompts: 32;
- event coalescing window: 3 seconds;
- Ignore cooldown per identity: 5 minutes;
- token lifetime: 2 minutes;
- only one visible popup at a time.

When full, the queue drops the newest prompt and continues blocking. No queue condition may weaken filtering.

### 5. Controller protocol

Three typed messages extend the existing JSON-over-named-pipe protocol:

- `READ_PENDING_PROMPTS`: read-only; returns deliverable prompts.
- `DISMISS_PROMPT`: consumes a token and begins cooldown; changes no firewall policy.
- `ALLOW_PROMPT`: privileged; consumes a token and applies the stored subject as an outbound TCP/UDP exception.

The service owns the token-to-subject mapping. Tokens are random GUIDs, expire, and are single use. Unknown, expired, already-consumed, ambiguous, or non-promptable tokens return an error and leave traffic blocked.

### 6. Allow semantics

Allow creates a `TcpUdpPolicy` with only:

- `AllowedRemoteTcpConnectPorts = "*"`;
- `AllowedRemoteUdpConnectPorts = "*"`.

Listener fields stay null, so no inbound listener permission is created. The exception is merged into the active profile, saved, and all filters are reinstalled through the existing WFP transaction path. Already established policy precedence remains unchanged.

### 7. Popup UI

The controller polls for prompts every 750 ms. A custom WinForms popup is used instead of `NotifyIcon` balloons because balloon notifications cannot reliably expose two explicit action buttons.

Popup properties:

- anchored above the taskbar in the current screen's working-area bottom-right corner;
- topmost but non-activating on show, so it does not steal keyboard focus;
- app icon, product/file description, canonical path, publisher status when available, destination, protocol, and service name;
- **Allow outgoing** primary button;
- **Ignore** secondary button;
- close and timeout are equivalent to Ignore;
- ambiguous service attribution explains why Allow is disabled;
- queued prompts appear sequentially.

The popup never claims an executable is safe. Unsigned, invalid-signature, user-writable-location, and recently changed binaries receive visible warnings, but the user remains able to allow a non-service executable.

### 8. Live network activity

The existing Connections window becomes **SecureWall Network Activity** and refreshes once per second. It merges the service's bounded WFP net-event history with Windows TCP/UDP endpoint tables. Status labels state only observed facts:

- `Allowed`: WFP observed a classify-allow decision;
- `Blocked`: WFP observed a classify-drop decision;
- exact TCP states such as `Established`: current OS transport-table state;
- `Listening (local endpoint)`: a local socket exists, without claiming external reachability.

Fresh settings show all categories. Filters, sorting, process/service identity, protocol, endpoints, direction, and timestamps remain available.

## Failure behavior

- Service unavailable: popup polling stops and existing persistent WFP rules remain active.
- Controller exits: filtering remains active; prompt queue stays bounded and expires.
- Configuration save fails: newly created allow filters are not retained as success; UI receives failure.
- Filter reload fails: service logs failure and preserves or reconstructs default-deny filters; no optimistic success response.
- Event audit unavailable: service-specific Allow is disabled when attribution is ambiguous.
- Prompt flood: coalescing, cooldown, bounded queue, and one-visible-popup policy prevent resource exhaustion and security fatigue.
- App changes on disk after event: before applying Allow, service verifies canonical path still exists and rechecks file identity metadata. The exception remains path-based because that is TinyWall's and WFP's supported application identity model; the UI warns that replacing a file at the same path inherits the rule.

## Verification strategy

Pure queue, token, eligibility, correlation, and allow-policy logic is isolated from WinForms/WFP and tested first in a dependency-free console test harness. Integration tests use fake clocks and fake policy writers. The production project must build for `net48` on Windows.

No automated test enables the real firewall. Real WFP integration requires an expendable local Windows VM or physical test machine with local console access. The manual matrix includes executable, UWP, single-service `svchost`, ambiguous shared-service host, Ignore, timeout, password lock, explicit block, blocklist, reboot, controller exit, service restart, and uninstall recovery.

Installing or activating the firewall is intentionally excluded from unattended verification because a default-deny firewall can sever remote access. It requires explicit local-console confirmation.

## Non-goals

- No inbound allow button.
- No domain-based decisions from reverse DNS.
- No cloud reputation or telemetry.
- No packet-inspection driver.
- No automatic allow based on signatures, install locations, or popularity.
- No weakening of explicit blocks to make an Allow click appear successful.
