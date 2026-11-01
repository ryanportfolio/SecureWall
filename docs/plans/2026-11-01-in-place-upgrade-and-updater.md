# In-place MSI upgrade and "Check for updates": design plan

Baseline: `de7ac3dc` (v0.4.2). Status: approved by the owner on 2026-11-01 with the recommended answers below. Nothing here is built, installed or run yet.

Today an upgrade means uninstalling SecureWall in Settings > Apps and installing the new MSI. Between the two the PC has no SecureWall policy at all: `/msi-cleanup` releases the persistent deny baseline, the Windows Firewall compatibility rules and the service. This plan replaces that with two features, built in this order:

1. **In-place upgrade.** Running a newer `SecureWall_<arch>.msi` over an installed SecureWall upgrades it. The persistent deny baseline stays in WFP for the whole swap, so the PC is never unfiltered; external traffic is blocked only for the seconds between the old service stopping and the new one committing its policy.
2. **Check for updates.** A tray and Manage action finds the newest signed SecureWall release on GitHub, shows its notes, downloads and verifies the MSI, and starts the in-place upgrade behind a UAC prompt.

The updater depends on the upgrade path, so it is built second.

## Part 1: in-place MSI upgrade

### What blocks it today

| Where | What it does | Effect on an upgrade |
|---|---|---|
| `Product.wxs` launch condition `NOT UPGRADINGPRODUCTCODE` | Rejects a package run as the "old product" inside another package's upgrade | Windows Installer runs the **old** package's cached execute sequence, LaunchConditions included, when a new package removes it. Every installed SecureWall up to v0.4.2 therefore fails its own removal, and the upgrade rolls back. This cannot be fixed from the new package (see "Versions up to v0.4.2" below). |
| Launch condition on `OLDER_UPGRADEABLE_FOUND`, `SELF_FOUND`, `NEWER_FOUND` | Refuses to run when any related product exists | Must allow the older range. |
| Launch condition on `EXISTINGSECUREWALLSERVICE` | Refuses a fresh install while a SecureWall service exists | Must allow it when an upgradeable SecureWall product owns that service. |
| `Upgrade` table, all rows `OnlyDetect='yes'`, no `RemoveExistingProducts` | Detection only | The older range must remove the old product. |
| `RegistryKeys` component, `ForceDeleteOnUninstall='yes'` on `HKLM\Software\SecureWall` | Deletes the whole key on product removal | The Windows Firewall journal (`SOFTWARE\SecureWall\FirewallRecovery`) and audit journal (`SOFTWARE\SecureWall\AuditRecovery`) live under it. Removing the old product during an upgrade would delete them while the compatibility rules they describe stay in place, so a later uninstall could no longer restore the original notification settings or find SecureWall's owned rules. |
| `InstallCustomRollback` = `/msi-rollback-install` on `NOT Installed` | Failed first-install teardown: disables and deletes the service, removes the baseline | A new package is "NOT Installed" during an upgrade too. Its rollback would tear down a working installation. |
| `StartControllerAndDetect` = `/autowhitelist` on `(NOT Installed) AND UILevel=5` | First-install profile detection | Would also run on every full-UI upgrade. `AutoWhitelist()` merges every detected profile into the active profile, which can widen existing exceptions. |
| `INSTALLDIR` default only | Program Files\SecureWall | A custom directory chosen at first install would be ignored, and `/install` would then refuse because the registered service `ImagePath` belongs to the old path. |
| No service stop before file removal | The service and every user's tray controller run `SecureWall.exe` from `INSTALLDIR` | File replacement would hit in-use files and end in a reboot-required (3010) result with mixed binaries. |

What already works in our favour:

- The old package's `UninstallCustom` (`/msi-cleanup`) is conditioned `NOT UPGRADINGPRODUCTCODE`, so an upgrade-time removal of a v0.5+ package never releases protection.
- The service is registered by `SecureWall.exe /install` through `ManagedInstallerClass`, not by the MSI `ServiceInstall` table, so removing the old product does not delete the service registration. Its `ImagePath` (`"<INSTALLDIR>\SecureWall.exe"`) stays valid when the new files land in the same directory.
- The persistent deny baseline's provider has no `serviceName`, so it survives the service stopping, and `RegisterRestrictiveBaseline` replaces it atomically (delete plus re-register in one WFP transaction) when the new service starts.
- `/install` on a registered, stopped service already validates the registration, runs the machine-data guard, refreshes shipped data (`MachineDataGuard.InstallDefaults`), reapplies health settings and starts the service.

### Versions up to v0.4.2

Their cached packages contain the `NOT UPGRADINGPRODUCTCODE` launch condition, so no later MSI can remove them as part of an upgrade. The workarounds (re-caching a patched copy of each old package under its random per-build ProductCode, or a patch per architecture) cost more than they save for a prerelease with one known installation.

