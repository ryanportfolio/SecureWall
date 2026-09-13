# SecureWall security model

SecureWall is default-deny host firewall software. A defect can either interrupt networking or permit traffic that should have been blocked. Treat install and real-network testing as privileged operations.

The Windows service remains start-pending until its first WFP default-deny transaction commits. If initialization fails, it stops instead of reporting a ready but unenforced state.

## Enforcement invariants

- Normal mode blocks inbound and outbound traffic unless a higher-priority explicit allow applies.
- The LocalSystem service owns WFP filters and stored policy. The tray controller cannot author a subject.
- A prompt is created only for outbound TCP/UDP drops whose runtime filter ID belongs to SecureWall's committed default-block ALE filters.
- Explicit user blocks and blocklists have higher weights and never become prompts.
- Allow uses an opaque, expiring, single-use service token. The resulting rule contains only outbound TCP/UDP connect wildcards; inbound listener fields remain unset.
- Ignore, close, timeout, unknown token, expiry, ambiguity, lock, save failure, and reload failure all remain blocked.
- Package identity accepts only an AppContainer package SID (`S-1-15-2-*`); null-SID sentinels are treated as no package authority. Service identity uses exact path plus service name. An ambiguous or unattributed registered service executable cannot be allowed from the popup.
- Named-pipe clients must run from the exact installed SecureWall executable path in Debug and Release builds; Allow remains password-gated when the service is locked. This check is an image-path comparison, not a caller-identity boundary; see "Local IPC boundary" below.

The persistent and boot-time baseline denies all non-loopback traffic, including DNS and DHCP. It contains no recovery permits. All runtime policy, including saved allows, belongs to the service's dynamic WFP session. Service loss or startup failure withdraws runtime permissions and leaves strict external denial until the service successfully restores policy or explicit removal completes.

Address renewal and name resolution can fail during this interval; recovery requires a local console.

The baseline deny uses `DefaultBlock - 2`, below the runtime default block so committed runtime filter IDs retain prompt authority. Disabled, Learning, LAN and WSL allowances do not survive independently of the dynamic session. BlockAll suppresses optional LAN and WSL permits. Until-reboot exceptions expire on service initialization or reinitialization; timed exceptions retain their absolute expiry.

The Windows_Update database profile grants outbound TCP to the exact `wuauserv` service at its `svchost.exe` path. It supplies no executable-wide `svchost.exe` permission. Other Windows Update components need their own justified rules; update completion remains unverified.

Configuration changes journal the prior policy, then save and enforce the candidate before publishing success. Compensation starts whenever the candidate write is attempted, including a failure after the file was replaced. Failed compensation withdraws runtime grants. If journal completion fails after enforcement, runtime grants are withdrawn before further storage recovery is attempted.

An unresolved recovery journal must be restored before startup can load stored policy. Defaults are used only on confirmed configuration absence; corruption, decryption errors and access failures stop initialization rather than silently loading defaults.

Required display/network-triggered policy reload failures also withdraw runtime grants. Every filter registration is required; a registration failure aborts replacement. Background reads do not extend the ten-minute password unlock window.

Encrypted output is finalized before the underlying file content is flushed with `Flush(true)`. Writers use a temporary file in the target directory, replace or move it into place, then flush the installed file. A post-swap flush failure can leave the candidate on disk even though the write throws.

The recovery journal supports process-failure recovery; ordinary file rename and journal deletion do not establish durable directory-entry ordering across power loss. A lost journal deletion could replay an older policy. Journal rename/delete power-loss ordering and physical-media persistence remain unverified. Content-flush calls and synthetic tests are not proof of either guarantee.

The journal is `config.recovery` beside the service configuration. An unreadable or corrupt journal deliberately prevents startup because the service cannot establish which policy was committed. Preserve it and the configuration for diagnosis. From the VM/local console, restore a known-good matching backup or use the installed MSI's full uninstall followed by a fresh install. If removal fails, restore the VM snapshot. Deleting only the journal could activate an uncommitted candidate policy; do not use that as recovery.

