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

Persistent and boot-time WFP registrations contain only a restrictive recovery baseline: a deny at `DefaultBlock - 2` plus eight DHCP/DNS permits at `DefaultBlock - 1` (`EnforcementPolicy.RecoveryPermitRules`). All runtime policy uses a dynamic session, including saved permanent allows. The baseline has a lower weight than the runtime default block in the same sublayer, so live outbound default-block IDs remain promptable and the permits are inert while the service runs. Service loss removes runtime allowances and leaves baseline denial except DHCP and DNS. Every registration failure aborts replacement; there is no best-effort exception for explicit blocks or optional permits.

Policy changes durably journal the prior configuration before saving a candidate, transact WFP replacement, then publish state. Recovery failure closes the dynamic session. Startup resolves any pending journal before using stored configuration. Windows Firewall compatibility starts only after protective WFP setup; exact rule ownership and a durable profile-notification journal support cleanup after service loss.