Decision proposed: v0.5.0 is the first upgrade-capable release. `OldestUpgradeableVersion` becomes `0.5.0`. An installed v0.4.2 or older matches `OLDER_INCOMPATIBLE_FOUND`, and the existing `FoundIncompatibleVersion` error tells the user to uninstall it first. The move to v0.5.0 is the last manual uninstall-and-install; v0.5.0 to any later version is in place. The v0.5.0 release notes say so.

### Upgrade transaction

`RemoveExistingProducts` (REP) is scheduled immediately after `InstallInitialize`, so the old product's removal and the new product's installation share one transaction and one rollback script. The alternatives were rejected: REP after `InstallValidate` runs the removal in its own transaction that cannot roll back if the new install fails; REP after `InstallExecute` installs new files over in-use old ones and depends on unversioned files (`profiles.json`, `hosts.bck`, `SecureWall.exe.config`) being replaced by file-versioning rules.

Each side of the swap is handled by the version that knows it:

- **The old package stops itself.** A v0.5+ package carries an upgrade-only deferred SYSTEM action, conditioned `REMOVE="ALL" AND UPGRADINGPRODUCTCODE`, sequenced before its `RemoveFiles`. It runs the installed (old) `SecureWall.exe` while that file still exists. Its rollback twin restarts the old service after Windows Installer restores the old files.
- **The new package starts itself.** After `InstallFiles`, the new package runs its own `SecureWall.exe` to start the service. Its rollback twin stops the new service before Windows Installer removes the new files.

The other option, the new package stopping the old service before REP, needs an executable that is neither the old installed file nor a not-yet-installed new file: a helper in the MSI `Binary` table, running from the installer's temporary folder, outside the protected-installation checks the service image validation relies on. It would also place deferred actions between `InstallInitialize` and REP, which ICE63 flags. Letting each version handle its own side keeps every privileged step on an authenticated Program Files image. The cost: a v0.5.0 bug in its upgrade-stop step cannot be fixed by a later package. Such a bug fails the upgrade closed (rollback, old version keeps running), and manual uninstall-and-install remains the fallback.

#### Sequence

Execute script of an upgrade from version N (old, at least 0.5.0) to N+1 (new). The transaction ID passed to every step is the new package's ProductCode (`UPGRADINGPRODUCTCODE` inside the old package, `[ProductCode]` inside the new one); it ties a snapshot to exactly one upgrade.

| # | Package | Action (deferred, SYSTEM unless noted) | What it does |
|---|---|---|---|
| 1 | new | `InstallInitialize` | |
| 2 | old (inside REP) | rollback: `/msi-upgrade-restore <txn>` | Registered first so it runs last in rollback, after old files are back. |
| 3 | old | `/msi-upgrade-stop <txn>` | (a) Runs the machine-data guard and authenticates the installation and service registration exactly as `/msi-cleanup` does; a rejected tree or a foreign `ImagePath` fails here, before anything changes. (b) Requires MpsSvc running and BFE running, so the new service can start. (c) Writes the upgrade-in-progress marker (see "Blocking other service starts"). (d) Disables SCM restart-on-failure so a crash mid-swap cannot start half-installed binaries; the startup type stays Automatic so a reboot still brings up whatever service is present. (e) Stops the service gracefully with the existing bounded `ServiceLifecyclePolicy` wait; the deny baseline now enforces alone. (f) Terminates this installation's controllers (`TerminateInstallationControllers`) so no file is in use. (g) Only now, with no service or controller left to write policy, snapshots the files the service writes (`config`, its recovery journal, and any other policy file the service saves) into `%ProgramData%\SecureWall\upgrade\<txn>\` and flushes them. The running service saves `config` inside `ApplyConfiguration`, so a snapshot taken before the stop could capture a half-applied change or drop one the service already acknowledged. Returns non-zero on any failure, which aborts the upgrade before files change. |
| 4 | old | `RemoveFiles`, `RemoveRegistryValues` | Old payload removed. `HKLM\Software\SecureWall\InstallDir` only; journals untouched (see "Registry ownership"). |
| 5 | new | `InstallFiles`, `WriteRegistryValues` | New payload into the same `INSTALLDIR`. |
| 6 | new | rollback: `/msi-upgrade-abort <txn>` | Stops the new service if it is running (bounded), disables its restart-on-failure, terminates controllers. Leaves the registration and baseline. |
| 7 | new | `/msi-upgrade-start <txn>` | Requires the service registered and Stopped, its `ImagePath` equal to this executable, and the snapshot for `<txn>` present (proof that step 3 ran). Runs the machine-data guard and the shipped-data refresh (as `/install` does today), reapplies `EnsureHealth` (Automatic start, restart policy, controller logon task), starts the service, and waits for Running within `ServiceLifecyclePolicy.StartupTimeout`. Fails if the service exits or does not report Running. |
| 8 | new | `InstallFinalize` | Transaction committed. |
| 9 | new | commit: `/msi-upgrade-commit <txn>` | Deletes the snapshot directory. |
| 10 | new | commit, impersonated: `StartController` (no `/autowhitelist`) | Relaunches the tray for the installing user when `UILevel=5`. |

