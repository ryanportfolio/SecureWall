# SecureWall isolated VM validation

This bundle is for an expendable Windows VM with a fresh snapshot and local or virtual-console recovery. Never run it on the development host while TinyWall is installed.

The runner refuses physical-machine models, mismatched computer names, non-admin sessions, existing TinyWall, existing SecureWall, loopback targets, missing confirmation, and an MSI that differs from the bundle hash manifest. It installs and uninstalls the actual MSI in silent mode. The extracted application is never installed as a service. It captures MSI logs, audit policy, WFP state, Security event 5157 evidence, SecureWall data, probe outcomes, and cleanup state.

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
  -Architecture x64 `
  -ConfirmIsolatedVm
```

Choose `x86`, `x64`, or `arm64` to match the VM. All three packages must have been built before preparing this bundle. A bundle hash detects changes after packaging; it is not an independent publisher signature.

By default the runner stops the tray process, uninstalls SecureWall through the MSI, and captures post-cleanup evidence even after a failed test. It compares Windows Firewall rules, notification settings, the hosts file, audit policy, service/provider presence, and the controller task. `-KeepInstalled` is available only for deliberate VM debugging. An unrelated policy change during the test produces a restoration mismatch that must be investigated.

This guided test covers basic fresh-install/allow/ignore/removal behavior. It does not cover the boot, crash, fault injection, repair/upgrade rejection, service identity, or networking compatibility cases in `docs/HARDENING-VALIDATION.md`. Record those cases separately before production use. Never call a failed or skipped case a pass.
