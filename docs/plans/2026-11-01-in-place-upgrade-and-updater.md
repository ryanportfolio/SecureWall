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
| `RegistryKeys` component, `ForceDeleteOnUninstall='yes'` on `HKLM\Software\SecureWall` | Deletes the whole key on product removal | The Windows Firewall journal (`SOFTWARE\SecureWall\FirewallRecovery`) and audit journal (`SOFTWARE\SecureWall\AuditRecovery`) live under it. A normal service stop restores and clears both (see "Filtering across the swap"), but when that restore fails the journal is the only record of what to undo. Removing the old product during an upgrade would delete it. The 64-bit packages write this key in the 64-bit view, where the journals live; the x86 package writes it under `WOW6432Node` on 64-bit Windows. |
| `InstallCustomRollback` = `/msi-rollback-install` on `NOT Installed` | Failed first-install teardown: disables and deletes the service, removes the baseline | A new package is "NOT Installed" during an upgrade too. Its rollback would tear down a working installation. |
| `StartControllerAndDetect` = `/autowhitelist` on `(NOT Installed) AND UILevel=5` | First-install profile detection | Would also run on every full-UI upgrade. `AutoWhitelist()` merges every detected profile into the active profile, which can widen existing exceptions. |
| `INSTALLDIR` default only | Program Files\SecureWall | The UI has no directory chooser, but `INSTALLDIR=` on the msiexec command line can place SecureWall in another protected folder under Program Files (`InstallationSafety.cs`). An upgrade would ignore it, and the service `ImagePath` check would then refuse. |
| No service stop before file removal | The service and every user's tray controller run `SecureWall.exe` from `INSTALLDIR` | File replacement would hit in-use files and end in a reboot-required (3010) result with mixed binaries. |

What already works in our favour:

- The old package's `UninstallCustom` (`/msi-cleanup`) is conditioned `NOT UPGRADINGPRODUCTCODE`, so an upgrade-time removal of a v0.5+ package never releases protection.
- The service is registered by `SecureWall.exe /install` through `ManagedInstallerClass`, not by the MSI `ServiceInstall` table, so removing the old product does not delete the service registration. Its `ImagePath` (`"<INSTALLDIR>\SecureWall.exe"`) stays valid when the new files land in the same directory.
- The persistent deny baseline's provider has no `serviceName`, so it survives the service stopping, and `RegisterRestrictiveBaseline` replaces it atomically (delete plus re-register in one WFP transaction) when the new service starts.
- `/install` on a registered, stopped service already validates the registration, runs the machine-data guard, refreshes shipped data (`MachineDataGuard.InstallDefaults`), reapplies health settings and starts the service.
- The service reports Running only after its first policy commit and after the Windows Firewall compatibility setup: `FinishStateChange` follows `InitFirewall()` and the `WindowsFirewall` constructor (`TinyWallService.cs` around line 2385). SCM Running is therefore a sound success signal for the upgrade.

### Versions up to v0.4.2

Their cached packages contain the `NOT UPGRADINGPRODUCTCODE` launch condition, so no later MSI can remove them as part of an upgrade. The workarounds (re-caching a patched copy of each old package under its random per-build ProductCode, or a patch per architecture) cost more than they save for a prerelease with one known installation.

Decision 1 (approved): v0.5.0 is the first upgrade-capable release. `OldestUpgradeableVersion` becomes `0.5.0`. An installed v0.4.2 or older matches `OLDER_INCOMPATIBLE_FOUND`, and the existing `FoundIncompatibleVersion` error tells the user to uninstall it first. The move to v0.5.0 is the last manual uninstall-and-install; v0.5.0 to any later version is in place. The v0.5.0 release notes say so. v0.5.0's own upgrade row matches nothing; the first real in-place upgrade is v0.5.0 to its successor.

### Upgrade transaction

`RemoveExistingProducts` (REP) is scheduled immediately after `InstallInitialize`, so the old product's removal and the new product's installation share one transaction and one rollback script. The alternatives were rejected: REP after `InstallValidate` runs the removal in its own transaction that cannot roll back if the new install fails; REP after `InstallExecute` installs new files over in-use old ones and depends on unversioned files (`profiles.json`, `hosts.bck`, `SecureWall.exe.config`) being replaced by file-versioning rules.

Each side of the swap is handled by the version that knows it:

- **The old package stops itself.** A v0.5+ package carries an upgrade-only deferred SYSTEM action, conditioned `Installed AND REMOVE="ALL" AND UPGRADINGPRODUCTCODE` and sequenced `Before="UnpublishFeatures"` like today's `UninstallCustom`, so it runs before `RemoveRegistryValues` (which would remove the event log source its failure events need) and before `RemoveFiles`. It runs the installed (old) `SecureWall.exe` while that file still exists. Its rollback twin restarts the old service after Windows Installer restores the old files. These conditions must not use `OLDER_UPGRADEABLE_FOUND` or any other property set by `FindRelatedProducts`: that action does not run in the old package during its nested removal, so such a condition would be false and the stop would silently never happen.
- **The new package starts itself.** After `InstallFiles`, the new package runs its own `SecureWall.exe` to start the service. Its actions are conditioned `NOT Installed AND OLDER_UPGRADEABLE_FOUND`. Its rollback twin stops the new service before Windows Installer removes the new files.

The other option, the new package stopping the old service before REP, needs an executable that is neither the old installed file nor a not-yet-installed new file: a helper in the MSI `Binary` table, running from the installer's temporary folder, outside the protected-installation checks the service image validation relies on. It would also place deferred actions between `InstallInitialize` and REP, which ICE63 flags. Letting each version handle its own side keeps every privileged step on an authenticated Program Files image. The cost: a v0.5.0 bug in its upgrade-stop step cannot be fixed by a later package. Such a bug fails the upgrade closed (rollback, old version keeps running), and manual uninstall-and-install remains the fallback. The installer contract test locks the per-side conditions so this cannot regress before v0.5.0 ships.

#### Sequence

Execute script of an upgrade from version N (old, at least 0.5.0) to N+1 (new). The transaction ID passed to every step is the new package's ProductCode (`UPGRADINGPRODUCTCODE` inside the old package, `[ProductCode]` inside the new one); it ties a snapshot and a marker to exactly one upgrade.