Rollback after a failure at step 5 to 7 runs in reverse: step 6 stops the new service, Windows Installer removes the new files and restores the old product's files and registration, then step 2 (`/msi-upgrade-restore`, old executable) restores the snapshot byte for byte, re-enables restart-on-failure, runs the old version's own shipped-data refresh (so `profiles.json` and `hosts.bck` return to the old bytes) and starts the old service. A failure at step 3 aborts with nothing changed except, possibly, a stopped service, which step 2 restarts.

Readiness: SCM Running is the success signal the current `/install` uses. Building step 7 includes confirming in source that the service reports Running only after its first policy commit; if it does not, step 7 waits for a committed-policy signal over the authenticated pipe instead.

#### Blocking other service starts

Disabling SCM restart-on-failure is not enough. Today a tray controller that cannot reach the service calls `TinyWallDoctor.EnsureServiceInstalledAndRunning` (`TinyWallController.cs` around line 1394), which starts a stopped service, through `sc.exe start` for a standard user. A tray launched by another user's logon during the swap could start the old binary before its removal or the new binary before step 7, locking files and breaking step 7's stopped-service precondition.

Step 3 therefore writes a SYSTEM-only marker, `HKLM\SOFTWARE\SecureWall\UpgradeInProgress = <txn>`, before it stops anything. Every automatic start path (controller repair, the controller's reconnect timer, `/install` outside the upgrade modes) refuses to start the service while the marker is present and Windows Installer is running an installation (the `Global\_MSIExecute` mutex is held), and the controller shows "SecureWall is being upgraded" instead of its repair error. Step 7 and step 2 are the only starters that accept the marker, and only for their own transaction ID. The commit step clears it after success, and step 2 clears it after starting the old service on rollback; step 6 leaves it, because the old files are not back yet when it runs. A marker left by a power loss is ignored once no installation is running, so SCM's boot start and manual `sc start` recovery are never blocked. SCM's own Automatic start at boot does not read the marker at all.

#### Filtering across the swap

- From step 3(e) until step 7's policy commit, only the persistent and boot-time deny filters are active. Unknown and previously allowed external traffic is blocked in both directions, DNS and DHCP included; loopback works. This is the same state the "Service stop/crash" validation case already requires.
- At step 7 the new service atomically replaces the baseline and commits its dynamic session. The provider key is stable, so the new version deletes every object the old one created. Rule for every future release: `DeleteWfpObjects` must remove every object any upgradeable version can have registered under the SecureWall provider, including sublayers a later version stops using.
- Windows Firewall compatibility rules, the hosts file protection and their journals stay as they are; `/msi-cleanup` never runs. The audit-policy lease is released when the old service stops and reacquired when the new one starts, which is today's stop/start behaviour.
- Pending prompts, prompt tokens and runtime-only grants (timed exceptions in memory, the "until reboot" state) are lost, exactly as on a service restart. Saved exceptions return with the new service.
- Expected blocked window: service stop plus file swap plus start, tens of seconds. Open connections drop. The full-UI MSI and the updater both say so before starting.

#### Failure outcomes

| Failure | Result |
|---|---|
| Guard, registration, MpsSvc or stop failure at step 3 | Upgrade aborts. Old service keeps running, or is restarted by step 2 if it had already stopped. |
| File copy, registry or new-service start failure | Full rollback to the old version, old policy snapshot and old shipped data; old service running. |
| Rollback's old-service start also fails (step 2) | Service stays stopped, baseline blocks external traffic. Not permanent: the service is still Automatic, so a reboot retries it, and `sc start SecureWall` from an elevated prompt retries at once. The MSI error and Application event 1001 name the step. This is the case to watch in the VM matrix. |
| Power loss or hard reset mid-swap | The boot-time and persistent baseline block external traffic at boot. The Automatic service starts whatever binary is present; if that fails, traffic stays blocked until local-console recovery. Windows Installer rolls back a suspended installation the next time an MSI runs. VM case U8 records the actual behaviour at each stage. |
| New service crashes after the MSI committed | Normal SCM restart policy; baseline holds meanwhile. Same as any crash today. |

Nothing in this design opens the network to recover connectivity; every failure ends either in a running SecureWall or in the deny baseline.

#### Machine data and the guard

- No MSI table ever touches `%ProgramData%\SecureWall`; only the guarded SYSTEM entry points do, as today.
- Steps 3, 2 and 7 each run the full-tree guard before reading or writing machine data. A rejected tree fails step 3 before the stop, so a hostile tree can never trigger a swap. The emergency release path (`ReleaseForMsiWithRejectedMachineData`) stays reserved for explicit removal and failed first installs.
- The snapshot lives inside the guarded tree, in a subdirectory created by SYSTEM that inherits the protected ACL. `MachineDataPolicy` already validates every descendant, so nothing new needs trusting. A snapshot whose transaction ID matches no running upgrade is never restored; the next successful start or commit deletes it.
- Configuration format: the new version may read and rewrite the old format. Rollback never asks the old version to read a newer file, because it restores the snapshot first. This removes the need for downgrade-compatible configuration.
- Shipped-data refresh: unchanged. The new `/msi-upgrade-start` and the old `/msi-upgrade-restore` both go through `MachineDataGuard.InstallDefaults`, so `profiles.json` and `hosts.bck` always match the binaries that are running.

#### /autowhitelist

The upgrade never runs it. `StartControllerAndDetect` becomes `NOT Installed AND NOT OLDER_UPGRADEABLE_FOUND AND UILevel=5` (fresh install only); upgrades use the plain `StartController` action. New application profiles in a refreshed `profiles.json` reach users only through the existing Manage window. Installer contract test: the `/autowhitelist` action's condition excludes the upgrade property.

#### Registry ownership

- The `RegistryKeys` component keeps only the `InstallDir` value and loses `ForceDeleteOnUninstall`. MSI then removes the value and nothing else.
- Explicit removal (`/msi-cleanup`, `/uninstall`, failed-first-install rollback) deletes `HKLM\Software\SecureWall` itself as its last step, after both journals were restored and cleared. If a journal is kept because its restore failed, the key stays, which is the behaviour the journals need anyway.
- A new `Platform` value (`x64`, `arm64` or `x86`, from `$(var.Platform)`) records which MSI is installed. The updater uses it to choose the matching asset.

#### Package changes (`Product.wxs`)

- Upgrade table: the older range `Minimum=0.5.0 IncludeMinimum=yes Maximum=$(ProductVersion) IncludeMaximum=no` loses `OnlyDetect` and gains `MigrateFeatures`. `SELF_FOUND` and `NEWER_FOUND` stay detect-only and keep failing with a message. A manual Upgrade table is kept instead of `<MajorUpgrade>` because WiX's element cannot express the equal-version refusal: an equal-version rebuild has a new ProductCode and would otherwise install side by side.
- Windows Installer compares only the first three version fields. Releases must change one of them; the release check in `release.yml` already ties the tag to `<Version>`, and the installer test asserts the three-part comparison.
- `RemoveExistingProducts` `After="InstallInitialize"`.
- Launch conditions: drop `NOT UPGRADINGPRODUCTCODE` (this is what lets v0.5.0 itself be upgraded later); replace the related-product condition with the detect-only refusals; allow `EXISTINGSECUREWALLSERVICE` when `OLDER_UPGRADEABLE_FOUND`. `/msi-upgrade-start` still proves the service belongs to this installation.
- `INSTALLDIR` from `RegistrySearch` on `HKLM\Software\SecureWall\InstallDir` during an upgrade; the directory dialog is skipped when `OLDER_UPGRADEABLE_FOUND`.
- Fresh-install actions (`InstallCustom`, `InstallCustomRollback`, `StartControllerAndDetect`) gain `AND NOT OLDER_UPGRADEABLE_FOUND`. Upgrade actions from the sequence table are added with the opposite condition. `UninstallCustom` keeps `NOT UPGRADINGPRODUCTCODE`.
- The repair/modify refusal and `NOT RollbackDisabled` stay.
- `FilesInUse` and `MsiRMFilesInUse` dialogs may still appear at `InstallValidate` while tray controllers run, before step 3 terminates them. VM case U12 records whether they do; if so, the fix is an immediate-phase notice instead of the dialog, not killing processes before the user has confirmed.

#### Pending upgrade test hook

Rollback cases need a new version that fails at a chosen step. Release builds must not contain fault injection, so the test MSI is a separate build configuration (`UpgradeFaultTest`) that reads a fault name from a SYSTEM-only registry value and is never attached to a release. The installer test refuses a release artifact compiled with that symbol.

## Part 2: "Check for updates"

### Revisiting the removed updater

The TinyWall updater was removed on purpose (`docs/SECURITY.md` "SecureWall has no update feed...", `.claude/reference/deployment.md` line 7, `README.md` line 29, `docs/TESTING.md` line 18, `UpdateFeedRemovalTests`). The reasons were: it fetched from TinyWall's server (`tinywall.pados.hu`), which must never replace this fork; it downloaded database and hosts files outside the MSI; it ran without a signature. This plan keeps all three bans:

- TinyWall's feed stays banned. The only remote source is `https://api.github.com/repos/ryanportfolio/SecureWall/releases` and the assets it lists.
- `profiles.json` and `hosts.bck` still change only through the MSI's shipped-data refresh. The client downloads three files per update: the signed manifest, its signature and one MSI.
- Nothing installs unless a signature made with a key held outside GitHub verifies.

What changes is the statement "SecureWall has no update client." It becomes "SecureWall has a user-initiated release check against its own GitHub releases, run by the controller and verified with an embedded public key." The doc and test changes are listed under "Docs and tests" below.

### Integrity scheme

A `SHA256SUMS.txt` downloaded next to the MSI proves nothing: whoever can replace one asset can replace both. Three options:

| Option | Protects against | Cost |
|---|---|---|
| **A. Signed manifest, owner-held key (recommended now)** | GitHub account or token compromise, a malicious workflow or action, tampered assets, a hostile CDN | Free. No new dependency: ECDSA P-256 with SHA-256, verified by `ECDsaCng` in .NET Framework 4.8, signed with Windows CNG on the owner's PC. One manual step per release. Does not help SmartScreen or the UAC publisher line. |
| B. Same key as a GitHub Actions secret | Tampered assets after the build, CDN | Automatic, but anyone who can push a workflow to the repository can sign anything. Weaker than A for the threat that matters most here. |
| C. Authenticode code signing | Tampering, and gives Windows a verified publisher (UAC, SmartScreen, `WinVerifyTrust`) | A certificate whose key must sit in an HSM or hardware token (CA/B rules since June 2023). Commercial OV certificates are roughly $200 to $500 a year plus token or cloud-HSM fees. Microsoft's Azure Artifact Signing (formerly Trusted Signing) is about $10 a month and works in CI, but requires identity validation, and individual developers were eligible only in the US and Canada as of 2025. Prices and eligibility need rechecking before deciding. |

Recommendation: A now, for prereleases and the first updater. Add C before the first non-prerelease release, which `docs/SECURITY.md` already lists as a production requirement; once present, the updater also requires a valid Authenticode signature with a pinned subject. GitHub artifact attestations were considered and rejected: verifying them needs a Sigstore client (a new dependency), and they attest "built by this workflow", which a repository compromise can also produce.

Design of option A:

- **Keys.** The owner generates a primary key pair in the Windows CNG user key store, non-exportable, with strong key protection (a password prompt on every use), and a backup key pair on offline media kept elsewhere. Both public keys are committed under `tools/release/keys/` and compiled into `SecureWall.exe`. Either verifies. If the primary is lost or compromised, the next release is signed with the backup and ships with a new primary. A core test checks that the embedded keys equal the committed files.
- **Manifest.** `Build-SecureWallRelease.ps1` writes `SHA256SUMS.txt` with a header the signature covers:

  ```text
  SecureWall-Release-Manifest: 1
  Repository: ryanportfolio/SecureWall
  Tag: v0.5.1
  Version: 0.5.1
  <sha256>  SecureWall_x64.msi
  <sha256>  SecureWall_arm64.msi
  <sha256>  SecureWall_x86.msi
  ```

  Existing `sha256sum -c` users keep working if the header lines start with `#`; the final format is fixed when B2 is built.
- **Signing step.** The current flow stays: the owner publishes the prerelease, `release.yml` builds and attaches the MSIs and `SHA256SUMS.txt`, and the site job runs. Then the owner runs `tools/release/Sign-SecureWallRelease.ps1 -Tag v0.5.1` on their PC. It downloads the manifest and MSIs, recomputes every hash, checks the header against the tag and the csproj version at that tag, signs the exact manifest bytes and uploads `SHA256SUMS.txt.sig`. Until that asset exists, the updater treats the release as not ready. Honest limit: the owner signs what CI built. A compromise of the build itself before signing is not caught; reproducible builds would be needed for that.
- **Rollback attacks.** The client accepts only a version greater than the installed one, and only when the signed `Tag` and `Version` match the release it came from. Replaying an older signed release is refused. Withholding new releases (a freeze attack) is possible and accepted.

### Flow

1. **Trigger.** "Check for updates" in the tray menu and on Manage > About, next to the version label where TinyWall's update button used to be. Manual only (open decision 5).
2. **Network access.** WFP cannot match host names, and `SecureWall.exe` is also the LocalSystem service image, so a path exception is not an option. The check reuses the AI permit's shape: `SecureWall.exe` app ID, outbound TCP 443, and an `ALE_USER_ID` condition that denies SYSTEM, LOCAL SERVICE and NETWORK SERVICE and allows INTERACTIVE tokens. Recommended form: a transient grant. The controller sends a new password-gated pipe message; the service adds the permit to the dynamic session for 10 minutes or until the check finishes, never to the saved configuration or the persistent session, and removes it on expiry, reload failure, BlockAll or display-off blocking. The alternative is a persistent setting like `AiAssistantEgress` (open decision 4). Either way the permit is not limited to GitHub: while it is active, any interactive-token process running the SecureWall image can reach any TCP 443 destination, the same limitation the AI permit documents.
3. **Query.** `HttpClient` in the controller (part of .NET Framework, no new dependency), TLS 1.2 or 1.3, `User-Agent: SecureWall/<version>`. `GET /repos/ryanportfolio/SecureWall/releases?per_page=30`, body capped at 1 MiB, 30-second deadline. Redirects only to HTTPS and only to `api.github.com`, `github.com`, `objects.githubusercontent.com` and `release-assets.githubusercontent.com`. A system proxy on a port other than 443 is not supported, as with the AI feature. GitHub's unauthenticated limit (60 requests an hour per IP) is shown as its own message.
4. **Select.** Ignore drafts, prereleases unless the owner opted in, releases whose version is not greater than the installed one, and releases missing `SecureWall_<Platform>.msi`, `SHA256SUMS.txt` or `SHA256SUMS.txt.sig`. Take the highest remaining version.
5. **Verify the manifest.** Download the manifest (64 KiB cap) and signature (1 KiB cap), verify against the embedded keys, check `Repository`, `Tag` and `Version`, and find the line for this platform. Any failure shows "This release is not signed" and stops.
6. **Show.** Version, publish date, prerelease flag and the release notes as plain text in a read-only box (no HTML or Markdown rendering, capped at 20,000 characters), labelled as coming from GitHub and not covered by the signature. The dialog says the network pauses for up to a minute during the upgrade. Buttons: Install, Later, Skip this version (stored per user).
7. **Download.** Install requires the service to be unlocked when a password is set (open decision 6). The controller downloads the MSI to `%LOCALAPPDATA%\SecureWall\updates\<tag>\` with a 100 MiB cap and checks its hash, for early feedback only.
8. **Elevate.** The controller starts `SecureWall.exe /apply-update "<msi>" "<manifest>" "<sig>"` with the `runas` verb, which shows the UAC prompt. A controller that the logon task already started elevated (an administrator account) gets no prompt; the Install click is then the consent.
9. **Elevated helper.** Refuses to run as SYSTEM or as the service. Copies the three files into a fresh `%ProgramData%\SecureWall Updates\<tag>\` directory whose ACL grants only SYSTEM and Administrators, owner Administrators, no reparse points, separate from the guarded machine-data tree. Protecting the leaf is not enough: `C:\ProgramData` lets ordinary users create folders and gives the creator full control, so a user could precreate `SecureWall Updates` and later rename or replace the verified `<tag>` directory before `msiexec` opens it. The helper validates the whole ancestry the way `MachineDataGuard.Validate` does (trusted owner, no write, delete or delete-child right for non-administrators, no reparse point on any ancestor), creates a missing parent itself with the protected ACL, and refuses, without repairing, a parent that fails the check; the dialog then names the folder for an administrator to remove. Re-verifies the signature and hash on the protected copies, so a same-user process that swapped the files in `%LOCALAPPDATA%` after step 7 gains nothing. Opens the MSI database (msi.dll, read-only) and checks `UpgradeCode` and `ProductVersion` against SecureWall's and the manifest's. Then starts `msiexec.exe /i "<protected msi>" /l*v "<protected dir>\install.log"` with full UI and exits.
10. **Upgrade.** Part 1 runs. Step 3 terminates the controllers; step 10 restarts the installing user's controller on the new version, which shows "Updated to v0.5.1" when its version differs from the one recorded before the update. The commit step deletes old staging directories.

The download and every HTTP request run in the controller under the user's token. The LocalSystem service never contacts the network for updates; its only part is adding and removing the transient permit. A core test enforces that the service-side sources never reference the release client.

### Docs and tests

| File | Change |
|---|---|
| `docs/SECURITY.md` (line ~141) | Replace "SecureWall has no update feed and no update client" with the release-check description: GitHub releases of `ryanportfolio/SecureWall` only, controller only, signed manifest with owner-held keys, transient TCP 443 permit and its limits, elevated helper re-verification, TinyWall feed still banned, shipped data still MSI-only. Add an "In-place upgrade" section with the sequence, the blocked window and the recovery steps. |
| `.claude/reference/deployment.md` (line 7) | Same correction; add the signing step, key custody and rotation to the release procedure, and the v0.4.2-to-v0.5.0 manual step. |
| `README.md` (line 29) | Replace the "no update feed" paragraph with how to upgrade (run the newer MSI or use Check for updates) and the one-time manual step from v0.4.2 or older. |
| `docs/TESTING.md` (line 18) | The bullet becomes: no TinyWall update client (feed host, descriptor, `/updatenow`, legacy updater types); HTTP clients only in the AI client and the release client; service sources never reference the release client; the release client's host list is GitHub only. |
| `UpdateFeedRemovalTests.cs` | Keep every TinyWall pattern (`pados.hu`, `UpdVer`, `update.json`, `UpdateDescriptor`, `UpdateModule`, `UpdaterMethod`, `GetCompressedUpdate`, `/updatenow`, `UpdateFeedEnabled`), keep the `AutoUpdateCheck` legacy-load tests and keep that name out of new code. Replace the blanket `WebClient`/`Download*` ban with an allowlist: `HttpClient` and download calls may appear only in the two named controller files. Add: the only URL constants in the release client are the GitHub hosts above; `TinyWallService.cs` and the service path do not reference it. New code is named `ReleaseCheck` / `SignedReleaseManifest`, not `UpdateChecker`. |
| New pure tests | Manifest parsing, signature verification (valid, wrong key, truncated, wrong tag, wrong version, missing platform line), release selection (drafts, prerelease opt-in, equal and lower versions, missing assets), redirect and size policy, `/apply-update` identity refusal, transient permit lifetime and gating. |
| `tests/installer/Test-SecureWallInstaller.ps1` | Replace the SW10 assertions with the new contract: REP placement, upgrade range and minimum, equal-version and downgrade refusal, autowhitelist condition, upgrade action pairs and conditions, `RegistryKeys` without force delete, `Platform` value, no fault-test symbol in release artifacts, manifest header format. |
| `docs/plans/2026-09-06-pre-switch-hardening.md` | Note under SW10 that this plan supersedes the "no in-place upgrade" choice from v0.5.0 on. |

## VM test matrix

All cases run on a disposable VM with a clean snapshot and local console, as `docs/HARDENING-VALIDATION.md` requires. Each case captures the existing before-and-after inventory plus, for upgrade cases, `netsh wfp show filters` polled every 250 ms during the MSI run and continuous external probes from a second VM. These rows are added to `docs/HARDENING-VALIDATION.md`; row 41 ("Repair, equal-version rebuild and upgrade attempts") is rewritten to cover only repair, equal version and downgrade.

| Case | Required result |
|---|---|
| U1 Upgrade from v0.4.2 | Running v0.5.0 over v0.4.2 stops at `FoundIncompatibleVersion` before any change; v0.4.2 service, policy, files and WFP state unchanged. Manual uninstall then fresh v0.5.0 works. |
| U2 Upgrade, full UI and silent | v0.5.0 to v0.5.1. Exit code 0, never 3010. Same service registration and `ImagePath`; `config`, `pwd`, `hosts.orig` and journal hashes unchanged (record any deliberate migration); shipped data equals v0.5.1's; exception list identical (no autowhitelist); baseline filters present in every WFP sample; no unknown external connection succeeds at any time; saved allows work after; the blocked window measured and recorded; snapshot directory gone; tray back for the installing user. |
| U3 Password-locked service | MSI upgrade succeeds from an elevated prompt; the lock and password survive; the updater refuses Install until unlocked. |
| U4 Custom install directory | First install to a non-default directory; upgrade keeps it, shows no directory dialog, and the service starts from it. |
| U5 New service fails to start | `UpgradeFaultTest` build fails at start: MSI rolls back; old files, old shipped data and byte-identical config snapshot restored; old service Running with restart policy restored; baseline continuous; no snapshot left; event log names the step. |
| U6 Failure at each step | Separately: old service refuses to stop within the deadline; a localization DLL held open exclusively during `InstallFiles`; registry write denied; MpsSvc stopped before the upgrade (refused at step 3, nothing changed). Each ends with the old version running and policy intact. |
| U7 Cancel during progress | Cancel in the MSI progress dialog after step 3: same result as U5. |
| U8 Hard reset mid-swap | VM reset after step 3, during `InstallFiles` and during step 7. After boot: external probes blocked until a service commits policy; record whether the Automatic service starts, what the next MSI run does with the suspended installation, and the exact local-console recovery. |
| U9 Rejected machine data | Hostile ownership or ACL in the data tree before upgrading: step 3 refuses before stopping anything; event 1000; tree hashes, owners and ACLs unchanged. |
| U10 Equal version and downgrade | Rerun of the installed MSI and an older upgradeable MSI both refused before any action; repair still refused. |
| U11 Journals across upgrade | With a Windows Firewall compatibility journal and an audit journal present, upgrade: both keys unchanged. A later explicit uninstall restores the original notification values, rules and audit flags, then removes `HKLM\Software\SecureWall`. |
| U12 Controllers in several sessions | Two users logged on with trays open: both terminated before file replacement, no reboot required. Record whether `FilesInUse` appears; second user's tray returns at next logon. |
| U12a Tray starts during the swap | Log a second user on, or start `SecureWall.exe` from the Start menu, between steps 3 and 7 (fault build pauses there): the tray shows "being upgraded", the service stays stopped, and the upgrade completes. After a hard reset with the marker left behind, the service starts at boot and a tray's repair path works again. |
| U13 SCM restart suppression | Crash the old service between steps 3 and 7 (fault build): SCM does not start it mid-swap. After commit, `sc qfailure` shows the three restart actions again. |
| U14 Baseline schema change | Fault build whose baseline adds a filter and drops a sublayer: after upgrade only the new set exists; after a rolled-back upgrade only the old set exists. |
| U15 BFE restart during swap | Restart BFE between steps 3 and 7: baseline reloads, external traffic stays blocked, upgrade completes or rolls back cleanly. |
| R1 Transient permit | Check for updates with no permit: the grant appears in WFP with the user condition, works for the controller, stays blocked for a SYSTEM process running the same image, and disappears at the end of the check, at expiry, in BlockAll and on a failed reload. Locked service: the grant request needs the password. |
| R2 Release selection | Prereleases off: none offered while every release is a prerelease. On: newest offered. Drafts never. Equal or lower versions never. Release without `.sig`: "not signed". |
| R3 Tampering | Flipped byte in the MSI, edited manifest, signature from an unknown key, manifest tag or version not matching the release, and a replayed older signed release: each refused before UAC with a distinct message. |
| R4 Swap after verification | Replace the MSI in `%LOCALAPPDATA%` between the controller's check and the UAC prompt: the elevated helper's re-verification refuses; nothing installs. Separately, as an ordinary user, precreate `%ProgramData%\SecureWall Updates` (user-owned, then with a user write ACE, then as a junction): the helper refuses before copying and leaves the folder untouched. |
| R5 Transport limits | Redirect to `http://` or to a non-GitHub host, oversized API and manifest bodies, slow-drip body past the deadline: each refused; no partial file used. |
| R6 UAC declined | Nothing changes; the controller keeps running and can retry. |
| R7 Account types | Standard user (UAC asks for credentials) and administrator with an already elevated tray (no prompt); both reach the MSI with full UI. |
| R8 End to end | v0.5.x to the next release through the button on x64, arm64 and x86 VMs: U2's results plus the "Updated to" notice, staging cleaned, `install.log` present. |
| R9 Network failure | No network, DNS blocked, proxy on port 8080, GitHub 403 rate limit: clear message each time; transient grant removed. |

## Build sequence

Each step ends with the core tests, full MSBuild Debug and Release, the installer contract test and a fresh review. Nothing is installed on the development host.

**Phase A, in-place upgrade (ships as v0.5.0):**

1. A1 Registry ownership: `RegistryKeys` without force delete, `Platform` value, explicit-removal key cleanup. Contract test.
2. A2 Maintenance modes `/msi-upgrade-stop`, `/msi-upgrade-restore`, `/msi-upgrade-start`, `/msi-upgrade-abort`, `/msi-upgrade-commit` in `Program.cs` and `TinyWallDoctor.cs`, with the decisions in a pure `UpgradeLifecyclePolicy` tested against a fake SCM and fake file system (snapshot naming, snapshot only after stop, stale snapshot handling, registration and state preconditions), plus the upgrade-in-progress marker and its check in every automatic start path.
3. A3 `Product.wxs` changes and the installer contract test rewrite.
4. A4 `UpgradeFaultTest` build configuration and its release-artifact refusal.
5. A5 Docs: SECURITY, README, deployment, release-notes template (manual step from v0.4.2), pre-switch plan note, `HARDENING-VALIDATION.md` rows U1 to U15.
6. A6 Build v0.5.0 and a local v0.5.1 test MSI; run U1 to U15 on the VM. Release v0.5.0 only after they pass.

**Phase B, updater (v0.5.1 or v0.6.0):**

7. B1 Owner creates the primary and backup keys (owner action; no key material is created by an agent). Public keys committed; `Sign-SecureWallRelease.ps1`.
8. B2 Manifest header in `Build-SecureWallRelease.ps1`; installer test.
9. B3 Pure core: manifest parser and verifier, release selection, URL and redirect policy, with tests.
10. B4 Service: transient update permit message, lifetime and gating, with tests.
11. B5 Controller: tray item, About button, update dialog, settings (include prereleases), download with caps.
12. B6 `/apply-update` elevated helper: protected staging, re-verification, MSI property check, msiexec launch, cleanup.
13. B7 Docs and `UpdateFeedRemovalTests` changes from the table above.
14. B8 VM cases R1 to R9 and a full U2 rerun through the button. Release.

## Decisions

Approved by the owner on 2026-11-01 ("Yes, recommended").

1. **v0.4.2 and older:** one last manual uninstall-and-install reaches v0.5.0. No re-caching of old packages.
2. **Signing:** owner-held ECDSA P-256 key in the Windows CNG store, plus an offline backup key, with a manual signing step per release. No CI-secret key. Authenticode is reconsidered before the first non-prerelease release.
3. **Prereleases:** not settled. The plan offered no recommendation here, so the approval does not cover it. The build defaults "Include prereleases" to off, as the original brief asked (the owner opts in), and this is confirmed with the owner before step B5.
4. **Network permit:** a transient 10-minute grant per check, never saved.
5. **Automatic checks:** manual button only.
6. **Password lock:** Install requires unlocking when a password is set.
7. **Release split:** the in-place upgrade ships alone as v0.5.0; the updater follows in a later release.
