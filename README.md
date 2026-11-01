# SecureWall

Website: [secwall.org](https://secwall.org/)

SecureWall is a Windows default-deny firewall derived from TinyWall. It keeps TinyWall's service-owned Windows Filtering Platform enforcement and adds a tightly scoped, bottom-right prompt when a previously unknown application or service is blocked while attempting an outbound connection.

The tray menu's **Network Activity** window refreshes every second and separates observed WFP decisions (`Allowed` / `Blocked`), exact TCP transport states, and local listening endpoints. `Listening (local endpoint)` deliberately does not claim that a port is externally reachable.

SecureWall is early development software. Do not install it on a machine you cannot recover locally. A firewall defect can interrupt networking or weaken host isolation.

## Installer

Prerelease builds provide standard per-machine Windows installers:

- `SecureWall_x64.msi` for normal Intel/AMD 64-bit Windows PCs.
- `SecureWall_arm64.msi` for Windows on ARM.
- `SecureWall_x86.msi` only for 32-bit Windows.

The installer copies the complete runtime, installs and starts the LocalSystem service, and starts the tray controller for interactive installations. It refuses to install while TinyWall is present. Repair and modification are rejected.

To upgrade from v0.5.0 or later, run the newer MSI for the same architecture at the computer's own keyboard and screen. Setup stops SecureWall, replaces its files in the same directory and starts the new version; rules, settings and the password are kept. All connections to other computers stay blocked until the new version starts, usually for less than a minute, and open connections drop. If anything fails, setup rolls back to the previous version and its policy; if that version then cannot start, connections stay blocked until a restart or `sc start SecureWall` from an elevated prompt. v0.4.2 and older cannot be upgraded in place: uninstall them in Settings > Apps first, then install the new version. That is a one-time step; [docs/SECURITY.md](docs/SECURITY.md#in-place-upgrade) has the details and the recovery steps.

Alpha installers are unsigned. Windows may show an unknown-publisher warning. Verify the MSI against the release's `SHA256SUMS.txt` before running it. Test procedures for WFP, reboot and audit-policy behavior are in [docs/TESTING.md](docs/TESTING.md) and [docs/HARDENING-VALIDATION.md](docs/HARDENING-VALIDATION.md).

The persistent and boot-time baseline denies all non-loopback traffic, including DNS and DHCP. It contains no recovery permits. All runtime policy, including saved allows, belongs to the service's dynamic WFP session. Service loss or startup failure withdraws runtime permissions and leaves strict external denial until the service successfully restores policy or explicit removal completes. A running service that has to withdraw its permissions rebuilds its policy in-process; if that keeps failing, it exits with an error code and Windows restarts it after 5, 30 and 60 seconds, then every minute. That automatic restart takes effect from the first Windows restart after installation. A reload that fails before changing any filter keeps the current policy and retries after about 5, 15 and 30 seconds, unless the failure leaves network addresses unverified or a grant already out of date; then it withdraws permissions and rebuilds. See [docs/SECURITY.md](docs/SECURITY.md).

Address renewal and name resolution can fail during this interval. If the cause persists, recovery requires a local console. Extracted bundles are not supported install locations; privileged registration requires a protected Program Files tree.

The MSI stores `profiles.json` and `hosts.bck` in `INSTALLDIR/data-defaults`. Its SYSTEM `/install` entry validates the machine-data tree, then copies each default into `%ProgramData%\SecureWall` when it is absent or differs from the shipped file, so a reinstall delivers database updates. Configuration, password and recovery files are left alone. Unsafe ownership, write permissions or reparse paths cause rejection without repairing or overwriting the existing tree. The default Windows Update profile retains the `wuauserv` service exception and removes the executable-wide `svchost.exe` web permission. Other Windows Update components may need their own rules.

To update, click **Check for updates** in the tray menu, or on the Maintenance tab in Manage. SecureWall reads its own releases on GitHub, checks the release's signature against the release keys built into SecureWall, and shows the newest version it may install with its release notes. **Install** downloads the installer, asks Windows for administrator permission, checks the signature and installer again, and runs the in-place upgrade with setup's own window. **Skip this version** hides that version until a newer one appears. The check runs only when you click it. If SecureWall has a password, it asks for it first. Every SecureWall release so far is a prerelease, so **Include prereleases** on the Maintenance tab is on by default; turn it off to be offered full releases only. Releases published before the update check existed carry no signature and are never offered. SecureWall never contacts TinyWall's update server; the TinyWall updater and its `/updatenow` switch stay removed. [docs/SECURITY.md](docs/SECURITY.md#check-for-updates) describes the checks and their limits.

## Intended prompt behavior

- Normal mode blocks inbound and outbound traffic unless an explicit exception applies.
- TinyWall's Learning (Autolearn) mode is removed; these prompts replace it. A saved or imported configuration that names Learning starts in Normal mode. Exceptions learned by earlier builds stay until you delete them.
- Only outbound drops caused by SecureWall's default-block filters may produce prompts.
- **Allow outgoing** creates a permanent outbound TCP/UDP exception for the service-owned subject represented by the prompt token.
- **Ignore** adds the app to the Ignored list in the saved profile (Manage, Ignored), so its later blocks do not prompt. The entry adds no WFP filter and grants no traffic; the app stays blocked. While SecureWall is locked, Ignore asks for the password; a cancel or wrong password adds nothing and only stops prompts for that app for five minutes.
- Closing the prompt or prompt expiry leaves policy unchanged.
- Explicit user blocks, blocklists, inbound drops, and ambiguous shared-service identities are never converted into broad allow rules.
- A service whose SID type is NONE, or a program the service inventory cannot rule out as a service, is shown with Allow disabled and the reason; see [docs/SECURITY.md](docs/SECURITY.md).
- The optional AI **?** lookup is off by default. Turning it on (SecureWall must be unlocked) adds a Normal-mode permit for `SecureWall.exe` limited to outbound TCP port 443 from signed-in (interactive) accounts; the LocalSystem service never matches it. Do not add a manual exception for `SecureWall.exe`: that would also cover the service. The endpoint must use HTTPS port 443; other ports and a system proxy on another port are not supported. Answers are labeled AI-generated, size-capped, and never change policy.

The implementation and verification plan is in [docs/superpowers/plans/2026-07-14-securewall.md](docs/superpowers/plans/2026-07-14-securewall.md). The threat model and architecture are in [docs/design/2026-07-14-securewall-design.md](docs/design/2026-07-14-securewall-design.md).

## Build and test

SecureWall targets .NET Framework 4.8 and must be compiled with full Visual Studio MSBuild because it uses COM references. Run the dependency-free prompt/security harness first:

```powershell
dotnet run --project tests\SecureWall.Core.Tests\SecureWall.Core.Tests.csproj
& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" TinyWall\TinyWall.csproj /t:Build /p:Configuration=Debug /p:RestorePackages=false
```

The debug-only `/protocolselftest`, `/pipeintegrationtest`, and `/promptpreview` switches verify IPC serialization, authenticated pipe exchange, and the synthetic popup without starting the service or changing WFP. WiX 3.14.1 is needed to build the MSI; `tools\release\Build-SecureWallRelease.ps1` creates all three installers and their SHA-256 manifest without installing anything. `tools\vm\Prepare-SecureWallVmBundle.ps1` packages the guarded real-firewall validation runner for an expendable Windows VM. Full commands and the privileged VM matrix are in [docs/TESTING.md](docs/TESTING.md). Security and recovery rules are in [docs/SECURITY.md](docs/SECURITY.md).

## Lineage and license

SecureWall is based on TinyWall 3.5.1, pinned to upstream commit `1df71b146d01d734d5b5a45a814b29e6a073f4d0`. See [UPSTREAM.md](UPSTREAM.md) for provenance and modification notes.

The firewall application and this combined work are licensed under GNU GPL version 3; see [LICENSE](LICENSE). Starter automation retained in `.claude/`, `.agents/`, and `.codex/` remains under its separately preserved terms in [LICENSES/STARTER-MIT.txt](LICENSES/STARTER-MIT.txt) and any per-skill notices.

SecureWall is not TinyWall and is not endorsed by TinyWall's author.

### Installation recovery and logs

When setup fails, its last page has two buttons: **Open SecureWall log** opens `%ProgramData%\SecureWall\logs\installer.log`, and **Open setup log** opens the Windows Installer log that every SecureWall setup writes to `%TEMP%` (`MSI*.LOG`). Each opens in your default text viewer, so Ctrl+A and Ctrl+C copy the whole log for a report. After a failed `/install`, `installer.log` also holds the service's status and exit codes and the `service.log` entries written during that attempt.

Optional diagnostic logging can be enabled in Settings for a normal-use trial and turned off later. See [live testing](docs/LIVE-TESTING.md), the [coverage map](docs/DIAGNOSTIC-COVERAGE.md), and the [local support-bundle collector](tools/diagnostics/README.md). Reports separate what was observed from what the logs cannot show.

A clean TinyWall-only migration requires uninstalling TinyWall and rebooting before installing SecureWall. A previous SecureWall build may leave an unsafe `%ProgramData%\SecureWall` tree that this version must reject. Generic MSI failure can indicate that rejection; the Windows Application log then holds an Error from source `SecureWall` with the reason. Stop, preserve the directory and installer diagnostics, and use trusted manual recovery from a local console. Do not change ACLs or blindly restore writable legacy configuration to bypass validation. See [legacy data and controller recovery](docs/SECURITY.md#legacy-data-and-controller-recovery) for the migration boundary and MSI diagnostic limit. Export TinyWall's settings to a `.tws` file and copy `%ProgramData%\TinyWall` before removing it; the rollback order is in [live testing](docs/LIVE-TESTING.md#rolling-back-to-tinywall).

If an installed SecureWall rejects that tree, the service stops starting while its deny baseline keeps blocking external traffic, and `SecureWall.exe /uninstall` refuses to run. Remove SecureWall from Settings > Apps at the local console. MSI removal runs a SYSTEM [emergency release](docs/SECURITY.md#emergency-release-when-machine-data-is-rejected) that restores Windows Firewall and audit state from their registry journals, deletes only SecureWall's own firewall objects and service, leaves the hosts file and the rejected tree in place without reading file contents from it, and reports its warnings on stderr and, while the service's event source exists, in the Application event log. Each failed release step writes Application event 1001 from source `SecureWall` naming the step and the exception, and a failed release adds one 1001 summary with the exit code. Disabling the service no longer releases the baseline.

The controller can request a start of an existing validated service. Missing services require MSI recovery through full removal and fresh installation; controller startup does not install a service. Ordinary interactive controller logs are in `%LocalAppData%\SecureWall\logs`; elevated controller and service/installer logs remain under guarded `%ProgramData%\SecureWall\logs`.