| # | Package | Action (deferred, SYSTEM unless noted) | What it does |
|---|---|---|---|
| 1 | new | `InstallInitialize` | |
| 2 | old (inside REP) | rollback: `/msi-upgrade-restore <txn>` | Registered first so it runs last in rollback, after old files are back. Idempotent; see "Rollback". |
| 3 | old | `/msi-upgrade-stop <txn>` | (a) Runs the machine-data guard and authenticates the installation and service registration exactly as `/msi-cleanup` does; a rejected tree or a foreign `ImagePath` fails here, before anything changes. (b) Requires MpsSvc running and BFE running, so the new service can start. (c) Writes the upgrade-in-progress marker with phase `stopping` (see "Blocking other service starts" and "Rollback"); it advances to `stopped` after (f) and to `snapshot-complete` after (h). (d) Disables SCM restart-on-failure so a crash mid-swap cannot start half-installed binaries; the startup type stays Automatic so a reboot still brings up whatever service is present. (e) Stops the service gracefully with the existing bounded `ServiceLifecyclePolicy` wait; the deny baseline now enforces alone. (f) Terminates this installation's controllers (`TerminateInstallationControllers`), failing if any survives, so no file is in use. (g) Re-checks that the service is Stopped (a controller that read the marker too early could have started it), then, with no service or controller left to write policy, snapshots the service's policy files and flushes them. (h) Re-checks Stopped once more and writes the snapshot's hash manifest as its very last act. Returns non-zero on any failure, which aborts the upgrade before files change. |
| 4 | old | `RemoveRegistryValues`, `RemoveFiles` | Old payload removed. MSI-owned values only (`InstallDir`, `Platform`); journals and marker untouched (see "Registry ownership"). |
| 5 | new | `InstallFiles`, `WriteRegistryValues` | New payload into the same `INSTALLDIR`. |
| 6 | new | rollback: `/msi-upgrade-abort <txn>` | Disables restart-on-failure first, then stops the new service if it is running (bounded), waits past the first SCM restart delay and confirms Stopped, terminates controllers. Leaves the registration, baseline, marker and snapshot. |
| 7 | new | `/msi-upgrade-start <txn>` | Requires the service registered and Stopped, its `ImagePath` equal to this executable, the marker and a complete snapshot for `<txn>` present (proof that step 3 finished). Runs the machine-data guard and the shipped-data refresh (as `/install` does today), reapplies the startup type and controller logon task, starts the service, and waits for Running within `ServiceLifecyclePolicy.StartupTimeout`. It leaves restart-on-failure off (see "Recovery settings ownership"). Fails if the service exits or does not report Running. |
| 8 | new | `InstallFinalize` | Transaction committed. |
| 9 | new | commit: `/msi-upgrade-commit <txn>` | Re-enables restart-on-failure, deletes the snapshot directory and clears the marker. `Return="ignore"`: a failure here is logged (event 1001) and cannot fail an already committed upgrade; a leftover snapshot is never restored outside its own rollback, and the next service start outside an installation restores the restart policy. |
| 10 | new | commit, impersonated: `StartController` (no `/autowhitelist`) | Relaunches the tray for the user who started the MSI, when `UILevel=5` and the updater did not suppress it (see Part 2, step 9). Silent upgrades never start a tray; trays return at the next logon or from the Start menu. |

#### Rollback

Rollback after a failure at steps 5 to 7, or a cancel after step 3, runs in reverse: step 6 stops the new service, Windows Installer removes the new files and restores the old product's files and registry values, then step 2 runs with the old executable.

Step 2 is idempotent, because it may run after step 3 failed at any point, after a failed step 6, or after a reboot mid-swap. Step 3 records its progress in the marker's phase (`stopping`, `stopped`, `snapshot-complete`), and step 2 decides from that phase, never from the service's state alone: a Running service after a failed step 6 or a reboot can be the **new** binary.

1. No marker for `<txn>`, or phase `stopping` with the service Running: the old service never stopped. Skip to 4.
2. Otherwise make sure the service is Stopped, whichever binary is running: stop it (bounded), terminate this installation's controllers, and confirm Stopped. If it cannot be stopped, keep the snapshot and the marker, return failure and leave recovery to the local console; never delete a snapshot that has not been restored.
3. If a snapshot for `<txn>` exists and its hash manifest is complete and verifies, restore the snapshot as an exact set: every covered file is replaced with its snapshot copy, and every covered file absent from the snapshot is deleted (a failed new service can leave a newer-format `config.recovery`). The secret file `pwd` is restored with the secret-file DACL (`SecretFileProtection`). An incomplete or unverifiable snapshot is never restored; the live files were not touched before step 5, so they are left alone.
4. Run the old version's shipped-data refresh (`MachineDataGuard.InstallDefaults`), so `profiles.json` and `hosts.bck` return to the old bytes, and start the old service if it is not running.
5. Re-enable restart-on-failure, delete the snapshot (only after a successful restore, or when there was nothing to restore), clear the marker.

#### Recovery settings ownership

While a marker is present and an installation runs, the upgrade transaction alone owns the service's SCM recovery settings. Today every service start calls `TinyWallDoctor.EnsureHealth` right after reporting Running (`TinyWallService.cs` around line 2395), and `EnsureHealth` turns restart-on-failure on unconditionally. During an upgrade that would undo step 3(d) and could race step 6. So `EnsureHealth` skips the recovery settings (it still logs) while the marker is present and Windows Installer is running an installation; step 7 leaves them off; only step 9 (commit) and step 2 (rollback) turn them back on. A marker left by a power loss stops counting once no installation runs, so the next ordinary service start restores the policy as today.

#### Blocking other service starts

Disabling SCM restart-on-failure is not enough. Today a tray controller that cannot reach the service calls `TinyWallDoctor.EnsureServiceInstalledAndRunning` from `InitController` (`TinyWallController.cs` around line 1394), which starts a stopped service. A tray launched by another user's logon during the swap could start the old binary before its removal or the new binary before step 7, locking files and breaking step 7's stopped-service precondition. (The reconnect timer only polls; it never starts the service.)

Step 3 therefore writes a SYSTEM-only marker, `HKLM\SOFTWARE\SecureWall\UpgradeInProgress = <txn>` in the 64-bit registry view (the view the journals use), before it stops anything. While the marker is present and Windows Installer is running an installation (the `Global\_MSIExecute` mutex is held):

- `EnsureServiceInstalledAndRunning` and `/install` outside the upgrade modes refuse to start the service.
- A controller that finds the marker shows "SecureWall is being upgraded" and exits, so it cannot hold `SecureWall.exe` open through `RemoveFiles`.
- Steps 7 and 2 are the only starters that accept the marker, and only for their own transaction ID.

A controller that read the marker just before step 3 wrote it can still start the service; step 3's re-checks in (g) and (h) catch that and fail the upgrade closed. The commit step and step 2 clear the marker; step 6 leaves it, because the old files are not back yet when it runs. A marker left by a power loss is ignored once no installation is running, so SCM's boot start and manual `sc start` recovery are never blocked; until then it only delays tray repair while some unrelated MSI runs. SCM's own Automatic start at boot does not read the marker at all.

#### Filtering across the swap

