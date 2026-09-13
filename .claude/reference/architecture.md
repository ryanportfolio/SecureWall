# Architecture

- `TinyWall/TinyWallService.cs`: LocalSystem enforcement owner, WFP provider/sublayers, weighted filters, prompt service handlers.
- `pylorak.Windows.WFP/`: direct Windows Filtering Platform wrappers; no custom kernel driver.
- `TinyWall/FirewallLogWatcher.cs`: named-field Security event 5157 parsing and exact audit-policy leasing. `AuditPolicyLease.cs` journals the original flags to `HKLM\SOFTWARE\SecureWall\AuditRecovery` (`RegistryAuditPolicyJournal.cs`) before the first change; service start, `/uninstall`, and `/msi-cleanup` restore stale entries.
- `TinyWall/Prompting/`: pure identity, correlation, bounded queue, token DTO, display coordinator, and WinForms popup. `ExecutableRiskAssessment.cs` is the pure warning classifier; `TinyWall/ExecutableRiskProbe.cs` gathers signature (WinVerifyTrust plus catalog), directory DACL, and last-write facts in the controller at display time.
- `TinyWall/ConnectionsForm.cs`: one-second live Network Activity view combining bounded WFP allow/drop observations with Windows TCP/UDP endpoint tables. `Allowed` and `Blocked` are observed decisions; `Listening (local endpoint)` is not a reachability claim.
- `TinyWall/TinyWallController.cs`: unprivileged tray client and asynchronous 750 ms prompt polling.
- `TinyWall/Message.cs`: source-generated JSON protocol over the authenticated named pipe.

Normal mode is default deny. Only committed runtime IDs for outbound ALE default-block filters are promptable. The service maps an opaque token to an immutable subject; the controller never supplies an executable path, package SID, or service name as authority.

Filter weights retain upstream order: blocklist, raw-socket permit/block, user block, user permit, default permit, default block. Prompt-created allows therefore cannot override explicit user blocks.

The persistent and boot-time baseline denies all non-loopback traffic, including DNS and DHCP. It contains no recovery permits. All runtime policy, including saved allows, belongs to the service's dynamic WFP session. Service loss or startup failure withdraws runtime permissions and leaves strict external denial until the service successfully restores policy or explicit removal completes.

Address renewal and name resolution can fail during this interval; recovery requires a local console. The deny is at `DefaultBlock - 2`; runtime default blocks remain promptable. Every filter registration failure aborts replacement.

Policy changes journal the prior configuration before attempting a candidate write, transact WFP replacement, then publish state. A write that throws after replacement still triggers compensation. Encrypted output finalizes before content flushing, and the installed file is flushed after replacement. This supports process-failure recovery; journal rename/delete ordering under power loss and physical-media guarantees remain unverified.

Recovery failure closes the dynamic session. Startup resolves any pending journal before using stored configuration. Windows Firewall compatibility starts only after protective WFP setup; exact rule ownership and a durable profile-notification journal support cleanup after service loss.


`Installer/MachineDataGuard.cs` validates machine-data ancestors and descendants before production data access. MSI defaults live under `INSTALLDIR/data-defaults`; SYSTEM `/install` creates only a missing protected directory and seeds only absent `profiles.json` and `hosts.bck` after validation. MSI has no ProgramData writes or deletes. Unsafe existing trees are rejected, never repaired in place. Safe disappearing temporary children are tolerated only after parent revalidation.

Configuration defaults require confirmed absence; existing corrupt/unreadable configuration fails startup closed. Environmental reload failures withdraw runtime grants. Hosts restoration errors propagate and retain recovery data. Executable warnings include file ownership/write rights and parent replacement rights; audit health and bounded suppression diagnostics describe degraded attribution without granting traffic. The Windows_Update profile retains exact `wuauserv` outbound TCP access and has no executable-wide rule.
