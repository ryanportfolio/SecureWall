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
- Named-pipe clients must run from the exact installed SecureWall executable path in Debug and Release builds; Allow remains password-gated when the service is locked.

The persistent and boot-time policy is a restrictive deny-only baseline. Runtime permits and promptable default blocks belong to a dynamic WFP session. Stopping or losing the service withdraws runtime permissions and leaves the baseline blocking until the service successfully restores saved policy. Disabled, Learning, LAN and WSL allowances do not survive independently of that session. BlockAll suppresses optional LAN and WSL permits. Until-reboot exceptions also expire on service initialization or reinitialization; timed exceptions retain their absolute expiry.

Configuration changes journal the prior policy, then save and enforce the candidate before publishing success. Failed compensation withdraws runtime grants. An unresolved recovery journal must be restored before startup can load the candidate. Every filter registration is required; a registration failure aborts replacement. Background reads do not extend the ten-minute password unlock window.

The journal is `config.recovery` beside the service configuration. An unreadable or corrupt journal deliberately prevents startup because the service cannot establish which policy was committed. Preserve it and the configuration for diagnosis. From the VM/local console, restore a known-good matching backup or use the installed MSI's full uninstall followed by a fresh install. If removal fails, restore the VM snapshot. Deleting only the journal could activate an uncommitted candidate policy; do not use that as recovery.

The controller authenticates the connected service before writing requests. The narrow Debug synthetic-pipe exception cannot select the production pipe. Prompt snapshots are reconciled against service authority, with at most 64 displayed/queued entries and an absolute token deadline independent of polling or AI reading time.

The service grants authenticated users only process-image query and lifetime-wait rights so the unelevated controller can retain a process handle and compare it with SCM's SYSTEM service configuration. It grants no process-write or token-query rights. Timed firewall exceptions are pruned on the minute maintenance pass and during rule reconstruction; they are not a second-precision cutoff.

Allow grants the identified executable, package or service permanent outbound TCP/UDP access to all destinations and ports. The displayed connection is an example of blocked traffic, not the scope of that grant. Executable rules follow a path, not a content hash. Publisher text is unverified metadata, not proof of a trusted signature.

## Audit and attribution

WFP net events are authoritative for the drop and filter ID but do not contain a PID. Security event 5157 is parsed by field name and accepted only when filter ID, normalized path, direction, protocol, complete tuple, and timestamp match. SecureWall leases failure auditing by OR-ing it into the existing Filtering Platform Connection flags and restores the exact prior flags on disposal.

If auditing or PID/service lookup fails, attribution degrades without weakening WFP policy. SecureWall also inventories registered Win32 service image paths; an unreadable or incomplete inventory fails closed, and an unattributed service executable remains non-allowable.

## Operational safety

Never install SecureWall over RDP, SSH, remote PowerShell, a cloud-only console, or any machine where losing networking prevents recovery. Use an expendable Windows VM with a snapshot and working local/virtual console. Do not install TinyWall and SecureWall together.

The MSI checks TinyWall's registry identity, and shared registration, controller recovery and service startup paths check the Service Control Manager. Activation refuses coexistence. Direct installation additionally requires a protected tree below Program Files, including every executable, DLL and ancestor; extracted bundles are not install locations. Keep TinyWall installed on the real machine until SecureWall completes the isolated VM matrix; then uninstall TinyWall and reboot before a SecureWall installation.

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
