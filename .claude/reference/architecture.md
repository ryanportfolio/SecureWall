# Architecture

- `TinyWall/TinyWallService.cs`: LocalSystem enforcement owner, WFP provider/sublayers, weighted filters, prompt service handlers.
- `pylorak.Windows.WFP/`: direct Windows Filtering Platform wrappers; no custom kernel driver.
- `TinyWall/FirewallLogWatcher.cs`: named-field Security event 5157 parsing and exact audit-policy leasing.
- `TinyWall/Prompting/`: pure identity, correlation, bounded queue, token DTO, display coordinator, and WinForms popup.
- `TinyWall/ConnectionsForm.cs`: one-second live Network Activity view combining bounded WFP allow/drop observations with Windows TCP/UDP endpoint tables. `Allowed` and `Blocked` are observed decisions; `Listening (local endpoint)` is not a reachability claim.
- `TinyWall/TinyWallController.cs`: unprivileged tray client and asynchronous 750 ms prompt polling.
- `TinyWall/Message.cs`: source-generated JSON protocol over the authenticated named pipe.

Normal mode is default deny. Only committed runtime IDs for outbound ALE default-block filters are promptable. The service maps an opaque token to an immutable subject; the controller never supplies an executable path, package SID, or service name as authority.

Filter weights retain upstream order: blocklist, raw-socket permit/block, user block, user permit, default permit, default block. Prompt-created allows therefore cannot override explicit user blocks.

Every default-block filter requires both persistent and boot-time WFP registrations. Any failure escapes the rule loop, rolls back the transaction, and prevents initial service startup from reporting Running. Non-critical filters retain TinyWall's best-effort compatibility behavior.