- From step 3(e) until step 7's policy commit, only the persistent and boot-time deny filters are active. Unknown and previously allowed external traffic is blocked in both directions, DNS and DHCP included; loopback works. This is the same state the "Service stop/crash" validation case already requires.
- At step 7 the new service atomically replaces the baseline and commits its dynamic session. The provider key is stable. Rule for every future release: `DeleteWfpObjects` must find and remove every object any upgradeable version can have registered under the SecureWall provider, in every layer and sublayer any of them used, including ones a later version stops using; otherwise provider removal fails, the new service cannot start and the upgrade rolls back.
- A service stop already restores Windows Firewall to its own state: `WindowsFirewall.Dispose` calls `RestoreOwnedState`, which removes SecureWall's allow-all compatibility rules, restores the original notification values and clears the `FirewallRecovery` journal; the audit lease's disposal restores the audit flags and clears its journal. So during the swap Windows Firewall enforces its original policy alongside the baseline (which only adds blocking), and both journals are normally empty. The new service reacquires both before it reports Running. If a restore at stop fails, its journal entry stays and the key must survive the upgrade, which "Registry ownership" guarantees.
- The hosts file keeps its content; the hosts lock is a file handle that ends with the old process and returns with the new one.
- Pending prompts and prompt tokens are lost, exactly as on a service restart. Timed exceptions live in `config` and survive. "Until reboot" exceptions are pruned from `config` at every service start (`PruneExpiredRules(restarting: true)`), and the pruned config is saved, so an upgrade ends them like a restart does.
- Expected blocked window: service stop plus file swap plus start, tens of seconds. Open connections drop. The full-UI MSI shows this on its welcome page, and the updater says so before starting.

#### Failure outcomes

| Failure | Result |
|---|---|
| Guard, registration, MpsSvc, stop or controller-termination failure at step 3 | Upgrade aborts. Step 2 restarts the old service if it had stopped, re-enables restart-on-failure and clears the marker. |
| File copy, registry or new-service start failure | Full rollback to the old version, old policy snapshot and old shipped data; old service running. |
| Rollback's old-service start also fails (step 2) | Service stays stopped, baseline blocks external traffic. Not permanent: the service is still Automatic, so a reboot retries it, and `sc start SecureWall` from an elevated prompt retries at once. The MSI error and Application event 1001 name the step. This is the case to watch in the VM matrix. |
| Power loss or hard reset mid-swap | The boot-time and persistent baseline block external traffic at boot. The Automatic service starts whatever binary is present; if that fails, traffic stays blocked until local-console recovery. Windows Installer rolls back a suspended installation the next time an MSI runs, which runs step 2 with the kept snapshot. VM case U8 records the actual behaviour at each stage. |
| New service crashes after the MSI committed | Normal SCM restart policy; baseline holds meanwhile. Same as any crash today. |

Nothing in this design opens the network to recover connectivity; every failure ends either in a running SecureWall or in the deny baseline.

#### Machine data and the guard

- No MSI table ever touches `%ProgramData%\SecureWall`; only the guarded SYSTEM entry points do, as today.
- Steps 3, 2 and 7 each run the full-tree guard before reading or writing machine data. A rejected tree fails step 3 before the stop, so a hostile tree can never trigger a swap. The emergency release path (`ReleaseForMsiWithRejectedMachineData`) stays reserved for explicit removal and failed first installs.
- The snapshot lives in `%ProgramData%\SecureWall\upgrade\<txn>\`, created by SYSTEM inside the guarded tree, so `MachineDataPolicy` already validates it against replacement. Its set is every file the service writes: `config`, the policy recovery journal (`config.recovery`), and `pwd`. The guard checks write rights, not read rights, and the tree's inherited ACL lets Users read; `pwd` is therefore copied and restored with the secret-file DACL (`MachineDataPolicy`'s `SecretFileDacl`), never with the inherited one. A2's tests list the files the service writes and fail if a new one appears that the snapshot set does not cover.
- Only step 9 (success) and step 2 (rollback) delete a snapshot. A snapshot is never restored outside its own transaction's step 2.
- Configuration format: the new version may read and rewrite the old format. Rollback never asks the old version to read a newer file, because it restores the exact snapshot set first. This removes the need for downgrade-compatible configuration.
- Shipped-data refresh: unchanged. The new `/msi-upgrade-start` and the old `/msi-upgrade-restore` both go through `MachineDataGuard.InstallDefaults`, so `profiles.json` and `hosts.bck` always match the binaries that are running.
- A refreshed `profiles.json` changes more than the list of available profiles. Special exceptions the user already enabled are looked up by name at every rule build, so a changed definition takes effect at the upgrade's first commit, without any user action. Only new profiles wait for the Manage window. The release checklist therefore reviews every `profiles.json` diff for widened definitions before tagging.

#### /autowhitelist

The upgrade never runs it. `StartControllerAndDetect` becomes `NOT Installed AND NOT OLDER_UPGRADEABLE_FOUND AND UILevel=5` (fresh install only); upgrades use the plain `StartController` action. Installer contract test: the `/autowhitelist` action's condition excludes the upgrade property.

#### Registry ownership

- The `RegistryKeys` component keeps the `InstallDir` value, gains a `Platform` value (`x64`, `arm64` or `x86`, from `$(var.Platform)`) and loses `ForceDeleteOnUninstall`. MSI then removes its own two values and nothing else, in the same view it wrote them.
- SecureWall-owned subkeys (`FirewallRecovery`, `AuditRecovery`, `UpgradeInProgress`) are always read and written in the 64-bit view by the code, whatever package architecture installed it.
- Explicit removal (`/msi-cleanup`, `/uninstall`, failed-first-install rollback) gets a new commit-phase action that deletes `HKLM\Software\SecureWall` only when no journal value is left. A deferred action would escape MSI rollback: a rolled-back uninstall would then lose `InstallDir` and `Platform` for good. If a journal is kept because its restore failed, the key stays, which is the behaviour the journals need anyway.
- The updater uses `Platform` to choose the matching asset.

#### Package changes (`Product.wxs`)

- Upgrade table: the older range `Minimum=0.5.0 IncludeMinimum=yes Maximum=$(ProductVersion) IncludeMaximum=no` loses `OnlyDetect` and gains `MigrateFeatures`. `SELF_FOUND` and `NEWER_FOUND` stay detect-only and keep failing with a message. A manual Upgrade table is kept instead of `<MajorUpgrade>` because WiX's element cannot express the equal-version refusal: an equal-version rebuild has a new ProductCode and would otherwise install side by side.
- Windows Installer compares only the first three version fields. Releases must change one of them; the release check in `release.yml` already ties the tag to `<Version>`, and the installer test asserts the three-part comparison.
- `RemoveExistingProducts` `After="InstallInitialize"`.
- Launch conditions: drop `NOT UPGRADINGPRODUCTCODE` (this is what lets v0.5.0 itself be upgraded later); replace the related-product condition with the detect-only refusals; allow `EXISTINGSECUREWALLSERVICE` when `OLDER_UPGRADEABLE_FOUND`. `/msi-upgrade-start` still proves the service belongs to this installation.
- Cross-architecture upgrades are refused. One UpgradeCode covers all three packages, so an x64 MSI could otherwise upgrade an arm64 or x86 install and silently change its architecture or install directory. A `RegistrySearch` reads the installed `Platform` from the package's own view, and a launch condition refuses an upgrade unless it equals `$(var.Platform)`. An x86 install on 64-bit Windows keeps its values under `WOW6432Node`, where only the x86 package finds them, so only the x86 package can upgrade it.
- `INSTALLDIR` comes from a `RegistrySearch` on `HKLM\Software\SecureWall\InstallDir` (package's own view) during an upgrade. There is no directory dialog to skip.
- Conditions, per side, locked by the installer contract test:
  - old side, steps 2 and 3: `Installed AND REMOVE="ALL" AND UPGRADINGPRODUCTCODE`, `Before="UnpublishFeatures"`;
  - new side, steps 6, 7 and 9: `NOT Installed AND OLDER_UPGRADEABLE_FOUND`; step 10 adds `UILevel=5 AND NOT SECUREWALL_SUPPRESS_USER_ACTIONS`;
  - fresh-install actions (`InstallCustom`, `InstallCustomRollback`, `StartControllerAndDetect`): add `AND NOT OLDER_UPGRADEABLE_FOUND`;
  - `UninstallCustom` and `UninstallCustomRollback` keep `NOT UPGRADINGPRODUCTCODE`;
  - no old-side action references any `FindRelatedProducts` property.
- The repair/modify refusal and `NOT RollbackDisabled` stay.
- `MSIRESTARTMANAGERCONTROL=Disable` means only the legacy `FilesInUse` dialog can appear, and only for processes with a window: open trays, before step 3 terminates them. VM case U12 records whether it does; if so, the fix is an immediate-phase notice instead of the dialog, not killing processes before the user has confirmed.

#### Upgrade fault test build

Rollback cases need versions that fail at a chosen step, on either side: U6 and U13 need a failing **old** version (step 3 runs the old executable), U5 and U14 a failing **new** one. Release builds must not contain fault injection, so the test MSIs come from a separate build configuration, `UpgradeFaultTest`, that reads a fault name from a SYSTEM-only registry value and can pause at a named step for the race cases. It is installable as the old version (a v0.5.0 fault build) and as the new one. It stamps itself visibly: MSI property `SECUREWALL_FAULT_TEST=1`, ProductName suffix " (fault test)" and an assembly attribute. The installer test fails a release artifact carrying any of the three, and fails if `release.yml` or `Build-SecureWallRelease.ps1` reference the configuration.

## Part 2: "Check for updates"

### Revisiting the removed updater

The TinyWall updater was removed on purpose (`docs/SECURITY.md` "SecureWall has no update feed...", `.claude/reference/deployment.md` line 7, `README.md` line 29, `docs/TESTING.md` line 18, `UPSTREAM.md` line 117, `UpdateFeedRemovalTests`). The reasons were: it fetched from TinyWall's server (`tinywall.pados.hu`), which must never replace this fork; it downloaded database and hosts files outside the MSI; it ran without a signature. This plan keeps all three bans:

- TinyWall's feed stays banned. The only remote source is `https://api.github.com/repos/ryanportfolio/SecureWall/releases` and the assets it lists.
- `profiles.json` and `hosts.bck` still change only through the MSI's shipped-data refresh. The client downloads three files per update: the signed manifest, its signature and one MSI.
- Nothing installs unless a signature made with a key held outside GitHub verifies.

