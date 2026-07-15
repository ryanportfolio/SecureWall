# PromptWall security model

PromptWall is default-deny host firewall software. A defect can either interrupt networking or permit traffic that should have been blocked. Treat install and real-network testing as privileged operations.

The Windows service remains start-pending until its first WFP default-deny transaction commits. If initialization fails, it stops instead of reporting a ready but unenforced state.

## Enforcement invariants

- Normal mode blocks inbound and outbound traffic unless a higher-priority explicit allow applies.
- The LocalSystem service owns WFP filters and stored policy. The tray controller cannot author a subject.
- A prompt is created only for outbound TCP/UDP drops whose runtime filter ID belongs to PromptWall's committed default-block ALE filters.
- Explicit user blocks and blocklists have higher weights and never become prompts.
- Allow uses an opaque, expiring, single-use service token. The resulting rule contains only outbound TCP/UDP connect wildcards; inbound listener fields remain unset.
- Ignore, close, timeout, unknown token, expiry, ambiguity, lock, save failure, and reload failure all remain blocked.
- Package identity accepts only an AppContainer package SID (`S-1-15-2-*`); null-SID sentinels are treated as no package authority. Service identity uses exact path plus service name. An ambiguous or unattributed registered service executable cannot be allowed from the popup.
- Named-pipe clients must run from the exact installed PromptWall executable path in Debug and Release builds; Allow remains password-gated when the service is locked.

## Audit and attribution

WFP net events are authoritative for the drop and filter ID but do not contain a PID. Security event 5157 is parsed by field name and accepted only when filter ID, normalized path, direction, protocol, complete tuple, and timestamp match. PromptWall leases failure auditing by OR-ing it into the existing Filtering Platform Connection flags and restores the exact prior flags on disposal.

If auditing or PID/service lookup fails, attribution degrades without weakening WFP policy. PromptWall also inventories registered Win32 service image paths; an unreadable or incomplete inventory fails closed, and an unattributed service executable remains non-allowable.

## Operational safety

Never install PromptWall over RDP, SSH, remote PowerShell, a cloud-only console, or any machine where losing networking prevents recovery. Use an expendable Windows VM with a snapshot and working local/virtual console. Do not install TinyWall and PromptWall together.

The MSI checks TinyWall's registry identity, and the elevated `/install` path independently checks the Service Control Manager. Either detection refuses installation before PromptWall activates WFP. Keep TinyWall installed on the real machine until PromptWall completes the isolated VM matrix; then uninstall TinyWall and reboot before a PromptWall installation.

Recovery order in a test VM:

1. Use the VM/local console, not the network.
2. Uninstall PromptWall from Windows Apps, or run the installed `PromptWall.exe /uninstall` elevated.
3. Reboot and verify the PromptWall service, scheduled task, provider filters, and compatibility rules are gone.
4. If ordinary uninstall fails, preserve logs and the VM snapshot for diagnosis; do not improvise broad WFP/registry deletion commands on a real machine.

PromptWall has no binary update feed. Upstream TinyWall update descriptors and binaries are intentionally disabled for this distinct fork.

## Current assurance boundary

Pure-domain tests, native compilation, protocol serialization, and synthetic popup mode do not prove real WFP behavior. Production claims require the manual matrix in `docs/TESTING.md`, code signing, installer verification, and independent security review.
