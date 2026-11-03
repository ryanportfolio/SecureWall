# Installing, upgrading and recovering SecureWall

SecureWall is early development software. Do not install it on a machine you cannot recover at its own keyboard and screen. A firewall defect can interrupt networking or weaken host isolation.

## Installers

SecureWall needs Windows 10 version 21H2 (build 19044) or newer, or Windows 11; setup refuses older builds. Each release attaches three per-machine Windows installers:

- `SecureWall_x64.msi` for normal Intel/AMD 64-bit Windows PCs.
- `SecureWall_arm64.msi` for Windows on ARM.
- `SecureWall_x86.msi` only for 32-bit Windows.

The installer copies the complete runtime, installs and starts the LocalSystem service, and starts the tray controller for interactive installations. It refuses to install while TinyWall is present. Repair and modification are rejected.

Alpha installers are unsigned. Windows may show an unknown-publisher warning. Verify the MSI against the release's `SHA256SUMS.txt` before running it. Test procedures for WFP, reboot and audit-policy behavior are in [TESTING.md](TESTING.md) and [HARDENING-VALIDATION.md](HARDENING-VALIDATION.md).

Extracted bundles are not supported install locations; privileged registration requires a protected Program Files tree.

The MSI stores `profiles.json` and `hosts.bck` in `INSTALLDIR/data-defaults`. Its SYSTEM `/install` entry validates the machine-data tree, then copies each default into `%ProgramData%\SecureWall` when it is absent or differs from the shipped file, so a reinstall delivers database updates. Configuration, password and recovery files are left alone. Unsafe ownership, write permissions or reparse paths cause rejection without repairing or overwriting the existing tree. The default Windows Update profile retains the `wuauserv` service exception and removes the executable-wide `svchost.exe` web permission. Other Windows Update components may need their own rules.

## Upgrading

To upgrade from v0.5.0 or later, run the newer MSI for the same architecture at the computer's own keyboard and screen. Setup stops SecureWall, replaces its files in the same directory and starts the new version; rules, settings and the password are kept. All connections to other computers stay blocked until the new version starts, usually for less than a minute, and open connections drop. If setup stops on a "Files in Use" page that lists a SecureWall window, such as SecureWall Firewall Settings or SecureWall Network Activity, choose **Ignore**: setup closes SecureWall itself before it replaces any file. If anything fails, setup rolls back to the previous version and its policy; if that version then cannot start, connections stay blocked until a restart or `sc start SecureWall` from an elevated prompt.