What changes is the statement "SecureWall has no update client." It becomes "SecureWall has a user-initiated release check against its own GitHub releases, run by the controller and verified with an embedded public key." The doc and test changes are listed under "Docs and tests" below.

### Integrity scheme

A `SHA256SUMS.txt` downloaded next to the MSI proves nothing: whoever can replace one asset can replace both. Three options:

| Option | Protects against | Cost |
|---|---|---|
| **A. Signed manifest, owner-held key (chosen)** | Assets replaced on GitHub after signing (account or token compromise, `--clobber` uploads), a hostile CDN or network. Not a malicious build before signing (see below). | Free. No new dependency: ECDSA P-256 with SHA-256, verified by `ECDsaCng` in .NET Framework 4.8, signed with Windows CNG on the owner's PC. One manual step per release. Does not help SmartScreen or the UAC publisher line. |
| B. Same key as a GitHub Actions secret | Assets replaced after the build, CDN | Automatic, but anyone who can push a workflow to the repository can sign anything. |
| C. Authenticode code signing | Tampering, and gives Windows a verified publisher (UAC, SmartScreen, `WinVerifyTrust`) | A certificate whose key must sit in an HSM or hardware token (CA/B rules since June 2023). Commercial OV certificates are roughly $200 to $500 a year plus token or cloud-HSM fees. Microsoft's Azure Artifact Signing (formerly Trusted Signing) is about $10 a month and works in CI, but requires identity validation, and individual developers were eligible only in the US and Canada as of 2025. Prices and eligibility need rechecking before deciding. |

Decision 2 (approved): A now; C is reconsidered before the first non-prerelease release, which `docs/SECURITY.md` already lists as a production requirement. Once C exists, the updater also requires a valid Authenticode signature with a pinned subject. GitHub artifact attestations were considered and rejected: verifying them needs a Sigstore client (a new dependency), and they attest "built by this workflow", which a repository compromise can also produce.

What A does not cover, accepted explicitly: the owner signs what CI built. A malicious workflow change, compromised action or anyone able to upload release assets before the owner signs can supply MSIs that carry the expected version, the owner's public keys and a manifest naming the reviewed commit; every check below would pass and the owner would sign them. The signing checks catch mistakes (wrong tag, wrong commit, stale keys, a rerun build), not that attack. Closing it needs builds the owner can verify independently: reproducible builds compared against a local build, or building and signing on the owner's PC instead of in CI. Neither is in this plan; it is listed for the Authenticode decision.

Design of option A:

- **Keys.** The owner generates a primary key pair in the Windows CNG user key store, non-exportable, with strong key protection (a password prompt on every use), and a backup key pair on offline media kept elsewhere. Both public keys are committed under `tools/release/keys/` and compiled into `SecureWall.exe`. Either verifies. Limits, stated plainly: the backup is necessarily exportable and carries the same authority, so its storage matters as much as the primary's; a compromised key stays trusted by every installed version until that version updates, and because selection takes the highest version, an attacker-signed "v9.9.9" would win and block later legitimate releases. Recovery from a key compromise is a manual reinstall of a release that drops the key. A core test checks that the embedded keys equal the committed files.
- **Signature format.** IEEE P1363 (`r||s`, 64 bytes, base64), which `ECDsa.VerifyData` expects. DER output from other tools is rejected.
- **Manifest.** `Build-SecureWallRelease.ps1` writes `SHA256SUMS.txt` with a header the signature covers. Header lines start with `#` so `sha256sum -c` and `Get-FileHash` users are unaffected (to be confirmed when B2 is built):

  ```text
  # SecureWall-Release-Manifest: 1
  # Repository: ryanportfolio/SecureWall
  # Tag: v0.5.1
  # Version: 0.5.1
  # Prerelease: true
  # Commit: <full commit SHA>
  <sha256>  SecureWall_x64.msi
  <sha256>  SecureWall_arm64.msi
  <sha256>  SecureWall_x86.msi
  ```

  `Prerelease` is signed so that GitHub's unsigned flag cannot push a prerelease to users who opted out.
- **Signing step.** The current flow stays: the owner publishes the prerelease, `release.yml` builds and attaches the MSIs and `SHA256SUMS.txt`, and the site job runs. Then the owner runs `tools/release/Sign-SecureWallRelease.ps1 -Tag v0.5.1 -Commit <reviewed SHA>` on their PC. It refuses unless the tag points at that commit and the manifest's `Commit` equals it; downloads the manifest and MSIs and recomputes every hash; checks the header against the tag, the release's prerelease flag and the csproj version at that commit; extracts `SecureWall.exe` from each MSI and refuses unless its embedded public keys equal the owner's local copies; then signs the exact manifest bytes and uploads `SHA256SUMS.txt.sig`. Until that asset exists, the updater skips the release. A rerun of `release.yml` after signing rebuilds the MSIs with new ProductCodes and new hashes and leaves the old signature stale (the release then fails closed); the procedure is to delete the `.sig` and sign again.
- **Rollback attacks.** The client accepts only a version greater than the installed one, and only when the signed `Tag` and `Version` match the release it came from. Replaying an older signed release is refused. Withholding new releases (a freeze attack) is possible and accepted.
- **Test key.** VM cases need signed tampered and future releases. They use a separate test key pair that only the `UpdaterTest` build configuration embeds (see "Test harness"); the production key never signs test or fault builds.

### Flow