The controller authenticates the connected service before writing requests. The narrow Debug synthetic-pipe exception cannot select the production pipe. Prompt snapshots are reconciled against service authority, with at most 64 displayed/queued entries and an absolute token deadline independent of polling or AI reading time.

The service grants authenticated users only process-image query and lifetime-wait rights so the unelevated controller can retain a process handle and compare it with SCM's SYSTEM service configuration. It grants no process-write or token-query rights. Timed firewall exceptions are pruned on the minute maintenance pass and during rule reconstruction; they are not a second-precision cutoff.

Allow grants the identified executable, package or service permanent outbound TCP/UDP access to all destinations and ports. The displayed connection is an example of blocked traffic, not the scope of that grant. Executable rules follow a path, not a content hash. Publisher text is unverified metadata, not proof of a trusted signature.

## Audit and attribution

WFP net events are authoritative for the drop and filter ID but do not contain a PID. Security event 5157 is parsed by field name and accepted only when filter ID, normalized path, direction, protocol, complete tuple, and timestamp match. SecureWall leases failure auditing by OR-ing it into the existing Filtering Platform Connection flags and restores the exact prior flags on disposal.

The original flags are journaled to `HKLM\SOFTWARE\SecureWall\AuditRecovery` (64-bit registry view; value name is the subcategory GUID, DWORD value is the original flags) before any `AuditSetSystemPolicy` change, whichever of the two in-process leases (failure auditing, learning-mode success auditing) makes it. The entry is written and flushed first; the live policy changes only after the write succeeded. Orderly service stop restores the flags and clears the entry. Service start, interactive `/uninstall`, and `/msi-cleanup` read the journal before any other audit work and restore whatever a crash, `FailFast`, `Process.Kill`, or interrupted MSI cleanup left behind, clearing each entry only after the backend accepted the restore. A malformed entry is skipped, kept in place, and reported by name so the healthy ones still restore; it never blocks uninstall because it holds no recoverable value. A healthy entry whose restore fails keeps its entry and aborts the uninstall (interactive `/uninstall` and `/msi-cleanup` return -1, so the MSI rolls back and the product stays installed), because the uninstaller deletes `HKLM\SOFTWARE\SecureWall` and removes the service that would otherwise retry; the log names the subcategory. A lease clears its own entry only after querying the live policy and finding it equal to the recorded original, so a nested lease whose restore failed cannot be masked by an outer lease that changed nothing. Whether a machine's audit policy was in fact returned to its original state after a crash remains a live-VM check, not a unit-test result.

If auditing or PID/service lookup fails, attribution degrades without weakening WFP policy. Subscription health, coalesced error/suppression counts and controller notifications are intended to make this visible without a per-drop log flood; restart recovery and UI delivery still require VM validation. A failed Security-event subscription stays unavailable until service restart after event access is repaired. There is no subscription retry; disposal detaches the watcher under its lifecycle lock, then waits for callbacks after releasing that lock. Late records and errors from an inactive watcher are rejected. SecureWall also inventories registered Win32 service image paths; an unreadable or incomplete inventory fails closed, and an unattributed service executable remains non-allowable. Executable warnings inspect file ownership and mutation rights as well as parent-directory replacement rights. These observations can change after display and do not pin executable content.

Exactly correlated drops enter a bounded queue of 64 entries. Each 250 ms timer pass drains one batch before acquiring a fresh bulk SCM process snapshot, shared only within that batch. Another batch always reads SCM again. Correlation callbacks do no SCM work and skip admission if a policy transition holds the queue lock. Capacity, contention, and a two-second publication deadline suppress work with coalesced diagnostics; suppressed connections remain blocked. Policy replacement, mode publication, and shutdown clear pending tokens and invalidate in-flight batches. Snapshot failures and shared service processes remain non-allowable. Call-count tests verify batching; they are not a performance benchmark.