v0.4.2 and older cannot be upgraded in place: uninstall them in Settings > Apps first, then install the new version. That is a one-time step; [SECURITY.md](SECURITY.md#in-place-upgrade) has the details and the recovery steps.

## Check for updates

Click **Check for updates** in the tray menu, or on the Maintenance tab in Manage. SecureWall reads its own releases on GitHub and shows the newest version it may install with its release notes. **Install** downloads the installer, asks Windows for administrator permission, checks the installer against the release's `SHA256SUMS.txt`, and runs the in-place upgrade with setup's own window. Manage and Network Activity close once you approve the administrator prompt, and changes not yet applied in Manage are discarded, so setup does not stop on its "Files in Use" page. The **Include prereleases** setting you checked with is kept. If another SecureWall window is open, such as a message box, a file picker or the exception editor, Install asks you to close it and click Install again. While setup runs, the tray menu does not open; it works again once setup finishes or you cancel it. Updates started from v0.5.4 or earlier, including the update to v0.5.5, leave Manage and Network Activity open; choose **Ignore** on that page as described above. **Skip this version** hides that version until a newer one appears.

The check runs only when you click it. If SecureWall is locked, it asks for the password first. Every SecureWall release so far is a prerelease, so **Include prereleases** on the Maintenance tab is on by default; turn it off to be offered full releases only. Releases published before the update check existed have no manifest header and are never offered. SecureWall never contacts TinyWall's update server; the TinyWall updater and its `/updatenow` switch stay removed. [SECURITY.md](SECURITY.md#check-for-updates) describes the checks and their limits.

## What stays blocked when the service is down

The persistent and boot-time baseline denies all non-loopback traffic, including DNS and DHCP. It contains no recovery permits. All runtime policy, including saved allows, belongs to the service's dynamic WFP session. Service loss or startup failure withdraws runtime permissions and leaves strict external denial until the service successfully restores policy or explicit removal completes.

A running service that has to withdraw its permissions rebuilds its policy in-process; if that keeps failing, it exits with an error code and Windows restarts it after 5, 30 and 60 seconds, then every minute. That automatic restart takes effect from the first Windows restart after installation. A reload that fails before changing any filter keeps the current policy and retries after about 5, 15 and 30 seconds, unless the failure leaves network addresses unverified or a grant already out of date; then it withdraws permissions and rebuilds. See [SECURITY.md](SECURITY.md).

Address renewal and name resolution can fail during this interval. If the cause persists, recovery requires a local console. Disabling the SecureWall service does not release the baseline: it still loads at every boot and keeps external traffic blocked until SecureWall is removed, or until the service is set back to Automatic (`sc config SecureWall start= auto`) and started. [SECURITY.md](SECURITY.md) explains why and which validation case to run first.

## Setup logs

When setup fails, its last page has two buttons: **Open SecureWall log** opens `%ProgramData%\SecureWall\logs\installer.log`, and **Open setup log** opens the Windows Installer log that every SecureWall setup writes to `%TEMP%` (`MSI*.LOG`). Each opens in your default text viewer, so Ctrl+A and Ctrl+C copy the whole log for a report. After a failed `/install`, `installer.log` also holds the service's status and exit codes and the `service.log` entries written during that attempt.

Ordinary interactive controller logs are in `%LocalAppData%\SecureWall\logs`; elevated controller and service/installer logs remain under guarded `%ProgramData%\SecureWall\logs`.

Optional diagnostic logging can be enabled in Settings for a normal-use trial and turned off later. See [live testing](LIVE-TESTING.md), the [coverage map](DIAGNOSTIC-COVERAGE.md), and the [local support-bundle collector](../tools/diagnostics/README.md). Reports separate what was observed from what the logs cannot show.

## Moving from TinyWall

A clean TinyWall-only migration requires uninstalling TinyWall and rebooting before installing SecureWall. Export TinyWall's settings to a `.tws` file and copy `%ProgramData%\TinyWall` before removing it; the rollback order is in [live testing](LIVE-TESTING.md#rolling-back-to-tinywall).

## Recovery when machine data is rejected

A previous SecureWall build may leave an unsafe `%ProgramData%\SecureWall` tree that this version must reject. Generic MSI failure can indicate that rejection; the Windows Application log then holds an Error from source `SecureWall` with the reason. Stop, preserve the directory and installer diagnostics, and use trusted manual recovery from a local console. Do not change ACLs or blindly restore writable legacy configuration to bypass validation. See [legacy data and controller recovery](SECURITY.md#legacy-data-and-controller-recovery) for the migration boundary and MSI diagnostic limit.

If an installed SecureWall rejects that tree, the service stops starting while its deny baseline keeps blocking external traffic, and `SecureWall.exe /uninstall` refuses to run. Remove SecureWall from Settings > Apps at the local console. MSI removal runs a SYSTEM [emergency release](SECURITY.md#emergency-release-when-machine-data-is-rejected) that restores Windows Firewall and audit state from their registry journals, deletes only SecureWall's own firewall objects and service, leaves the hosts file and the rejected tree in place without reading file contents from it, and reports its warnings on stderr and, while the service's event source exists, in the Application event log. Each failed release step writes Application event 1001 from source `SecureWall` naming the step and the exception, and a failed release adds one 1001 summary with the exit code.

The controller can request a start of an existing validated service. Missing services require MSI recovery through full removal and fresh installation; controller startup does not install a service.
