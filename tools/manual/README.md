# SecureWall manual acceptance test

This is an unsigned test build. Use the MSI on a disposable VM or spare PC. Keep TinyWall installed and running on your daily-use machine until the full validation matrix passes. Do not start the extracted controller elevated; service activation is a privileged operation even when triggered by controller recovery.

Safest target: a spare Windows 10/11 PC or disposable VM. If you use your primary PC, use a local console, save the TinyWall installer/config first, create a restore point, and do not depend on remote access for recovery.

## Before changing firewalls

1. Extract the whole zip to a local folder.
2. Verify `bundle-manifest.json` hashes if desired.
3. Keep this README and your TinyWall installer available offline.
4. Close sensitive work. Confirm you have local Administrator access.
5. Uninstall TinyWall normally, then reboot. SecureWall intentionally refuses to install while the TinyWall service exists.

## Install SecureWall (recommended)

Choose the MSI for the test PC:

- Most Intel/AMD Windows 10/11 PCs: `installers\SecureWall_x64.msi`
- 32-bit Windows: `installers\SecureWall_x86.msi`
- Windows on ARM: `installers\SecureWall_arm64.msi`

The packages are intentionally unsigned test artifacts, so Windows may show an Unknown Publisher warning. Confirm the MSI hash in `bundle-manifest.json`, then run the matching MSI from a local Administrator account. The installer and the runtime both refuse activation while the TinyWall service exists.

Expected: installation succeeds, the SecureWall service becomes `Running`, and the SecureWall tray icon appears. Open **Network activity** from the tray menu. Fresh settings show listening, allowed/active, and blocked rows.

## Installation and maintenance limits

Direct service installation from an extracted Downloads/Desktop/test folder is rejected. LocalSystem execution requires a protected Program Files payload tree with trusted ownership and write permissions. Use the MSI to place the runtime correctly.

Repair and in-place upgrades are deliberately rejected before they change the installed product. Export the configuration, uninstall explicitly, and install the new package. Retain the old installer and a VM snapshot until the new build passes its acceptance checks. Rollback of a fresh installation and removal still require testing with the exact MSI.

Runtime permissions disappear when the SecureWall service stops or crashes. Its persistent deny baseline remains until an explicit uninstall removes it. Loss of networking after service failure is intentional; recover through the local console.

## Acceptance checks

Use a reachable IP/port for your network; `1.1.1.1:443` is only an example.

1. Run the Allow probe:

   ```powershell
   & .\probes\SecureWall.AllowProbe.exe 1.1.1.1 443 5000
   $LASTEXITCODE
   ```

   Expected initially: exit `2` or `3`, a bottom-right popup, and a `Blocked` row within about one second. Click **Allow outgoing**, rerun, and expect exit `0` plus an `Allowed` observation.

2. Run the Ignore probe:

   ```powershell
   & .\probes\SecureWall.IgnoreProbe.exe 1.1.1.1 443 5000
   $LASTEXITCODE
   ```

   Expected: popup appears. Click **Ignore**. Retry remains exit `2` or `3`, no allow rule is created, and the blocked row remains visible.

3. Inspect a TCP/UDP listener row. Expected status: `Listening (local endpoint)`. This means a socket exists locally; it does not mean the port is reachable from another machine.

4. Confirm columns show process/service, protocol, local and remote endpoints, exact status/direction, and timestamp. Toggle each filter and confirm the list keeps refreshing.

## Uninstall and restore TinyWall

Preferred: uninstall **SecureWall** from Windows Installed apps. If MSI removal fails, preserve its log and revert the disposable VM snapshot. The installed executable also has an interactive recovery command, run elevated from its protected installation directory:

```powershell
& .\SecureWall.exe /uninstall
```

Approve the interactive uninstall prompt. Reboot, verify that the service, provider, compatibility rules and task are absent and original host settings are restored, then reinstall TinyWall and confirm its service is running. If any SecureWall acceptance check fails, stop testing and preserve `C:\ProgramData\SecureWall\logs` and MSI logs. An absent service alone does not prove complete cleanup.

Do not install SecureWall and TinyWall together. Do not test over a remote-only session.