## Local IPC boundary

The controller pipe is created with a DACL that grants Authenticated Users read and write access. On each connection the service resolves the client's PID with `GetNamedPipeClientProcessId`, reads that process's image path, and accepts the connection only when the path equals the service's own executable path (`PipeClientAuthorization.IsExpectedExecutable`). That is the whole server-side check: no token, session, or signature comparison, and no per-request re-authentication. The controller performs the mirror check on the server (image path plus SCM service identity) so a fake pipe cannot impersonate the service.

The consequence is that any process running as the same user can start the installed `SecureWall.exe` in controller mode, or drive an already running controller, and send every request the controller can send. That includes `PUT_SETTINGS`, which replaces the service's full configuration (every exception, mode-independent settings, blocklist and hosts options) in one message when the changeset matches, and `MODE_SWITCH`, `SET_PASSPHRASE`, `STOP_SERVICE`, and `ALLOW_PROMPT` (message types above 2047; types above 4095 are service-internal and rejected from the pipe). The application password is the only gate on those messages: while the service is locked they return `RESPONSE_LOCKED`; while it is unlocked (no password set, or within ten minutes of the last successful user action after `UNLOCK`) they are applied. Set a password and keep the service locked when unattended. This behaviour is inherited from TinyWall's pipe design and is unchanged in SecureWall; an operating-system administrator can in any case stop the service through SCM.

## Operational safety

Never install SecureWall over RDP, SSH, remote PowerShell, a cloud-only console, or any machine where losing networking prevents recovery. Use an expendable Windows VM with a snapshot and working local/virtual console. Do not install TinyWall and SecureWall together.

The MSI checks TinyWall's registry identity, and shared registration, controller recovery and service startup paths check the Service Control Manager. Activation refuses coexistence. Direct installation additionally requires a protected tree below Program Files, including every executable, DLL and ancestor; extracted bundles are not install locations. Keep TinyWall installed on the real machine until SecureWall completes the isolated VM matrix; then uninstall TinyWall and reboot before a SecureWall installation.

Machine data is guarded separately from executable installation. The MSI places both defaults under protected `INSTALLDIR/data-defaults` and has no ProgramData file creation, write or deletion components.

Before timing, logging or policy access, the Release entry validates machine data; SYSTEM `/install` may create a missing directory with a protected ACL and seeds only absent `profiles.json` and `hosts.bck` files after full-tree validation. The shared production data-path accessor also enters the guard. Existing unsafe owners, writable descendants, untrusted mutation rights and reparse ancestors are rejected without blessing the tree.

A disappearing temporary child during atomic replacement is tolerated only after its parent is revalidated; missing or unsafe roots remain errors. Debug synthetic paths do not prove the installed Release guard.

Hosts enable/disable failures propagate into configuration or cleanup failure. A missing original hosts backup is a valid never-enabled case; an unreadable backup is an error. A failed restoration retains its backup and must prevent successful teardown. Verify actual hosts bytes and failure ordering in the VM before accepting removal.

MSI repair, modification and in-place upgrades are rejected before changing the installation. Use explicit full removal followed by a fresh install. MSI maintenance uses a separate SYSTEM-only, noninteractive cleanup path; interactive removal retains confirmation and password handling. Cleanup must stop the service and restore owned Windows Firewall compatibility state before removing the persistent deny baseline. An administrator can stop the service through SCM; the application password is not an operating-system administrator security boundary.

The Windows Firewall service (`MpsSvc`) is a prerequisite for compatibility management. A disabled service must be restored to a supported configuration before installation. Recorded compatibility state must still be restored before the deny baseline can be removed. If Windows defers service deletion because another program holds a service handle, close service-management tools or reboot before reinstalling.

