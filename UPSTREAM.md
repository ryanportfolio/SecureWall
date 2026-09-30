# Upstream provenance

SecureWall is a modified version of [TinyWall](https://github.com/pylorak/TinyWall):

- Upstream release: 3.5.1 (tag `rel-3.5.1`, 2026-06-07)
- Upstream commit (pin): `1df71b146d01d734d5b5a45a814b29e6a073f4d0`
- Upstream head last reviewed: `62b088f` (2026-08-15), confirmed unchanged on 2026-09-29 (no newer commits, tags or releases on `pylorak/TinyWall` master)
- Upstream author and copyright holder: Károly Pados and other contributors recorded by the TinyWall repository
- Upstream license: GNU General Public License version 3
- Import date: 2026-07-14

The unmodified upstream README, changelog, future-ideas file, and GPL text are retained as `UPSTREAM-README.md`, `UPSTREAM-CHANGELOG.txt`, `UPSTREAM-FUTURE-IDEAS.txt`, and `LICENSE`.

## How the fork relates to upstream

The fork is a source snapshot of the pinned commit, not a git branch of it. This repository shares no commit history with `pylorak/TinyWall`, so `git merge` and `git rebase` against upstream are impossible. Upstream fixes are carried over by hand: read the upstream diff, apply the equivalent change to the fork's files, add tests, and record the upstream commit id in the commit message and in the list below.

## Upstream commits since the pin

All 58 upstream commits in `1df71b1..62b088f` are listed once below: 27 backported, 3 equivalent in fork, 1 not backported, 12 not applicable, 15 cosmetic or tooling.

### Backported

Each entry names the upstream commit or issue and where the change landed in the fork.

- `bfd90bc`: higher-contrast active tab header in dark mode (`TinyWall/DarkModeCS.cs`, taken unchanged).
- `54d1aea`: dark-mode ListView icon sizing (`TinyWall/DarkModeCS.cs`, taken unchanged).
- `d976ce5`: comment above `ENC_SALT` in `TinyWall/ServerConfiguration.cs` on why the config is only obfuscated. Reworded for the fork: the machine-data directory ACL, not the key, prevents modification.
- `b741831`: password hash format `Rfc2898-TWv352` (16-byte `RandomNumberGenerator` salt, PBKDF2-HMAC-SHA256, 200,000 iterations, 32-byte hash) in `pylorak.Utilities/Pbkdf2.cs` and `PasswordLock` (`TinyWall/Settings.cs`). Adapted: the legacy `Rfc2898` format (8-character salt, SHA-1, 150,000 iterations, 16 bytes; 3.5.1 and SecureWall up to 0.3.0) still verifies and is rewritten in the new format after a successful unlock; parsing is bounded (512 characters, iterations 100,000 to 10,000,000, salt 8 to 64 bytes, hash length per algorithm); the parser is picked by tag; the comparison is constant-time and case-sensitive.
- `6bf5772`: 32-bit ImageLists in the `AppFinderForm`, `ConnectionsForm`, `Processes`, `SettingsForm` and `UwpPackagesForm` designers.
- `011c33d`: slightly larger dark-mode list icons (`TinyWall/DarkModeCS.cs`, taken unchanged).
- `d9b5c62`: dark-mode focus highlight alignment (`TinyWall/DarkModeCS.cs`, taken unchanged).
- `fa57ba0`: `MpCmdRun.exe` added to the Windows Defender special exception (`TinyWall/Database/SpecialApplications/Special Windows Defender.json`) and hand-edited into the shipped `MsiSetup/Sources/CommonAppData/SecureWall/profiles.json`, whose only generator is the develtool GUI. `MpDefenderCoreService.exe` left out, as upstream did. `tests/installer/Test-SecureWallInstaller.ps1` now checks that the payload matches the source profiles. Reaches fresh installs only: `MachineDataGuard.InstallDefaults` seeds `profiles.json` only when it is absent.
- `7270d96`: `Utils.DisableMessageUIPI("TaskbarCreated")` at the start of `InitController` (`TinyWall/Utils.cs`, `TinyWall/TinyWallController.cs`), so an elevated controller re-adds its tray icon after Explorer restarts.
- `7fce29c`: `Utils.StartProcessAndForget` disposes the `Process` for the Connections web lookups and `mnuElevate_Click`. The unreachable `UpdateChecker.InstallUpdate` still calls `StartProcess`, as upstream left it.
- `3448b6b`: backported into currently disabled code. Update downloads (application database, hosts file) are decompressed and hashed in memory instead of through shared temp files (`TinyWall/TinyWallService.cs`, `TinyWall/Utils.cs`, `TinyWall/HostsFileManager.cs`). The only caller, `UpdaterMethod`, returns at once because `SecureWallProduct.UpdateFeedEnabled` is `const false`.
- `a56488f`: `UserIdFilterCondition` SDDL parenthesis fixed (`pylorak.Windows.WFP/FilterCondition.cs`). Latent: the class has no callers.
- `63e411a` (password-file part): `SecureTemp.ProtectFile` became `TinyWall/Installer/SecretFileProtection.cs` (`Ensure` replaces the DACL of an existing file, `CreateNew` creates a file already protected). The updater part is not applicable.
- `3146d95`: `pwd` gets the protected DACL `MachineDataPolicy.SecretFileDacl` (`D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x120080;;;BU)`), set on the temporary file at creation and re-applied to an existing `pwd` at service start and before each write. Adapted: Users keep READ_CONTROL, SYNCHRONIZE and FILE_READ_ATTRIBUTES (no read-data) so a standard-user controller can still run `MachineDataGuard` validation; upstream grants Users nothing. Before this port `pwd` inherited the data directory's BUILTIN\Users read grant (`TinyWall/Installer/MachineDataGuard.cs`), so any local user could copy the hash.
- `7762731` (password-file part): the service holds `pwd` with `FileShare.None` (`LockPasswordFile` in `TinyWall/TinyWallService.cs`) and unlocks it around `UNLOCK` and `SET_PASSPHRASE`. The database `UserAccess.ReadOnly` part matches the data directory ACL; the `AppPaths` and `FilesystemProtection` renames do not apply.
- `c4cd972`: `Utils.IsDarkModeActive` accepts a null `ControllerSettings` and falls back to the system theme, so `PasswordForm` works in a standalone `/uninstall` of a locked install. Adapted: null-safe instead of upstream's catch-all. The MSI path (`/msi-cleanup`) never shows a dialog.
- `8f45054`: `Transaction.Abort` treats `FWP_E_NO_TXN_IN_PROGRESS` (`0x8032000D`) as closed (`pylorak.Windows.WFP/Transaction.cs`), so `Dispose` no longer replaces the original error or leaks the engine handle reference. Commit handling is unchanged. The only item of the WFP-wrapper group reachable from live code.
- `c2ae8df`: `FilterEnumeratorBase` owns and disposes the provider GUID handle, including when enum-handle creation fails (`pylorak.Windows.WFP/FilterEnumerator.cs`, `pylorak.Windows.WFP/Engine.cs`).
- `2329049`: checked `CopySid` in `PInvokeHelper.CopyNativeSid` and a refcounting indexer setter in `FilterConditionList` (`pylorak.Windows.WFP/`). Both latent: no fork code reaches them. Adapted: the setter reads the old item first, so a bad index throws before any count changes.
- `9a25ffb`: "Search on ProcessLibrary" menu item, handler and resx entries removed (`TinyWall/ConnectionsForm*`).
- `7bfbcc3`: copy icon (`TinyWall/Resources/img/copy.png`, byte-identical to upstream) on the Connections copy items.
- `7e09aac`: "Copy path" and "Open folder" in the Connections menu. Adapted in `TinyWall/ProcessPathActions.cs`: Open folder accepts only drive or UNC paths, runs `explorer.exe /select,"<path>"` without shell execute, and falls back to the `explore` verb on the folder, so it cannot run the file; the list refresh pauses while the menu is open. `open_folder.png` is from upstream, with the Icons8 credit in `MsiSetup/Sources/ProgramFiles/SecureWall/Attributions.txt`. Upstream's `search.png` redraw not taken.
- `cf81bb8`: `DarkModeCS` built with `(this, false)` in the eight forms, so context-menu icons keep their colors. `TinyWall/DarkModeCS.cs` now equals upstream `cf81bb8`.
- `53800aa`: `CancelButton = btnCancel` in `TinyWall/AppFinderForm.Designer.cs`, so ESC cancels Application Finder.
- `2ed2d37`: block reason in Network Activity. Adapted: the service records each registered filter's group from its weight (`TinyWall/Prompting/FilterGroupMap.cs`), publishes the map only after commit, and sends the group in `FirewallLogEntry.FilterGroup`. Unmapped filter IDs show a plain "Blocked", not upstream's "by 3rd party app". Strings are English only. Not taken: moving the net-event subscription into a try/catch (a subscribe failure stays fatal to service start), the State column reorder, the event enum rename.
- `ac3b725` (atomic-write substance only): `TinyWall/AtomicFileWriter.cs` writes a `CreateNew` temp file beside the target, flushes it to disk, and swaps it in with `File.Replace`; `HostsFileManager.WriteAndRelock` relocks the hosts backup in a catch after a failed write. Not taken: the hosts file renames (the fork keeps the 3.5.1 names) and the empty custom hosts guard (the shipped `hosts.bck` is not empty).
- `62b088f`: bounded read of the WFP net-event `appId` blob. `NetEventSubscription.ReadAppIdPath` reads at most `size / 2` UTF-16 units and stops at the first null (`pylorak.Windows.WFP/NetEventSubscription.cs`).
- Issue #128 items 1, 2 and 3: the shared `StringBuilder` is serialized by `lock (SBuilder)`; native net-event callbacks (`NativeCallbackHandler0`, `NativeCallbackHandler1`, `WfpNetEventCallback`) wrap their body in `try/catch` so no managed exception propagates into FWPUClnt's RPC thread; appId length is bounded as above. Item 4 (IPv6 formatting) does not affect matching; see `487a0eb` below.
- Issue #128 item 5: the fork first fixed the salt from the issue text in `Utils.RandomString` (`RandomNumberGenerator` with rejection sampling). Upstream fixed it differently in `b741831`, and that port now supplies the salt. `Utils.RandomString` has no callers left.

### Equivalent in fork

- `9810b48`: release build script. The fork has its own pipeline (`MsiSetup/PrepareSources.ps1`, `.github/workflows/ci.yml`, `.github/workflows/release.yml`).
- `67ed774`: `FileLocker.TemporaryUnlock`. The fork unlocks and relocks explicitly in try/finally (`TinyWall/TinyWallService.cs`) and through `WriteAndRelock` (`TinyWall/HostsFileManager.cs`).
- `0cbd3e9`: deliberately different. Instead of uninstalling and retrying, the fork refuses a service pending deletion and validates the registered image (`TinyWallDoctor.EnsureServiceInstalledAndRunning`).

### Not backported, relevant

- `487a0eb`: net-event callbacks skip the address-to-string conversion. Intentionally not ported: it is a performance change only, and the prompt correlator consumes string addresses (`TinyWall/Prompting/DropCorrelator.cs` parses both sides with `IPAddress.TryParse`). Still open as a fork-only fix: the prompt shows IPv6 addresses uncompressed and without brackets (`TinyWall/Prompting/BlockedConnectionPopup.cs`).

### Not applicable

Dormant updater. `SecureWallProduct.UpdateFeedEnabled` is `const false`, so this code never runs, but it is still compiled in and still points at TinyWall's feed (`UpdateChecker.URL_UPDATE_DESCRIPTOR`, `https://tinywall.pados.hu/updates/UpdVer{0}/update.json`). Delete the updater, or port all five commits and switch to a SecureWall-owned URL before enabling any feed:

- `30c1712`: Authenticode check on the downloaded update installer. `UpdateChecker.cs` still runs the MSI from `%TEMP%` unchecked.
- `72ee63a`: updater as a separate startup command that requires admin. `/updatenow` is still parsed.
- `bf868d6`: `WebClient` instance rename in the updater.
- `f264655`: no temp file for the update descriptor. `UpdateChecker.cs` still uses `Path.GetTempFileName()`.
- `e0e7e22`: ACL on downloaded update installers.

Other features the fork does not have:

- `a84e08e`: PAD file images. The fork publishes no PAD metadata (`docs/pad_file.xml` still describes TinyWall).
- `7ed518b`: cleanup of the `temp_secure` folder. The fork has no such folder.
- `a3418c4`: `SecureTemp` class for the updater's admin-only temp folder. Updater-only; the fork writes temp files beside their targets (`TinyWall/AtomicFileWriter.cs`), and its data directory grants Users read, so this is not an equivalent.
- `9cfab8e`: log WFP exceptions during service dispose. The fork does not delete WFP objects on dispose; its dynamic filters disappear when the engine handle closes.
- `418ff8d`: ideas file update. `UPSTREAM-FUTURE-IDEAS.txt` is a frozen snapshot.
- `1ba7a3d`: thread-safe `FileLocker`. The fork uses `FileLocker` only on the service worker thread and in dispose.
- `624b628`: missing-directory handling in `AppPaths`. The fork has no `AppPaths`.

### Cosmetic or tooling, not taken

No effect on the shipped product: develtool CLI and command-line refactor, build scripts and solution config, C# 11 (the fork stays on `LangVersion` 9.0 and no port needed newer syntax), icon cleanup and rename, whitespace, `.gitignore` (the fork commits the compiled `profiles.json` by design), resx `Version=2.0.0.0` designer metadata, and designer-code refactoring.

- `ee0a43e`, `521ea28`: resx references to Windows.Forms v4.
- `54bdc54`: whitespace.
- `224fdb5`: ignore compiled `profiles.json`.
- `56ad092`, `711335d`, `82f7b1d`, `6d83bfb`, `bb6a40a`, `d410eef`: develtool CLI, C# 11, console attach, command-line parsing, `/compare`, GUI develtool removal. The fork keeps the GUI develtool, which is still the only generator of `profiles.json`.
- `932160f`, `28334eb`: solution build configuration, build script refactoring.
- `83ca9de`, `1c52415`: icon cleanup, rename to `TinyWall.ico`.
- `97ba990`: `TinyWallController` designer-code refactoring.

## Upstream issues and pull requests to watch

The maintainer says TinyWall 3.6 is in progress and unpublished (#127, 2026-08-18). Expect a large batch of commits at once.

- #129 (issue): whitelist occasionally ignored with a NordVPN killswitch; no root cause upstream. A VPN's own WFP filters can block traffic SecureWall allows.
- #127 (issue): Defender blocked with Windows Update off (fixed by `fa57ba0`, ported) and versioned Defender and UWP paths (still open).
- #59, #136 (issue, PR): the UWP picker cannot allow full-trust MSIX apps such as Spotify or Arc, because they carry no AppContainer package SID. The fork has the same picker; allow such apps by executable. #136 warns in the picker; the maintainer prefers filtering the list.
- #133 (PR): follow exceptions across versioned folders and MSIX updates, gated on a valid signature. The fork has the same exact-path limit.
- #142 (PR): wildcard path rules; the maintainer said "let's roll with this one" (2026-09-26). Decide intent before porting: wildcards widen what a rule matches, against the fork's exact-identity prompt model.
- #132 (issue): timed auto-learn; the maintainer plans a forced Learning timeout. SecureWall's Learning mode has no timeout; it ends only when the user switches mode or the service restarts (including at reboot), because Learning is never saved as the startup mode.
- #141 (PR): move the process-start watcher from WMI to ETW. The fork uses the same WMI watcher for child inheritance; the maintainer objects to parts of the design.
- #121 (issue): TinyWall 3.5.1 fails to uninstall while password-locked. On switch day, unlock TinyWall before uninstalling it, then reboot; the SecureWall installer refuses to run while TinyWall is installed.

## SecureWall modifications

SecureWall uses a distinct product name, executable identity, Windows service name, named pipe, scheduled task, application-data directory, and WFP provider GUID. Its principal functional change is a fail-closed, rate-limited outbound-drop prompt pipeline with service-owned attribution and authorization tokens.

Source-file namespaces and some internal class names remain `pylorak.TinyWall` to keep the fork reviewable and reduce unnecessary divergence. They do not identify the installed product.

SecureWall has no configured binary update feed. It must not install binaries advertised by TinyWall's upstream update service. The dormant updater code still hard-codes TinyWall's feed URL (see "Not applicable" above).

SecureWall is not TinyWall and is not endorsed by TinyWall's author.
