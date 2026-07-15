# SecureWall isolated VM validation

This bundle is for an expendable Windows VM with a fresh snapshot and local or virtual-console recovery. Never run it on the development host while TinyWall is installed.

The runner refuses physical-machine models, mismatched computer names, non-admin sessions, existing TinyWall, existing SecureWall, loopback targets, and missing confirmation. It captures audit policy, WFP state, Security event 5157 evidence, SecureWall data, probe outcomes, and cleanup state.

Use a reachable external IP and TCP port. The runner first verifies both probes can connect before installing SecureWall. It then requires two visible actions:

1. Click **Allow outgoing** for `SecureWall.AllowProbe.exe`, type `ALLOWED`, and verify the retry connects.
2. Click **Ignore** for `SecureWall.IgnoreProbe.exe`, type `IGNORED`, and verify the retry stays blocked.

Example from an elevated PowerShell console inside the VM:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Run-Validation.ps1 `
  -ExpectedComputerName 'PW-TEST-01' `
  -SnapshotReference 'clean-win11-2026-07-14' `
  -TargetAddress '1.1.1.1' `
  -TargetPort 443 `
  -ConfirmIsolatedVm
```

By default the runner stops the tray process, uninstalls SecureWall, and captures post-cleanup evidence even after a failed test. `-KeepInstalled` is available only for deliberate VM debugging.