Compatibility rules use reserved exact identifiers and a reserved group. Original notification settings are journaled durably before mutation and retained until restoration completes. Old builds used random names and did not record original settings: their orphan rules and historical notification values require an explicit migration audit against a known baseline. This version does not guess ownership from a substring or reconstruct missing history.

Recovery order in a test VM:

1. Use the VM/local console, not the network.
2. Uninstall SecureWall from Windows Apps, or run the installed `SecureWall.exe /uninstall` elevated.
3. Reboot and verify the SecureWall service, scheduled task, provider filters, and compatibility rules are gone.
4. If ordinary uninstall fails, preserve logs and the VM snapshot for diagnosis; do not improvise broad WFP/registry deletion commands on a real machine.

SecureWall has no binary update feed. Upstream TinyWall update descriptors and binaries are intentionally disabled for this distinct fork.

Optional AI explanations require HTTPS, disallow redirects, and disclose the default payload: executable filename, unverified publisher, identity type, service name and package SID when present. Full executable paths are excluded; destination disclosure is opt-in. The user-selected provider receives that payload and bearer credential.

## Current assurance boundary

Pure-domain tests, native compilation, protocol serialization, and synthetic popup mode do not prove real WFP behavior. Production claims require the matrices in `docs/TESTING.md` and `docs/HARDENING-VALIDATION.md`, code signing, installer verification, and independent security review. PID/service attribution races, audit-lease recovery after crashes, VPN/WSL/IPv6 behavior, and packet-level startup/shutdown behavior remain explicit live-test boundaries.

## Legacy data and controller recovery

A clean TinyWall-only migration means uninstalling TinyWall and rebooting before installing SecureWall, with no preexisting SecureWall machine-data tree. SYSTEM setup can create a missing protected `%ProgramData%\SecureWall` directory. A prior SecureWall installation can leave a tree with inherited user-write permissions. Even a trusted owner cannot prove that its old policy or backups were never changed by an untrusted user. Rejection of that tree is required security behavior.

A generic MSI error, including error 1722, can mean that machine-data validation rejected the tree. The startup diagnostic names the protected directory and retains the underlying exception. Interactive controller startup can show that diagnostic; SYSTEM maintenance writes it to stderr and opens no dialog. The deferred MSI action still has no dedicated UI for this error, and visibility of stderr depends on installer logging. Capture installer diagnostics and preserve the directory and VM snapshot before recovery.

Stop and use trusted manual recovery from a local console when validation fails. This build does not repair ACLs, import policy, quarantine or delete rejected data, or bypass the guard during uninstall. Do not blindly restore legacy writable configuration or backups into a new protected tree. Recovery needs an explicit, reviewed procedure based on trusted installation media and independently verified policy; existing recovery records remain evidence. No live recovery actions were performed as part of this change.

Controller recovery starts only an existing service with the expected protected executable, LocalSystem account and dedicated-process registration. Missing registration requires MSI recovery; this MSI supports full removal followed by a fresh installation, not in-place repair. Pending deletion requires closing service-management handles or a reboot before retrying. Ordinary users authorize only a System32 `sc.exe start` request; the controller waits for SCM Running status. Administrator controllers also do not create services or reconfigure dependencies. Service creation remains a guarded SYSTEM installation operation.

Ordinary interactive controller logs use `%LocalAppData%\SecureWall\logs`. Elevated, SYSTEM, noninteractive, impersonating and service/installer logging paths use guarded machine storage and never fall back to user paths. Log destination providers are evaluated lazily, guard failures are swallowed without recursive logging, and the existing 512 KiB truncation threshold remains. Service policy and recovery never consume user logs.

The review's proposed changes for findings 4 and 5 would release enforcement or cleanup protection after a required hosts operation failed. Those fail-open changes are rejected: retain failure and recovery evidence until restoration succeeds. Finding 6's proposed retention of prompt tokens across successful policy changes is also rejected. Revocation requires fresh evidence under the current policy generation; reduced popup churn does not justify stale authority.