1. **Trigger.** "Check for updates" in the tray menu and on Manage > About, next to the version label where TinyWall's update button used to be. Manual only (decision 5). When a password is set and SecureWall is locked, the action asks for the password first, as Manage does: the permit request in step 2 is a password-gated pipe message, so a locked service cannot even run the check (decision 6 extended to the check itself).
2. **Network access.** WFP cannot match host names, and `SecureWall.exe` is also the LocalSystem service image, so a path exception is not an option. The check uses the AI permit's shape (`SecureWall.exe` app ID, outbound TCP 443, an `ALE_USER_ID` condition that denies SYSTEM, LOCAL SERVICE and NETWORK SERVICE) with one difference: the allow entry is the requesting pipe client's user SID, not every interactive token, so another logged-on user's `SecureWall.exe` cannot use the window. Decision 4 (approved): a transient grant. The controller sends a new password-gated pipe message; the service adds the permit to the dynamic session for at most 10 minutes, never to the saved configuration or the persistent session, and removes it on expiry, on the controller's done message, when the requesting controller process exits, on LOCK, on reload failure, in BlockAll and under display-off blocking. The grant is a lease on the requesting process, not on a connection: the pipe client opens a new connection for every request (`PipeClientEndpoint.cs`), so connection lifetime means nothing. On the grant request the service records the client's process ID (`GetNamedPipeClientProcessId`), user SID and process creation time, and keeps a `SYNCHRONIZE` handle to that process; the handle both signals the exit and pins the process object, so a reused process ID cannot inherit the lease. The controller requests one grant for the check and a fresh one for the download in step 7. While a grant is active, that user's SecureWall-image processes can reach any TCP 443 destination, the same limitation the AI permit documents.
3. **Query.** `HttpClient` in the controller (part of .NET Framework, no new dependency), TLS 1.2 (Windows 10 has no TLS 1.3 client), `User-Agent: SecureWall/<version>`. `GET /repos/ryanportfolio/SecureWall/releases?per_page=30&page=N` for at most 3 pages, stopping early once a page holds a version not greater than the installed one; each body capped at 1 MiB, 30-second deadline per request. `AllowAutoRedirect=false`; redirects are followed by hand, at most 5, only to HTTPS and only to `api.github.com`, `github.com`, `objects.githubusercontent.com` and `release-assets.githubusercontent.com`. Asset URLs from the unsigned JSON must match `https://github.com/ryanportfolio/SecureWall/releases/download/<tag>/<name>` exactly. A system proxy on a port other than 443 is not supported, as with the AI feature. GitHub's unauthenticated limit (60 requests an hour per IP) is shown as its own message.
4. **Candidates.** Drop drafts, releases whose version is not greater than the installed one, skipped versions, and releases missing `SecureWall_<Platform>.msi`, `SHA256SUMS.txt` or `SHA256SUMS.txt.sig` (not ready yet). Sort the rest by version, highest first. GitHub's prerelease flag is unsigned, so it only orders work; the decision uses the signed line in step 5.
5. **Verify, highest first.** For each candidate in turn, at most 5: download the manifest (64 KiB cap) and signature (1 KiB cap), verify against the embedded keys, check `Repository`, `Tag` and `Version`, and find the line for the installed `Platform`. Accept the first candidate that verifies and whose signed `Prerelease` is `false`, or `true` with prereleases enabled. A candidate that fails verification or is a prerelease the user did not opt into is skipped, and the next one is tried, so a newer prerelease or a newer broken release never hides an eligible older one. If none is accepted, the result names what was skipped: "signature does not verify" (wrong key, edited manifest, truncated signature), "manifest does not match this release" (tag, version, repository or missing platform line), or "only prereleases are newer".
6. **Show.** Version, publish date, prerelease flag and the release notes as plain text in a read-only box (no HTML or Markdown rendering, capped at 20,000 characters), labelled as coming from GitHub and not covered by the signature. The dialog says the network pauses for up to a minute during the upgrade. Buttons: Install, Later, Skip this version (stored per user; a skipped version is not offered again, a newer one is).
7. **Download.** Install requires the service to be unlocked (decision 6). The controller requests a fresh grant, downloads the MSI to `%LOCALAPPDATA%\SecureWall\updates\<tag>\` with a 100 MiB cap, ends the grant and checks the hash, for early feedback only.
8. **Elevate.** The controller starts `SecureWall.exe /apply-update "<msi>" "<manifest>" "<sig>" <requesting user SID>` with the `runas` verb, which shows the UAC prompt. A controller that the logon task already started elevated (an administrator account) gets no prompt; the Install click is then the consent.
9. **Elevated helper.** Refuses to run as SYSTEM or as the service. In this order:
   1. Accepts source paths only on a local fixed drive, without UNC or device prefixes or reparse points, under `%LOCALAPPDATA%\SecureWall\updates\` of the requesting user.
   2. Reads the manifest and signature into memory under the size caps and verifies the signature first. The tag is taken only from the verified bytes and must match `^v\d+\.\d+\.\d+$`; the version must be greater than the installed one (the controller is not a security boundary).
   3. Validates the staging ancestry: `C:\ProgramData` lets ordinary users create folders and gives the creator full control, so a user could precreate `SecureWall Updates` and later rename or replace a verified folder before `msiexec` opens it. The helper checks every ancestor the way `MachineDataGuard.Validate` does (trusted owner, no write, delete or delete-child right for non-administrators, no reparse point), creates a missing `%ProgramData%\SecureWall Updates` itself with an ACL granting only SYSTEM and Administrators, and refuses, without repairing or deleting, a parent that fails the check; the dialog then names the folder for an administrator to remove.
   4. Holds a machine-wide single-instance mutex for the rest of its run, so two helpers never stage at once. Deletes earlier staging folders under the validated parent, never following reparse points and leaving any folder it cannot delete (a package still open by a running installation), then creates a fresh uniquely named folder and copies the three files in under fixed names.
   5. Re-verifies the signature and hash on the protected copies, so a same-user process that swapped the files in `%LOCALAPPDATA%` after step 7 gains nothing.
   6. Opens the MSI database (msi.dll, read-only) and checks `UpgradeCode`, the Template summary property against the installed `Platform`, and `ProductVersion` (four-part, `0.5.1.0`) against the manifest's three-part `Version`.
   7. Starts `<System32>\msiexec.exe` by full path: `/i "<protected msi>" /l*v "<protected dir>\install.log"`, full UI. When the helper's user differs from the requesting user (an over-the-shoulder UAC prompt, where a standard user typed an administrator's credentials), it adds `SECUREWALL_SUPPRESS_USER_ACTIONS=1`. That property hides the failure page's "open log" button (an impersonated shell action that would otherwise start Notepad as the administrator on the standard user's desktop) and skips step 10's `StartController`, which would otherwise start a tray under the administrator's account. Then the helper exits.
   The helper never runs as SYSTEM, and no SYSTEM action deletes anything under `%ProgramData%\SecureWall Updates`.
10. **Upgrade.** Part 1 runs. Step 3 terminates the controllers. When the requesting user ran the MSI themselves, step 10 restarts their tray on the new version, which shows "Updated to v0.5.1" when its version differs from the one it recorded before the update. In the over-the-shoulder case the standard user's tray returns at their next logon or from the Start menu, and shows the notice then.

The download and every HTTP request run in the controller under the user's token. The LocalSystem service never contacts the network for updates; its only part is adding and removing the transient permit.

### Test harness

R2, R3, R5 and R8 need releases and servers the real repository will not provide: tampered manifests with valid signatures, future versions, hostile redirects and slow bodies. They run against a VM-only harness:

- an `UpdaterTest` build configuration that embeds the test public key instead of the production keys and stamps itself visibly like the fault build; the installer and core tests fail any release artifact that carries the test key or the stamp;
- a local HTTPS server that stands in for `api.github.com` and `github.com`, reached through the VM's hosts file and a test root CA installed only on that VM, so the product's URL constants stay unchanged;
- scripts that generate signed, tampered and future releases with the test key.

The first real end-to-end update through GitHub happens with the first release after the updater ships, and is recorded as R8's production check.

### Docs and tests

Phase A (v0.5.0):

| File | Change |
|---|---|
| `docs/SECURITY.md` line 110 | "MSI repair, modification and in-place upgrades are rejected" becomes: repair and modification are rejected; upgrades from v0.5.0 on run in place. Add an "In-place upgrade" section with the sequence, the blocked window, the marker and the recovery steps. |
| `README.md` line 19 | Same correction; how to upgrade (run the newer MSI) and the one-time manual step from v0.4.2 or older. |
| `tools/manual/README.md` line 31 | Same correction. |
| `.claude/reference/deployment.md` | The v0.4.2-to-v0.5.0 manual step, the three-field version rule, the `profiles.json` diff review and the fault-build ban in the release procedure. |
| `docs/HARDENING-VALIDATION.md` | Rows U1 to U16 added; row 41 ("Repair, equal-version rebuild and upgrade attempts") rewritten to cover only repair, equal version, downgrade and cross-architecture refusal. |
| `tests/installer/Test-SecureWallInstaller.ps1` | Lines 88 (no REP), 90 (related-product refusal) and 92 (existing service refusal) change to the new contract: REP placement, upgrade range and minimum, equal-version, downgrade and cross-architecture refusal, per-side conditions and sequencing, autowhitelist condition, `RegistryKeys` without force delete, `Platform` value, fault-build stamp absent. Lines 86 (teardown condition), 89 (repair refusal) and 93 stay. |
| `docs/plans/2026-09-06-pre-switch-hardening.md` | Note under SW10 that this plan supersedes the "no in-place upgrade" choice from v0.5.0 on. |

Phase B (updater):

| File | Change |
|---|---|
| `docs/SECURITY.md` line 141 | Replace "SecureWall has no update feed and no update client" with the release-check description: GitHub releases of `ryanportfolio/SecureWall` only, controller only, signed manifest with owner-held keys and its limits, per-user transient TCP 443 permit and its limits, elevated helper checks, TinyWall feed still banned, shipped data still MSI-only. |
| `.claude/reference/deployment.md` line 7 | Same correction; the signing step, key custody, rotation and re-signing after a workflow rerun. |
| `README.md` line 29 | Replace the "no update feed" paragraph with Check for updates and its prerelease setting. |
| `UPSTREAM.md` line 117 | Same correction as SECURITY. |
| `docs/TESTING.md` line 18 | The bullet becomes: no TinyWall update client; `WebClient` and `Download*` calls nowhere; `HttpClient` only in the AI client and the release client; service-side files never reference the release client; the release client's host list is GitHub only; no test key or test stamp in release artifacts. |
| `UpdateFeedRemovalTests.cs` | Keep every current pattern, including `UpdateChecker`, `WebClient`, `DownloadFile`, `DownloadData` and `DownloadString`, banned in every product file, and keep the `AutoUpdateCheck` legacy-load tests (new code never uses that name). Add: `HttpClient` and `HttpClientHandler` appear only in `TinyWall/OpenAiCompatibleExplainClient.cs` and the named release-client file; `TinyWallService.cs`, `TinyWallDoctor.cs` and `TinyWall/Installer/*.cs` never reference the release-client types; the release client's only URL constants are the GitHub hosts above. New code is named `ReleaseCheck` / `SignedReleaseManifest`. |
| `docs/HARDENING-VALIDATION.md` | Rows R1 to R10 added. |

Pure tests arrive with the code they cover: manifest parsing and signature verification (valid, wrong key, DER signature, truncated, wrong tag, version, repository or prerelease flag, missing platform line), release selection (drafts, prerelease opt-in decided by the signed line, equal and lower versions, missing assets, skipped version, fallback past a newer prerelease or a newer release that fails verification, the 5-candidate and 3-page limits) and URL, redirect and size policy in B3; permit lifetime, user SID, LOCK removal, removal on requesting-process exit and refusal of a reused process ID in B4; `/apply-update` identity, path, tag and ancestry refusals in B6.

## VM test matrix

All cases run on a disposable VM with a clean snapshot and local console, as `docs/HARDENING-VALIDATION.md` requires. Each case captures the existing before-and-after inventory plus, for upgrade cases, WFP filter state polled as fast as the capture allows during the MSI run (target 250 ms; record the actual interval) and continuous external probes from a second VM. The upgrade bundle and runner come from step A6.

| Case | Required result |
|---|---|
| U1 Upgrade from v0.4.2 | Running v0.5.0 over v0.4.2 stops at `FoundIncompatibleVersion` before any change; v0.4.2 service, policy, files and WFP state unchanged. Manual uninstall then fresh v0.5.0 works. |
| U2 Upgrade, full UI and silent | v0.5.0 to v0.5.1, where v0.5.1's `profiles.json` and `hosts.bck` differ from v0.5.0's. Before upgrading, delete one exception that first-install detection added. Exit code 0, never 3010. Same service registration and `ImagePath`; `pwd`, `hosts.orig` hashes unchanged; `config` unchanged except the pruning of "until reboot" exceptions (create one beforehand) and any deliberate migration; shipped data equals v0.5.1's; the deleted detected exception is still absent (no autowhitelist); baseline filters present in every WFP sample; no unknown external connection succeeds at any time; saved allows work after; Windows Firewall journal and audit journal re-created by the new service; blocked window measured and recorded; snapshot directory and marker gone. Full UI only: tray back for the installing user. |
| U3 Password-locked service | MSI upgrade succeeds from an elevated prompt; the lock and password survive; `pwd` hash unchanged. |
| U4 Custom install directory | First install with `INSTALLDIR=` set to another folder under Program Files; upgrade keeps it and the service starts from it. |
| U5 New service fails to start | Fault build as the new version: it rewrites `config` and leaves a `config.recovery`, then fails at start. MSI rolls back; old files and old shipped data restored; `config` byte-identical to before the upgrade and the new `config.recovery` gone; `pwd` restored with the secret DACL (Users cannot read it, and could not read the snapshot copy at any point); old service Running with restart policy restored; no SCM restart of the new binary during rollback; baseline continuous; snapshot and marker gone; event log names the step. |
| U6 Failure at each step | Fault build as the old version: service refuses to stop within the deadline; snapshot write fails halfway (step 2 must not restore the partial copy). Normal versions: a localization DLL held open exclusively during `InstallFiles`; registry write denied; MpsSvc stopped before the upgrade. Each ends with the old version running, policy intact, `sc qfailure` showing the three restart actions and no marker. Fault build as the new version whose service ignores step 6's stop: step 2 finds phase `snapshot-complete` with the new binary Running, stops it, restores the snapshot and starts the old service; when the new service cannot be stopped at all, step 2 fails and keeps both the snapshot and the marker. |
| U7 Cancel during progress | Cancel in the MSI progress dialog after step 3: same result as U5. |
| U8 Hard reset mid-swap | VM reset after step 3, during `InstallFiles` and during step 7. After boot: external probes blocked until a service commits policy; record whether the Automatic service starts, what the next MSI run does with the suspended installation (step 2 with the kept snapshot), and the exact local-console recovery. |
| U9 Rejected machine data | Hostile ownership or ACL in the data tree before upgrading: step 3 refuses before stopping anything; event 1000; tree hashes, owners and ACLs unchanged. |
| U10 Equal version and downgrade | Rerun of the installed MSI and an older upgradeable MSI both refused before any action; repair still refused. |
| U11 Journals across upgrade | Stop path: journals cleared at the old service's stop and re-created at the new service's start; original notification values and audit flags unchanged after a final explicit uninstall, which then removes `HKLM\Software\SecureWall`. Failed-restore path: make the Windows Firewall restore fail at stop (MpsSvc fault in the fault build); the journal entry survives the upgrade and a later uninstall restores from it. Roll back an explicit uninstall: `InstallDir` and `Platform` are still present. |
| U12 Controllers in several sessions | Two users logged on with trays open: both terminated before file replacement, no reboot required. Record whether `FilesInUse` appears; second user's tray returns at next logon. |
| U12a Tray starts during the swap | Fault build pauses between steps 3 and 7. Log a second user on, or start `SecureWall.exe` from the Start menu: the tray shows "being upgraded" and exits, the service stays stopped, the upgrade completes with exit code 0. Separately, start the service by hand during the pause: step 3's re-check or step 7 fails the upgrade closed and rollback restores the old version. After a hard reset with the marker left behind, the service starts at boot and a tray's repair path works again once no MSI runs. |
| U13 SCM restart suppression | Fault build as the old version exits abnormally while handling 3(e)'s stop request: SCM does not restart it. Fault build as the new version crashes at step 7 and again during rollback: no SCM restart of the new binary. Fault build pausing between step 7 and commit: `sc qfailure` shows no restart actions even though the new service's own startup ran `EnsureHealth`. After a committed upgrade, `sc qfailure` shows the three restart actions again. Record whether disabling failure actions cancels a restart SCM had already queued. |
| U14 Baseline schema change | Fault build whose baseline adds a filter in a new layer and drops a sublayer: after upgrade only the new set exists; after a rolled-back upgrade only the old set exists. |
| U15 BFE restart during swap | Restart BFE between steps 3 and 7: baseline reloads, external traffic stays blocked, upgrade completes or rolls back cleanly. |
| U16 Architecture | x86 MSI installed on x64 Windows, then upgraded with the next x86 MSI: succeeds, values stay under `WOW6432Node`, journals and marker in the 64-bit view. An x64 MSI over that x86 install, and over an arm64 install: refused before any change. |
| R1 Transient permit | Locked: Check asks for the password first. Unlocked: the grant appears in WFP with the requesting user's SID, works for that user's controller, stays blocked for a SYSTEM process running the same image and for a second logged-on user's `SecureWall.exe`, and survives the controller's many short pipe connections, disappears at the end of the check, at expiry, on LOCK, when the controller process is killed, in BlockAll, under display-off blocking and on a failed reload. A fresh grant covers the download after a long pause on the notes. `config` hash unchanged; no permit after a service restart. |
| R2 Release selection (harness) | Prereleases off: a signed prerelease is not offered; a signed full release is, including an older full release behind a newer signed prerelease and behind a newer release whose signature fails. A GitHub prerelease flag that disagrees with the signed line is ignored. On: newest offered. Eligible release on page 2 of the API: found. Drafts never. Equal or lower versions never. A newer release without `.sig` is skipped and the next signed one offered. Skip this version: not offered again; a newer one is. |
| R3 Tampering (harness) | Flipped byte in the MSI, edited manifest, signature from an unknown key, DER-encoded signature, manifest tag, version, repository or prerelease flag not matching the release, and a replayed older signed release: each refused before UAC with the message step 5 or 7 defines. |
| R4 Swap after verification | Replace the MSI in `%LOCALAPPDATA%` between the controller's check and the UAC prompt: the helper's re-verification refuses; nothing installs. Separately, as an ordinary user, precreate `%ProgramData%\SecureWall Updates` (user-owned, then with a user write ACE, then as a junction to a system folder): the helper refuses before copying, leaves the folder and the junction target untouched, and a later committed upgrade deletes nothing there. Launch `/apply-update` by hand with a UNC source path, a `..` tag and an unsigned manifest: each refused before any file is written. |
| R5 Transport limits (harness) | Redirect to `http://` or to a non-GitHub host, an asset URL outside the release path, more than 5 redirects, oversized API and manifest bodies, slow-drip body past the deadline: each refused; no partial file used. |
| R6 UAC declined | Nothing changes; the controller keeps running and can retry. |
| R7 Account types | Administrator with an already elevated tray (no prompt): full UI, tray restarts with the "Updated" notice. Standard user with an administrator's credentials: full UI runs, the failure page shows no "open log" button, no process starts under the administrator's account on the user's desktop, and the user's own tray returns at next logon. |
| R8 End to end (harness, then production) | Harness: v0.5.x to a newer test-signed release through the button on x64, arm64 and x86 VMs: U2's results plus the "Updated to" notice, old staging cleaned, `install.log` present. Production: the first real release after the updater ships, through the button. |
| R9 Network failure | No network, DNS blocked, proxy on port 8080, GitHub 403 rate limit: clear message each time; transient grant removed. |
| R10 Blocked-window notice | The full-UI MSI welcome page and the update dialog both state the network pause before anything stops. |

## Build sequence

Each step ends with the core tests, full MSBuild Debug and Release, the installer contract test and a fresh review. Nothing is installed on the development host.

**Phase A, in-place upgrade (ships as v0.5.0):**

1. A1 Registry ownership: `RegistryKeys` without force delete, `Platform` value, 64-bit view for SecureWall-owned subkeys, commit-phase key cleanup on explicit removal. Contract test.
2. A2 Maintenance modes `/msi-upgrade-stop`, `/msi-upgrade-restore`, `/msi-upgrade-start`, `/msi-upgrade-abort`, `/msi-upgrade-commit` in `Program.cs` and `TinyWallDoctor.cs`, with the decisions in a pure `UpgradeLifecyclePolicy` tested against a fake SCM and fake file system: snapshot only after stop, hash manifest last, exact-set restore, partial-snapshot refusal, secret DACL on `pwd`, idempotent restore decided by the marker phase (including a Running new binary after a failed step 6), snapshot kept when the service cannot be stopped, restart policy off from step 3 until commit or rollback and `EnsureHealth` leaving it alone under an active marker, Stopped re-checks, marker set, phase, check, clear and transaction-ID mismatch refusal, and a list of service-written files the snapshot must cover. Marker check in `EnsureServiceInstalledAndRunning`, `/install` and controller startup (notice and exit).
3. A3 `Product.wxs` changes and the installer contract test changes listed above.
4. A4 `UpgradeFaultTest` build configuration, usable as old and new version, with its stamp and release refusal.
5. A5 Phase A docs from the table above, release-notes template (manual step from v0.4.2) and rows U1 to U16.
6. A6 VM tooling: extend `tools/vm/Prepare-SecureWallVmBundle.ps1` and the runner for two-version and fault-build bundles, WFP polling, continuous probes and per-case inventories.
7. A7 Build v0.5.0, a local v0.5.1 test MSI with changed shipped data, and both fault builds; run U1 to U16 on the VM. Release v0.5.0 only after they pass.

**Phase B, updater (a later release):**

8. B1 Owner creates the primary, backup and test key pairs (owner action; no key material is created by an agent). Public keys committed.
9. B2 Manifest header in `Build-SecureWallRelease.ps1`; installer test.
10. B3 Pure core: manifest parser and verifier, release selection, URL and redirect policy, with their tests; `Sign-SecureWallRelease.ps1` on the same parser.
11. B4 Service: per-user transient permit message, lifetime and removal triggers, with tests.
12. B5 Controller: tray item, About button, password prompt, update dialog, settings (include prereleases), download with caps. Confirm decision 3 with the owner first.
13. B6 `/apply-update` elevated helper: path and tag checks, verify-first, ancestry validation, staging, re-verification, MSI property check, msiexec launch with the suppress property, with tests; `SECUREWALL_SUPPRESS_USER_ACTIONS` in `Product.wxs` and the failure dialog.
14. B7 `UpdaterTest` configuration, harness server and release generator; Phase B docs and `UpdateFeedRemovalTests` changes; rows R1 to R10.
15. B8 VM cases R1 to R10 and a full U2 rerun through the button. Release.

## Decisions

Approved by the owner on 2026-11-01 ("Yes, recommended").

1. **v0.4.2 and older:** one last manual uninstall-and-install reaches v0.5.0. No re-caching of old packages.
2. **Signing:** owner-held ECDSA P-256 key in the Windows CNG store, plus an offline backup key, with a manual signing step per release. No CI-secret key. Authenticode is reconsidered before the first non-prerelease release.
3. **Prereleases:** not settled. The plan offered no recommendation here, so the approval does not cover it. The build defaults "Include prereleases" to off, as the original brief asked (the owner opts in), and this is confirmed with the owner before step B5. Note for that conversation: `release.yml` refuses to attach unsigned MSIs to a non-prerelease release, so until Authenticode exists every release is a prerelease, and with the setting off the button never offers anything.
4. **Network permit:** a transient grant of at most 10 minutes per network phase, for the requesting user only, never saved.
5. **Automatic checks:** manual button only.
6. **Password lock:** Install requires unlocking when a password is set. Because the permit request is a password-gated message, the check itself also asks for the password when locked.
7. **Release split:** the in-place upgrade ships alone as v0.5.0; the updater follows in a later release.
