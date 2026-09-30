# Pre-switch review: TinyWall 3.3.1 to SecureWall v0.3.0

Review date: 2026-09-29, status updated 2026-09-30. Product source reviewed: HEAD `fe0d011` (v0.3.0 is tag `5385cab`; `fe0d011` changes only `.claude/`, `.agents/` and `.codex/`). Fixes: branch `claude/pre-switch-fixes`, audited integration head `b192054`; after it, the release-prep commits `ca2cc3d` (version 0.4.0, `docs/releases/v0.4.0.md`, `release.yml` manual default tag `v0.4.0`) and `d68b4b8` (release note wording) change no product logic. Upstream reviewed to `62b088f`. Every finding below comes from a review round whose claims were re-checked by a separate auditor; see [Method and limits](#8-method-and-limits).

Severity: HIGH can break networking or security, or lose data, on the owner's PC. MEDIUM is a real defect or risk with narrower impact. LOW is minor. Intent questions are in section 4, not in the findings.

## 1. Summary

**Verdict: not ready for a production switch by the project's own gate.** The project's rule (`docs/HARDENING-VALIDATION.md:3`, `docs/TESTING.md:64`) calls for the VM matrix to pass before a production switch, and it has zero recorded runs. Every fix listed below was built, unit-tested where the logic allows, and checked by an independent auditor, but none has run on a live machine. v0.3.0 is unsigned, contains none of these fixes, and its only live test (2026-07-14, `docs/TESTING.md:66-68`) predates the current failure model.

**Owner decision: test on the real PC, not a VM.** The owner has decided to try SecureWall directly on their own PC, which currently runs TinyWall 3.3.1, instead of running the VM matrix first. That makes the first run on this PC the first live run of every fix. It does not make the build ready by the project's gate. Section 6 is the safety net for doing this: work at the local console, create a restore point and ideally a disk image, keep offline copies of both installers, export TinyWall's rules to a `.tws` file, and read "Getting back online" before starting.

The issues that mattered most for this PC, and where they stand. Fixes are merged into `claude/pre-switch-fixes` (audited integration head `b192054`), not yet into `main`; section 3 gives each finding's status line and commit.

- **SW-01 (HIGH), fixed on `claude/fix-h1`:** a failed environmental reload stopped the service with exit code 0, so Windows never restarted it and the PC stayed offline until someone started the service, relaunched the controller or rebooted. The service now keeps the committed policy where that is safe and retries, rebuilds its WFP session in process when it must revoke, and reports a failure exit code with repeated SCM restarts.
- **SW-02 (HIGH in this review, MEDIUM per Codex), fixed on `claude/fix-h2`:** a bad ACL or owner inside `%ProgramData%\SecureWall` blocked every uninstall path. MSI removal (Apps or `msiexec /x`) now runs a SYSTEM-only emergency release that removes SecureWall's WFP objects without reading file contents from the rejected folder. Each failed release step now writes an Application event 1001 naming the step (`claude/fix-y2`). Interactive `SecureWall.exe /uninstall` still refuses in that state.
- **SW-03 (HIGH if Learning mode is used), superseded: Learning mode is removed.** The owner does not need Learning mode, so `claude/fix-nl` removed it entirely. The service refuses a switch to Learning, and a stored or imported configuration that names Learning loads as Normal. The earlier svchost and loopback fixes to Learning on `claude/fix-h3` no longer apply. Rules learned by TinyWall or by SecureWall up to v0.3.0 are kept and must be reviewed by hand.
- **SW-08, SW-11, SW-14 (MEDIUM), fixed:** `SecureWall.exe /uninstall` now works while password-locked (upstream port); a prompt Allow no longer widens a LAN-only rule; Enter no longer triggers Allow unless the owner moved focus to it, and Allow arms only after the popup has been still for 1 s and its risk warnings have loaded.
- **SW-12 (MEDIUM), partly fixed:** the arming delay stops instant clicks, but a same-user process can still wait it out and click Allow when no password is set. Setting a password and keeping SecureWall locked remains the control. The owner plans no password, so this residual risk applies in full (see "Owner decisions").
- **Codex found two more MEDIUM issues:** the deny baseline's WFP provider named the SecureWall service, so a Disabled or Manual start type let the baseline drop at boot (fixed on `claude/fix-h2`); Learning could learn loopback-only traffic as a full grant (fixed on `claude/fix-h3`, then superseded by the Learning removal).
- **Dormant TinyWall updater deleted** (`claude/fix-up`): SecureWall contains no update client and no update feed URL, so it never checks for updates.

What remains:

- **Live validation of every fix.** Integration is complete: the upstream ports, the ten fix branches and the three follow-up branches (`claude/fix-up`, `claude/fix-nl`, `claude/fix-y2`) are merged into `claude/pre-switch-fixes`, audited integration head `b192054`. At that head 391 core tests pass, Debug and Release builds add no warnings, both self-tests pass, and the installer static checks pass 156 of 156; an independent audit found no defects (section 7). The two release-prep commits after it (`ca2cc3d`, `d68b4b8`) set version 0.4.0 and add release notes: `ca2cc3d` was audited before commit (391 core tests, installer static checks 156 of 156), and `d68b4b8` changes two lines of the release notes. Each fix added or updated a `docs/HARDENING-VALIDATION.md` row; none has run. The owner's first run on this PC will be the first live test.
- **Codex review of the integrated branch: done.** A Codex full review of the first integration (`f6a1c52`) found one MEDIUM, fixed on `claude/fix-y2` (emergency release failures now reach the Application log), one LOW, fixed in the second integration (stale `UPSTREAM.md` claims), and three Learning findings that are moot now that Learning is removed. Each follow-up branch had its own Codex review (section 7).
- **Popup unlock is global until relock.** When the owner unlocks from a popup, the service accepts privileged requests from any `SecureWall.exe` controller until the popup flow relocks after that one Allow. The owner decided not to scope it; with no password set there is no lock, so the case does not arise.
- **Baseline at boot without a provider `serviceName`.** The fix that keeps the baseline across a Disabled or Manual start type rests on the `FwpmProviderAdd0` remarks; other Microsoft pages are inconsistent. The VM row "Service Disabled or Manual, then reboot" must confirm it. Side effect by design: disabling the service in services.msc now leaves the PC offline until SecureWall is removed.
- **Held LOW findings.** Most of SW-23 to SW-46 and the LOW Codex findings are held by owner decision; section 3 marks which LOWs were fixed and which are moot.

Before switching: build from `claude/pre-switch-fixes` or, once published, use the v0.4.0 pre-release; keep offline copies of both installers and a TinyWall `.tws` export, create a restore point or disk image, work at the local console, and leave the AI helper off unless you need it. Follow section 6 step by step.

### Owner decisions

Recorded answers from the owner, 2026-09-29 and 2026-09-30:

1. **Scoped popup unlock: not done.** The password stays optional (the default), and the owner plans to set no password. Residual risk (SW-12): with no password, any process running as the owner can wait out the 1 s arming delay and click Allow on its own prompt, getting a permanent allow. Setting a password and keeping SecureWall locked is the only control against that.
2. **Dormant updater: deleted** on `claude/fix-up` (`b5f70d8`, merged in `b49cd71`). The update client, the controller's update timer, the Settings update button, `/updatenow`, the service's two-day update check, the `tinywall.pados.hu` feed URL and `docs/pad_file.xml` are gone (Q23, Q25). Unused update strings and one icon remain in the resources; no code uses them. New SecureWall builds have to be fetched and installed by hand.
3. **Learning mode: removed** on `claude/fix-nl` (`4ff3bac`, merged in `2842c3e`). This supersedes SW-03, Codex red 2 and yellow 3, and integration audit item D3.
4. **Icons: replace with original icons.** The Icons8 and other third-party icons (inherited from TinyWall, plus `copy.png` and `open_folder.png` from the P3 port, credited in `Attributions.txt`) will be replaced by original SVG-sourced icons. The design and drop list are approved, and the PNG, ICO and BMP files have been rendered from the SVG sources in the `sw-icons` worktree (branch `claude/custom-icons`, not yet committed). Wiring them into the product is a separate pull request after this one, so this branch still ships the third-party icons.
5. **Held LOW findings stay held.** No held LOW is fixed before the switch.
6. **Visual Studio Build Tools stay installed,** because building SecureWall needs them. Visual Studio remains registered as the crash debugger (`AeDebug` `Debugger=vsjitdebugger`), so a process crash on this PC can still open blocking JIT debugger dialogs. That registration can be turned off separately without uninstalling Build Tools; doing so remains the owner's option.
7. **Test on this PC without a VM.** See the verdict above and section 6.
8. **Release type: pre-release first.** After this branch merges, v0.4.0 is tagged as a GitHub pre-release, so `release.yml` attaches the unsigned installers with its prerelease gate unchanged. The owner flips it to a full release after testing on this PC. A full release still conflicts with `docs/SECURITY.md` and `.claude/reference/deployment.md`, which require signing and the VM matrix for production; this build is unsigned and not live-validated.

Still open:

9. **Intent questions** in section 4 have no recorded answers, except Q6 (settled by the Learning removal) and Q23 and Q25 (settled by the updater removal).

## 2. Upstream TinyWall status

- **Head unchanged.** `pylorak/TinyWall` master is still `62b088f` (committed 2026-08-15), re-queried live on 2026-09-29. No newer tag than `rel-3.5.1` and no GitHub releases. The maintainer says TinyWall 3.6 is in progress but unpublished (#127; see also #132, #142), so expect a large batch push later.
- **Base version.** SecureWall is based on TinyWall 3.5.1 (pin `1df71b1`). Every upstream fix between the owner's installed 3.3.1 and 3.5.1 is already inherited.
- **58 commits since the pin, by class at HEAD `fe0d011` (before the ports):** backported 3, equivalent in fork 5, not applicable 14, missing and relevant 6, missing and cosmetic 30. (The round-1 report counted `a3418c4` and `63e411a` as equivalent; the audit reclassified both as not applicable, infrastructure for the missing `3146d95`.)
- **Full list:** the per-commit classification and issue triage are in `UPSTREAM.md` on the upstream-ports branch (`a257605`), summarized under "Port status" below. At HEAD `fe0d011`, `UPSTREAM.md` has only a short backported and not-backported list.

The owner asked for applicable upstream fixes to be ported into SecureWall. The ports and the `UPSTREAM.md` update are the first five commits of `claude/pre-switch-fixes` and ship with it.

Port status: done on branch `claude/upstream-tinywall-ports` (base `fe0d011`). Each port was built, passed the core tests and an independent audit, and the branch received its own Codex review (section 7).

- **P1 WFP wrapper (`273ba57`):** `8f45054`, `c2ae8df` and `a56488f` ported; `2329049` ported with an adapted indexer setter. Covers SW-40 and SW-43.
- **P2 password storage (`8544481`):** `b741831`, `3146d95`, `c4cd972` and the `pwd` parts of `63e411a` and `7762731` adapted; `d976ce5` ported (a comment). Covers SW-08 and SW-18.
- **P3 controller and UI (`1ef4f5e`):** `7270d96`, `9a25ffb`, `7fce29c`, `7bfbcc3`, `53800aa`, `bfd90bc`, `54d1aea`, `011c33d`, `d9b5c62`, `6bf5772` and `cf81bb8` ported; `7e09aac` adapted; the resx-only `ee0a43e` and `521ea28` skipped as not needed. Covers SW-21, SW-41 and the `7fce29c` part of SW-43.
- **P4 database and block reason (`31fe6e1`):** `fa57ba0` ported, with `profiles.json` edited to match and an installer check that the payload matches the source profiles; `2ed2d37` adapted (Network Activity shows the block reason; a net-event subscription failure stays fatal to initialization, see Q2). Covers SW-39.
- **P6 `UPSTREAM.md` (`a257605`):** lists each of the 58 commits since the pin once: 27 backported, 3 equivalent in the fork, 1 not backported (`487a0eb`, performance only, intentionally not ported), 12 not applicable (including the five dormant-updater commits), 15 cosmetic or tooling. It also lists the upstream issues to watch. Covers SW-46. On `claude/pre-switch-fixes` the counts are 26 backported and 13 not applicable: the updater removal (`b5f70d8`) moved `3448b6b` to not applicable, and the second-integration follow-up (`b192054`) corrected stale claims about `fa57ba0` (it reaches reinstalls through the shipped-data refresh, not only fresh installs) and about which of `a56488f`, `8f45054` and `c2ae8df` run in live code. The Learning removal marked upstream #132 and #139 as no longer applicable.

Recommended ports, ranked. This list predates the ports: items 1 to 6 and 8 are done except the fork-only IPv6 display fix in item 5 (SW-42, held). In item 7, the owner decided the dormant updater (deleted) and the Learning timeout (moot, Learning removed); the other three are still open.

1. `7270d96` TaskbarCreated UIPI filter (SW-21). About 20 lines. Do before the switch.
2. `3146d95` password-file ACL together with `b741831` hash format (SW-18). Keep the fork's ACL model; give `pwd` a protected SYSTEM and Administrators DACL; upgrade the hash on the next successful unlock.
3. `fa57ba0` Defender `MpCmdRun.exe` special exception (SW-39).
4. `8f45054` treat `FWP_E_NO_TXN_IN_PROGRESS` as closed on abort (SW-40).
5. `c4cd972` PasswordForm guard (SW-08), `9a25ffb` ProcessLibrary removal (SW-41), and the fork-only IPv6 prompt formatting fix (SW-42).
6. Hygiene batch: `a56488f`, `2329049`, `c2ae8df`, `7fce29c` (SW-43).
7. Decide rather than port: dormant updater (Q23), Release `/develtool` (Q24), net-event subscribe failure (Q2), Learning timeout (Q6), versioned-path handling (#133, #142).
8. Optional UI: `2ed2d37` block reason, `7e09aac` and `7bfbcc3` copy path and open folder, the dark-mode fixes, `53800aa`.

Upstream issues and pull requests to watch:

- **#142** wildcard path rules. The maintainer said "let's roll with this one" (2026-09-26). Decide intent before porting: wildcards widen what a rule matches, against SecureWall's exact-identity prompt model.
- **#133** follow exceptions across versioned folders and MSIX updates. SecureWall has the same exact-path problem (stale rules, re-prompt after each update).
- **#132** forced timeout for auto-learn. No longer applicable: SecureWall removed Learning mode (Q6).
- **#127** Defender `MpCmdRun.exe` blocked when Windows Update is off (SW-39).
- **#129** allow rules occasionally ignored with NordVPN. No root cause upstream; SecureWall handles two plausible causes better. A VPN kill switch's own WFP filters can still block traffic SecureWall allows (Q22).
- **#59, #136** "Pick a UWP app" rules never match full-trust Store apps (Spotify, Arc). SecureWall inherits the picker; allow such apps by executable or from a prompt.
- **#141** WMI to ETW for process-start tracking. SecureWall uses the same WMI watcher for child inheritance, so short-lived children can miss inheritance and prompt instead.
- **#121** TinyWall 3.5.1 uninstall fails while password-locked. On switch day, unlock TinyWall before uninstalling it.

## 3. Findings

Each finding lists its source (round and item in the working reports), confidence, evidence at HEAD `fe0d011`, what goes wrong, and a suggested fix. "Inherited" means the same code exists in TinyWall 3.5.1.

Counts: HIGH 3, MEDIUM 19, LOW 24.

Each finding ends with a "Status" line. Fix branches (`claude/fix-<id>`) and the ports branch (`claude/upstream-tinywall-ports`) are based on `fe0d011`; each fix was built with MSBuild Debug, passed the core tests and an independent audit, and received a Codex review whose confirmed HIGH and MEDIUM findings were fixed and re-audited, except one MEDIUM the owner decided not to fix (SW-15 status, section 7). All of them are merged into `claude/pre-switch-fixes`. Three follow-up branches based on the first integration (`f6a1c52`) were merged after it: `claude/fix-up` (updater removal), `claude/fix-nl` (Learning removal) and `claude/fix-y2` (emergency release events). The audited integration head, `b192054`, was rebuilt, re-tested and audited (section 7). "Needs live test" means the fix added or updated a `docs/HARDENING-VALIDATION.md` row that has not run. Evidence lines still cite HEAD `fe0d011`.

### HIGH

#### SW-01. An environmental reload failure stops the service for good behind the deny-all baseline

- **Source:** R2 H1 (startup sub-claim corrected by the R2 audit). Different path from SW-02.
- **Confidence:** confirmed by code for the control flow and SCM reporting. How often each trigger fires on a real PC is plausible, not measured.
- **Evidence:** `TinyWall/TinyWallService.cs:1581` (`FailClosedCore` sets `RunService = false`), `:2155-2170` (loop exits), `:2677` (`service_shutdown success`), `:2684-2697` (STOPPED with exit code 0, `Process.Kill()`), `pylorak.Windows.Services/ServiceBase.cs:279-295`, restart action `pylorak.Windows.Services/ServiceControlManager.cs:111-165` set from `TinyWall/TinyWallDoctor.cs:434`. Baseline: `TinyWallService.cs:459-514`.
- **Triggers that call `FailClosed` without any user action:** a network change when adapter enumeration fails (`TinyWall/Prompting/EnforcementPolicy.cs:148-151`, `pylorak.Windows/NetworkAdapterEnumerator.cs:131-150`); the 30-minute reload (`TinyWallService.cs:1910-1918`); USB or volume mount changes (`:2123-2131`, `:2742-2765`); every display on/off (`:1939-1951`); rule expiry with a failed config save (`EnforcementPolicy.cs:131-143`). Inside the reload: the 5000 ms WFP transaction timeout (`TinyWallService.cs:84`), a failed `FwpmFilterDeleteByKey0` after a BFE restart (`:396-397`), a PathMapper cache build failure during volume arrival (`pylorak.Windows/PathMapper.cs:103-107, 264-267`), and the WSL race in SW-22.
- **What goes wrong:** SCM sees a clean stop, so the restart-on-failure action never fires, and Windows logs no service failure. The persistent and boot-time baseline then blocks every non-loopback connection, including DHCP and DNS, until a reboot or a controller relaunch (`TinyWallController.cs:1362-1369`; opening the tray menu only shows a balloon, `:739-749`). The diagnostic journal records the exit as a normal shutdown. TinyWall kept its whole policy in persistent filters, so a TinyWall crash left networking working. README.md:21-23 documents the offline state as intended fail-closed behavior; the missing retry or restart is the defect.
- **Startup failures differ:** an `InitFirewall` failure leaves `Run` and is rethrown on a foreground thread with no handler (`TinyWallService.cs:2679-2683`). The process crashes, SCM restarts it once after 1 s, and a second failure hits `SC_ACTION_NONE` (`ServiceControlManager.cs:128-134`). The end state for a persistent cause is the same: offline. SW-04 adds a startup trigger.
- **Fix:** for environmental reloads, do not fail closed when the WFP transaction aborted, because the abort keeps the previously committed policy (`pylorak.Windows.WFP/Transaction.cs:113-117`); log and retry with backoff. Revoke only when the new policy is narrower and cannot be installed, or the engine is dead. When revocation is required, rebuild the dynamic session instead of exiting. If the process must exit, report a nonzero exit code, enable failure actions on non-crash failures, and configure several restart actions with an explicit reset period (today `dwResetPeriod = 0`, `ServiceControlManager.cs:117-140`; how SCM treats 0 is unverified). Emit `service_failure`, and have the controller offer a one-click start when the service is down.
- **Status:** Fixed in `claude/fix-h1` (`0840e4b`; Codex follow-up `1e20bcd`), included in `claude/pre-switch-fixes`. A failed environmental reload keeps the committed policy when nothing it relies on changed and retries (at most three retained failures), otherwise revokes and rebuilds the WFP session in process with retries; exhausted recovery or a startup failure now reports a failure exit code, and SCM restarts the service after 5, 30 and 60 s with a one-day reset and non-crash failure actions on. Not done: a controller one-click start. Needs live test.

#### SW-02. A machine-data guard rejection leaves the deny baseline with no uninstall or break-glass path

- **Source:** R4 H1 (trigger list corrected by the R4 audit), R4 audit A1 (UAC-off trigger). Different path from SW-01.
- **Confidence:** the chain is confirmed by code. Trigger confidence is marked per trigger.
- **Evidence:** `TinyWall/Program.cs:289-311` (every Release mode except `/install` calls `MachineDataGuard.Require()` and returns -1), `TinyWall/TinyWallDoctor.cs:121-122, 203, 292` (repeated before WFP removal at `:371-383`), baseline `TinyWall/TinyWallService.cs:491-510`, `TinyWall/Prompting/MachineDataPolicy.cs:22-33` (untrusted write, delete, DACL or owner rejected), `:41` ("Repair using the MSI installer") versus `MsiSetup/Product.wxs:43-46` (repair refused). `docs/SECURITY.md:85, 101` forbid improvised WFP deletion and state that no reviewed recovery procedure exists; `TinyWall/Utils.cs:719-721` tells the user not to fix the ACL by hand.
- **What goes wrong:** once SecureWall has installed its baseline, an ACL or owner rejection inside `%ProgramData%\SecureWall` (or on `C:\ProgramData` or `C:\`) makes the service exit at every start, and makes `msiexec /x`, `SecureWall.exe /uninstall` and the MSI rollback `/install` all return -1 before WFP removal. The persistent, boot-time baseline keeps blocking all external traffic, so the PC cannot download a fix and rebooting does not help.
- **Confirmed trigger:** any file or folder under `%ProgramData%\SecureWall` that grants a non-SYSTEM, non-Administrators, non-TrustedInstaller SID write, delete or DACL rights, or has an untrusted owner. Examples: moving a file into the folder on the same volume (it keeps its original ACL; `Program.cs:313` reads an `enable-timings` toggle file from there), or `icacls /reset /T` on ProgramData.
- **Plausible trigger, needs a UAC-off VM run (R4 audit A1):** with UAC off (EnableLUA=0), or the built-in Administrator account, a non-elevated controller runs with a full admin token. Controller exceptions then write `gui.log` into `%ProgramData%\SecureWall\logs` (`TinyWall/Prompting/LogDestinationPolicy.cs:8-10`, `Utils.cs:662-689`) with the user SID as owner, which `MachineDataPolicy.cs:24-25` rejects at the next service start.
- **Corrected triggers (not a strand):** installing TinyWall while SecureWall is installed stops SecureWall's service (SW-04), but `msiexec /x` and `/uninstall` never call `RequireNoTinyWall` and still reach WFP removal. An ACL change on `C:\`, `C:\Program Files` or the install tree blocks `/msi-cleanup`, but elevated `SecureWall.exe /uninstall` still removes the WFP objects and service. A deleted `%ProgramData%\SecureWall` probably self-heals: the MSI rollback runs `/install`, which recreates the folder as SYSTEM, and a second uninstall succeeds; `config` and `pwd` are lost (plausible, not VM-tested).
- **Fix:** ship a SYSTEM-only break-glass mode that skips the machine-data guard and deletes only the filters, sublayers and provider under `{053FC8F9-9052-4B2F-9B24-7DE3A2BED6E0}`, then disables the service; document it for local-console use. Or let `/msi-cleanup` continue to WFP and service removal when the only failure is the machine-data guard, skipping hosts restore and logging the skip. Set an explicit trusted owner and the protected DACL on every file created under machine data, or route non-SYSTEM logs to the user profile. Add VM rows for a moved-in file, UAC off, and `icacls` changes.
- **Status:** Fixed in `claude/fix-h2` (`984fe7c`), included in `claude/pre-switch-fixes`, for MSI removal. With the tree rejected, `/msi-cleanup` and `/msi-rollback-install` run a SYSTEM-only emergency release that reads no file contents from the rejected tree, leaves the hosts file as it is, restores compatibility and audit state from their HKLM journals, and removes only SecureWall's WFP objects, service and task. Since `claude/fix-y2` (`ad08cf2`, Codex follow-up `ba0dfd8`, merged in `cb54b5c`), each failed release step writes one Application event 1001 naming the mode, the step and the exception, and a failed release adds one 1001 summary with the exit code (integrated-branch Codex finding, section 7). Interactive `/uninstall` still refuses and points to Settings > Apps. Not addressed: the triggers (an explicit trusted owner on files created under machine data, UAC-off log routing). Needs live test.

#### SW-03. Learning mode grants svchost.exe unrestricted TCP/UDP for every hosted service, and every learned app gets inbound listeners

- **Source:** R2 H2 = R3 B7. The R2 audit keeps HIGH, conditional on using Learning mode; R3 rated it MEDIUM. Inherited.
- **Confidence:** confirmed by code.
- **Evidence:** `TinyWall/TinyWallService.cs:2505-2509` compares `entry.AppPath` with the bare string `"svchost.exe"`, but learned paths are always full Win32 paths (`TinyWall/FirewallLogWatcher.cs:266, 276-280`), so the exclusion never matches. `TinyWall/DatabaseClasses/AppDatabase.cs:84-85, 114-118` skips the `TWUI:Special` entries (the only ones naming svchost) and falls back to `new TcpUdpPolicy(true)`, which sets outbound TCP/UDP and TCP/UDP listen ports to `*` (`TinyWall/ExceptionPolicy.cs:145-153`). The WFP condition is only the svchost AppId (`TinyWallService.cs:663-669`).
- **What goes wrong:** a Learning session in which any svchost-hosted service connects leaves a permanent rule letting every svchost-hosted service accept inbound connections and connect out on any port. Every other learned executable is also opened for inbound connections on all ports. While Learning runs, the machine is fully open anyway (`TinyWallService.cs:156-161`). Rebuilding the allowlist with Learning after the switch is the natural move and the dangerous one.
- **Fix:** compare `Path.GetFileName(entry.AppPath)` or the full System32 and SysWOW64 paths; exclude registered service executables via `ServiceExecutableCatalog`, or learn a `ServiceSubject`; learn outbound-only policies shaped like `PromptAllowPolicy`.
- **Status:** Superseded by the removal of Learning mode (owner decision) in `claude/fix-nl` (`4ff3bac`, merged in `2842c3e`). The service refuses a switch to Learning and keeps the current mode; a stored or imported configuration that names Learning loads as Normal; the enum value 4 stays reserved so old files still parse; 5157 prompt attribution is kept. The earlier Learning fix in `claude/fix-h3` (`b05466d`, which learned svchost only as one exactly attributed service and ignored loopback records) was merged first and is now moot, because its code was removed with Learning. The H3 Codex follow-up `acaf454` (subjects merge only when equal in both directions) is not Learning code and stays (SW-29). Not done: detecting broad rules learned by TinyWall or by SecureWall up to v0.3.0, including executable-wide svchost rules (SECURITY.md says to review and delete them by hand). Needs live test (the "Learning mode removed" VM row).

### MEDIUM

#### SW-04. Post-ready health guards take the service down at every start

- **Source:** R2 audit A1. Related to SW-01 (startup crash path) and SW-10 (rollback order).
- **Confidence:** confirmed by code.
- **Evidence:** in Release, `Run` calls `TinyWallDoctor.EnsureHealth` after `service_ready` (`TinyWall/TinyWallService.cs:2145-2150`). `EnsureHealth` starts with `InstallationSafety.RequireNoTinyWall()` and `RequireProtectedInstallation()` outside any try (`TinyWall/TinyWallDoctor.cs:410-413`); the second walks every ancestor and every file of the install tree (`TinyWall/Installer/InstallationSafety.cs:43-56, 193-235`).
- **What goes wrong:** a service named `TinyWall` appearing (for example reinstalling TinyWall to roll back before removing SecureWall), an ownership or ACL change on `C:\`, `C:\Program Files` or the install tree, or a transient ACL read failure makes the service crash at every boot. SCM restarts it once, then stops. The PC stays behind the deny-all baseline. Upstream `EnsureHealth` had no such guards.
- **Fix:** in the service context, log guard failures and warn in the controller; do not throw after policy has committed. Keep hard refusals in install and repair paths. Document "uninstall SecureWall before reinstalling TinyWall".
- **Status:** Fixed in `claude/fix-mf` (`8a1ef1a`), included in `claude/pre-switch-fixes`: post-ready guard failures are logged and raise a controller warning instead of withdrawing policy and crashing. Correction to the finding: a `TinyWall` service that is already present still stops SecureWall at start, before policy loads (the documented pre-start check), so never install TinyWall while SecureWall is installed. Needs live test.

#### SW-05. Hosts-file locking is on by default and now blocks startup

- **Source:** R2 M1.
- **Confidence:** confirmed by code; the named external triggers are plausible.
- **Evidence:** `TinyWall/ServerConfiguration.cs:152` (`LockHostsFile = true`), `TinyWall/HostsFileManager.cs:67-84, 212-219` (`RequireLock` throws on a missing file or a failed `FileShare.Read` lock), `TinyWall/TinyWallService.cs:1528-1534` (`ReapplySettings` runs before `InstallFirewallRules`), `:1244-1248, 1521` (at first init, rollback repeats the same failing setting), `TinyWall/Prompting/EnforcementPolicy.cs:227-231` (fails closed). Upstream skipped a missing file and ignored lock failures.
- **What goes wrong:** if `hosts` is missing, or another program holds it open for writing when the service starts (a hosts editor, a privacy tool, possibly Docker Desktop or an AV scan), SecureWall never installs runtime policy and the PC stays offline (SW-01).
- **Fix:** treat hosts protection as best-effort: log and warn, never gate WFP enforcement on it. Run `ReapplySettings` after the WFP commit or catch its failure separately.
- **Status:** Fixed in `claude/fix-mf` (`8a1ef1a`), included in `claude/pre-switch-fixes`: hosts-file locking is best-effort. A missing hosts file is tolerated, a lock failure is logged and shown as a health warning, and enforcement installs regardless. The hosts blocklist content path stays strict (off by default). Needs live test.

#### SW-06. SecureWall reuses TinyWall's WFP sublayer GUIDs and never checks for TinyWall's WFP objects

- **Source:** R2 M2 = R4 L1; R5 audit A1 (switch checklist gap). The R2 audit rates it MEDIUM for leftover TinyWall filters; the R4 audit rates the install-time collision LOW because the install rolls back cleanly. Merged at MEDIUM.
- **Confidence:** key reuse confirmed (`git diff 1df71b1 HEAD -- TinyWall/WfpSublayerKeys.cs` is empty). The failure outcome is plausible and depends on TinyWall leftovers.
- **Evidence:** only the provider key changed (`TinyWall/TinyWallService.cs:44`; TinyWall's is `{66CA412C-4453-4F1E-A973-C16E433E34D0}`). `DeleteWfpObjects` removes only SecureWall-provider filters and swallows sublayer-delete errors (`:2012-2034`), then `RegisterSublayer` runs with the same keys (`:479-489`). `TinyWall/Installer/InstallationSafety.cs:18-31` and the MSI search (`MsiSetup/Product.wxs:86-98`) check only for a `TinyWall` service or registry value, never for TinyWall's WFP provider.
- **What goes wrong:** if a TinyWall uninstall leaves persistent or boot-time sublayers or filters behind, SecureWall's baseline registration fails (the install rolls back) or TinyWall's old filters, including its old permits, keep enforcing beside SecureWall. The reverse applies when rolling back to TinyWall with SecureWall's baseline still present.
- **Fix:** give SecureWall its own sublayer GUIDs (no migration needed; in-place upgrades are refused). At install and service start, detect TinyWall's provider or foreign filters in SecureWall sublayers and refuse with a message naming the cleanup step. Until then, the checklist confirms TinyWall's WFP objects are gone before installing SecureWall.
- **Status:** Fixed in `claude/fix-mg` (`587d917`), included in `claude/pre-switch-fixes`: SecureWall has its own 14 sublayer GUIDs. Install and service start refuse, naming the objects and the cleanup step, when TinyWall's provider, a foreign owner of a SecureWall sublayer, or a foreign filter in a SecureWall or TinyWall-GUID sublayer is present. Uninstall refuses while a foreign filter sits in a SecureWall-owned sublayer. Needs live test.

#### SW-07. A failed first install whose rollback cleanup also fails can leave the baseline, a disabled service and no binary

- **Source:** R4 M1.
- **Confidence:** plausible. The code path is confirmed; how Windows Installer treats a failing rollback custom action is not VM-tested.
- **Evidence:** `MsiSetup/Product.wxs:141-142, 160-161` (rollback runs `/msi-rollback-install` before file rollback), `TinyWall/TinyWallDoctor.cs:208-216` (service disabled first), `:310-314` and `:327-341` (return -1 on hosts, Windows Firewall or audit restore failure, before WFP removal at `:371-383`), `Product.wxs:62-64` (fresh install refused while the service key exists).
- **What goes wrong:** `/install` fails after the baseline is registered, restoration then fails (for example MpsSvc unavailable). The baseline blocks all external traffic, the service points at a deleted exe, there is no Apps entry, and the next MSI refuses to install.
- **Fix:** on the failed-first-install path only, remove WFP objects even when restoration fails, keep the journals and log loudly. Add a VM row that forces `/install` to fail after `baseline_register` with MpsSvc stopped.
- **Status:** Fixed in `claude/fix-h2` (`984fe7c`), included in `claude/pre-switch-fixes`: failed first-install rollback continues past hosts and audit failures, always removes the service registration and task, and removes the baseline only once SecureWall's allow-all compatibility rules are confirmed gone. The baseline provider no longer names the service, so a Disabled service no longer releases it at boot (Codex red 1, section 7). Needs live test.

#### SW-08. Interactive `SecureWall.exe /uninstall` always fails while SecureWall is password-locked

- **Source:** R5 V11 = R1 F6 (upstream `c4cd972` missing). R1 rated it LOW; the R5 audit keeps MEDIUM because it breaks a documented recovery step.
- **Confidence:** confirmed by code.
- **Evidence:** `TinyWall/Program.cs:413-414` goes to `UninstallService()`; `TinyWall/TinyWallDoctor.cs:156-158` builds `PasswordForm` when locked; `TinyWall/PasswordForm.cs:16` calls `Utils.IsDarkModeActive(ActiveConfig.Controller)`, which is null outside the controller (`TinyWall/Settings.cs:248`; set only at `TinyWallController.cs:334, 717, 949`); `TinyWall/Utils.cs:243-244` dereferences it; the outer catch (`TinyWallDoctor.cs:181-185`) returns -1.
- **What goes wrong:** the owner sets a password as `docs/SECURITY.md:58` recommends, and recovery step 2 (`docs/SECURITY.md:83`, run `SecureWall.exe /uninstall` elevated) fails without ever asking for the password. MSI removal is unaffected (`/msi-cleanup`, `Product.wxs:143`, no dialog).
- **Fix:** port `c4cd972` or null-check `ActiveConfig.Controller`; add a test that `PasswordForm` constructs with it null. Until then, uninstall through Apps or `msiexec /x`.
- **Status:** Fixed by upstream port `c4cd972` (adapted, null-safe theme check; ports commit `8544481`).

#### SW-09. Installer failure reasons never reach the owner, and logs truncate themselves

- **Source:** R4 M5.
- **Confidence:** truncation confirmed; stderr loss plausible (documented Windows Installer behavior for EXE custom actions, not tested here).
- **Evidence:** on a guard failure, `TinyWall/Program.cs:300-310` writes only to `Console.Error`; the custom actions are EXE actions (`MsiSetup/Product.wxs:141-146`); `docs/SECURITY.md:99` concedes a generic error 1722. In `/service` mode SCM discards stderr too. `TinyWall/Utils.cs:678-686` truncates `service.log` and `installer.log` to zero past 512 KiB.
- **What goes wrong:** in exactly the SW-02 and SW-07 situations, the owner sees "error 1722" and an empty or truncated log; a restart loop overwrites the first, most useful exception.
- **Fix:** write a one-line reason to the Windows Application event log on guard failure (needs no machine-data path); rotate logs instead of truncating.
- **Status:** Fixed in `claude/fix-md` (`d3edca5`), included in `claude/pre-switch-fixes`: guard and maintenance failures write event 1000 or 1001 to the Application log, and logs rotate instead of truncating. The MSI was not built here (no WiX). Needs live test.

#### SW-10. Rolling back to TinyWall loses the TinyWall rules unless exported first, and the order of steps matters

- **Source:** R4 M6 (with audit caveat).
- **Confidence:** confirmed from the TinyWall 3.5.1 WiX source. The owner's 3.3.1 installer may differ, so back up regardless.
- **Evidence:** upstream `MsiSetup/Product.wxs:299-303` at `1df71b1` (`RemoveAppDataDir`, `RemoveFile Name='*' On='install'`) wipes `%ProgramData%\TinyWall` on a fresh TinyWall install. SecureWall imports only `.tws` exports (`TinyWall/SettingsForm.cs:551-569`). `TinyWall/Program.cs:36` refuses to start SecureWall while a `TinyWall` service exists (SW-04).
- **What goes wrong:** uninstall TinyWall, try SecureWall, reinstall TinyWall: TinyWall comes back with default rules and no password. Installing TinyWall before SecureWall is removed leaves SecureWall's baseline enforcing under TinyWall.
- **Fix (procedure and docs):** export a `.tws` and copy `%ProgramData%\TinyWall` before uninstalling TinyWall. To roll back: uninstall SecureWall via the MSI, reboot, confirm the SecureWall service and `{053FC8F9-...}` provider are gone, then install TinyWall and import the `.tws`. Add this to `docs/LIVE-TESTING.md`.
- **Status:** Fixed in `claude/fix-md` (`d3edca5`), included in `claude/pre-switch-fixes`, docs only: `docs/LIVE-TESTING.md` now says to export a `.tws` and copy `%ProgramData%\TinyWall` before removing TinyWall, and has a "Rolling back to TinyWall" section; README links it.

#### SW-11. A prompt Allow merges into an existing LAN-only rule and widens it to inbound internet access

- **Source:** R3 B1.
- **Confidence:** confirmed by code; no test covers the merge path.
- **Evidence:** `TinyWall/TinyWallService.cs:1499-1503` calls `AddExceptions`; `TinyWall/ServerConfiguration.cs:69-98` merges an equal-subject permanent exception into the new one and removes the old; `TinyWall/ExceptionPolicy.cs:119-124` (old LAN-only Unrestricted into new TCP/UDP: `LocalNetworkOnly` becomes false, result is Unrestricted) and `:196-204` (old listener ports survive, `LocalNetworkOnly` cleared); `TinyWallService.cs:1055-1062, 1073-1100` (Unrestricted becomes InOut, any protocol; listeners lose `LOCALSUBNET`).
- **What goes wrong:** app X is set to "Restrict to local network". It tries an internet destination, prompts, the owner clicks Allow. X is now reachable inbound from the internet on its listener ports, or on every port and protocol if it was Unrestricted. The popup and `docs/SECURITY.md:13` promise outbound TCP/UDP only.
- **Fix:** do not route prompt Allow through `AddExceptions` merging. Add a separate outbound-only exception, or add only outbound ports to an existing one without changing `LocalNetworkOnly` or the policy type. Add a test.
- **Status:** Fixed in `claude/fix-mc` (`fc99f9e`), included in `claude/pre-switch-fixes`: a merge that would be wider than both inputs keeps the two exceptions separate, so a LAN-only rule stays LAN-only and the prompt adds a separate outbound TCP/UDP rule. Covers manual adds and `Normalize` too. Needs live test.

#### SW-12. Any same-user process can press Allow on its own prompt when no password is set

- **Source:** R3 B4.
- **Confidence:** code facts confirmed; the click technique is standard Win32 behavior, not tested here.
- **Evidence:** the controller runs `asInvoker` (`TinyWall/app.manifest:19`); `TinyWall/Prompting/BlockedConnectionPopup.cs:114-118` has no input-origin check; `ALLOW_PROMPT = 3072` (`TinyWall/MessageType.cs:37`) is gated only by `PasswordLock.Locked` (`TinyWallService.cs:2534`), always false without a password (`TinyWall/Settings.cs:161-169`). `docs/SECURITY.md:58` already documents same-user IPC access.
- **What goes wrong:** malware running as the owner connects out, gets blocked, finds the popup and sends it a click (`BM_CLICK`, `SendInput` or UI Automation). It gets a permanent allow to every destination and port, which is the main thing an outbound firewall exists to stop. A password closes this only while locked, and SW-13 weakens the lock.
- **Fix:** reject Allow clicks whose `GetCurrentInputMessageSource` is not hardware input; consider running the prompt UI from an elevated controller; require a password at first run or make "no password" an explicit, explained choice.
- **Status:** Partly fixed in `claude/fix-mb` (`5f00b36`), included in `claude/pre-switch-fixes`: Allow arms only after the popup has been visible and unmoved for 1 s and the risk probe has finished, and a click on a disarmed Allow does nothing. What remains: a same-user process can wait out the delay and click while SecureWall is unlocked or has no password; the hardware-input check was not added because `SendInput` can spoof it at the same integrity level. The control is a password with SecureWall kept locked (documented in SECURITY.md).

#### SW-13. Popup timeouts, and any `DISMISS_PROMPT` with a random token, keep the password lock open indefinitely

- **Source:** R3 B2 (random-token path added by the R3 audit).
- **Confidence:** confirmed by code.
- **Evidence:** popups auto-close after 30 s (`TinyWall/Prompting/PromptDisplayDeadline.cs:13`) and send `Dismiss` (`TinyWall/Prompting/PromptDisplayCoordinator.cs:193-216`). `TinyWall/TinyWallService.cs:2555-2559` counts any successful `DISMISS_PROMPT` as user activity; `:1717-1722` and `TinyWall/Message.cs:315-316` return a normal response for an unknown or expired token. Relock after 10 minutes without activity (`TinyWallService.cs:54, 1906`).
- **What goes wrong:** after the owner unlocks and walks away, any app retrying a blocked connection prompts about every 5.5 minutes, and each timeout resets the 10-minute window. Any same-user process can also send `DISMISS_PROMPT` with a random GUID to keep it open, with no blocked traffic needed. Contradicts `docs/HARDENING-VALIDATION.md:18` and `docs/SECURITY.md:30`.
- **Fix:** send a distinct request for automatic timeouts and exclude it from user activity; count only dismissals of a valid, pending token from an explicit click. Test at the `PipeServerDataReceived` classification level.
- **Status:** Fixed in `claude/fix-mc` (`fc99f9e`), included in `claude/pre-switch-fixes`: popup timeouts send a separate `DISMISS_PROMPT_TIMEOUT` request, and only a dismissal of a valid pending token or a successful Allow counts as activity. Residual (documented): a same-user process can still extend the window by dismissing a real pending token. Needs live test.

#### SW-14. Enter maps to Allow, the popup is topmost at a predictable spot, and Allow is armed before risk warnings load

- **Source:** R3 B5 (async risk-warning detail added by the R3 audit).
- **Confidence:** code facts confirmed. Whether the popup takes focus when shown is unverified and needs a live check.
- **Evidence:** `TinyWall/Prompting/BlockedConnectionPopup.Designer.cs:141` (`AcceptButton = allowButton`), `:165` (`TopMost = true`); `BlockedConnectionPopup.cs:26` (`ShowWithoutActivation`), `:44` (Allow enabled at show), `:83-90` (fixed bottom-right position); risk warnings (unsigned, user-writable, recently changed) load after display (`BlockedConnectionPopup.Risk.cs:44-68`).
- **What goes wrong:** if the popup activates while the owner types, one Enter grants a permanent allow. A process can time a blocked connection so Allow appears under a click already in progress. Allow can be clicked before the risk warnings appear.
- **Fix:** make Ignore the default button or set none; disable Allow for about 1 s after the popup becomes visible or moves; apply `WS_EX_NOACTIVATE | WS_EX_TOPMOST` through `CreateParams`; add a live-matrix row for "popup appears while typing".
- **Status:** Fixed in `claude/fix-mb` (`5f00b36`; doc corrections `ec4dc8f`), included in `claude/pre-switch-fixes`: the popup has no default button, so Enter does not allow unless the owner moved focus to an armed Allow, and Allow arms only after the risk warnings load. `WS_EX_NOACTIVATE` was not added. Whether the popup takes focus needs live test.

#### SW-15. With a password set, clicking Allow while locked usually ends in a dismissal and a 5-minute suppression

- **Source:** R3 B6.
- **Confidence:** confirmed by code.
- **Evidence:** `TinyWall/Prompting/BlockedConnectionPopup.cs:59-70` (Locked shows "Unlock it from the tray", deadline unchanged); no unlock path from the popup (`ControllerPromptActionClient.cs:14`); unlocking is a modal `PasswordForm` from the tray (`TinyWallController.cs:1125-1154`) while the popup timer keeps ticking; timeout sends `DISMISS_PROMPT` and sets a 5-minute cooldown (`PromptQueue.cs:261`).
- **What goes wrong:** the recommended setup (password set, service locked) makes prompts nearly unusable, which pushes the owner to remove the password and reopens SW-12.
- **Fix:** on Locked, pause the deadline and offer an inline Unlock that retries Allow with the same token; skip the timeout dismissal and cooldown for a prompt whose last result was Locked.
- **Status:** Fixed in `claude/fix-mb` (`5f00b36`; always relock `ec4dc8f`), included in `claude/pre-switch-fixes`: Allow while locked opens the unlock dialog, holds the popup until token expiry, retries the same token, then relocks. Remaining limit: the popup unlock is global from UNLOCK until the relock (Codex finding on this branch, MEDIUM, docs corrected, code not changed). The owner decided not to add a scoped unlock and plans no password, so the popup unlock flow does not arise on this PC; see "Owner decisions". Needs live test.

#### SW-16. A service Allow writes a rule that never matches when the service's SID type is NONE

- **Source:** R3 audit AF1.
- **Confidence:** code path confirmed. The Windows behavior (no per-service SID in the token when SidType is NONE) is documented, not live-tested.
- **Evidence:** a PID with exactly one SCM service becomes `PromptIdentity.ForService` (`TinyWall/Prompting/ServiceAttribution.cs:40-41`); Allow writes a `ServiceSubject` (`TinyWallService.cs:1478-1482`); the filter matches `FWPM_CONDITION_ALE_USER_ID` against the per-service SID (`TinyWallService.cs:654-668`, `pylorak.Windows.WFP/FilterCondition.cs:507-510`). No code reads a service's SID type.
- **What goes wrong:** on this PC, 20 own-process, non-svchost services have SidType NONE, including `edgeupdate`, `edgeupdatem`, the Google updater services, `ClickToRunSvc`, `battlenet_helpersvc`, `GoogleChromeElevationService`, `MicrosoftEdgeElevationService` and AMD services. These updaters are the services most likely to prompt. The owner clicks Allow, the popup closes as a success, the traffic stays blocked, and the service prompts again on its next attempt.
- **Fix:** read `SERVICE_CONFIG_SERVICE_SID_INFO` during attribution. For SidType NONE own-process services, offer an executable-scoped allow and say so, or disable Allow with an explanation. Add a live-matrix row. Before the switch, run `sc.exe qsidtype <name>` on any service the owner expects to allow.
- **Status:** Fixed in `claude/fix-ma` (`37aaa87`; notice layout and quoting `884d27a`), included in `claude/pre-switch-fixes`: attribution reads the service SID type; for NONE or an unreadable type the popup shows the service with Allow disabled and gives the `sc.exe sidtype "<name>" unrestricted` command. Residual: the configured type is read, so a changed type needs a service restart. Needs live test.

#### SW-17. One unparseable service ImagePath disables Allow for every application, with no diagnostic

- **Source:** R3 B3.
- **Confidence:** confirmed by code. Does not trigger on this PC today: a read-only scan of 279 Win32 services found 0 unparseable entries.
- **Evidence:** `TinyWall/ServiceExecutableCatalog.cs:69-79` (any unparsed service sets `Complete = false`), `:39-40` (every lookup then fails); `TinyWallService.cs:2465-2472` passes `!catalogAvailable` as "registered service"; `ServiceAttribution.cs:45-46` returns a non-allowable identity; popup text at `BlockedConnectionPopup.cs:47, 100`.
- **What goes wrong:** a new service with an odd ImagePath (leftover keys from uninstalled software are common) makes every prompt show Allow disabled with "Shared Windows service host" wording, even for `firefox.exe`, and nothing logs why.
- **Fix:** log the offending service names; surface "service inventory incomplete" in `ServerState`; skip keys with no ImagePath; change the popup text for this case.
- **Status:** Fixed in `claude/fix-ma` (`37aaa87`), included in `claude/pre-switch-fixes`: an unresolved registration no longer disables Allow everywhere; only executables whose file name matches it lose Allow, the popup says why, and the service log names the registration. The image-path parser now returns "unresolved" instead of guessing at the first `.exe` (Codex yellow 1). Needs live test.

#### SW-18. The password hash file is readable by every local user, and its hash parameters are weak

- **Source:** R4 M4 = R1 F2 + R1 F3 (upstream `3146d95` and `b741831` missing). R1 rated F2 LOW on a single-user PC and MEDIUM with other local users, and F3 LOW; R4 rated it MEDIUM because same-user malware is the threat the password exists to stop, and the R4 audit verified it at MEDIUM.
- **Confidence:** confirmed by code.
- **Evidence:** `TinyWall/Installer/MachineDataGuard.cs:57` grants BUILTIN\Users `0x1200a9` (read) inherited to all files, including `pwd` (`TinyWall/Settings.cs:157`); the service locks it with `FileShare.Read` (`TinyWallService.cs:1882, 2065`). Format: 8-character salt, PBKDF2-HMAC-SHA1, 150,000 iterations, 16 bytes (`Settings.cs:181-185`), over an unsalted SHA-256 of the passphrase (`PasswordForm.cs:24`). `pylorak.Utilities/Pbkdf2.cs:22-33` parses iterations without bounds and compares with `OrdinalIgnoreCase` string equality, not constant time.
- **What goes wrong:** any local process, including the same-user malware the password is meant to stop, can copy `pwd` and guess weak passphrases offline on a GPU, then unlock the pipe and grant itself access. The data-dir ACL stops non-admins from planting a malicious `pwd`, so the unbounded parse is not reachable.
- **Fix:** give `pwd` (and its temp file) a protected SYSTEM and Administrators DACL and lock it with `FileShare.None`; `MachineDataPolicy` accepts a tighter ACL. Port `b741831` (16-byte random salt, SHA-256, 200,000 iterations, bounded parse, upgrade on next unlock) and use a constant-time byte comparison.
- **Status:** Fixed by upstream ports `3146d95` and `b741831` (adapted; ports commit `8544481`): `pwd` gets a protected DACL, is held with `FileShare.None`, and new hashes use a 16-byte random salt with PBKDF2-HMAC-SHA256 at 200,000 iterations; a legacy hash is rewritten after the next successful unlock.

#### SW-19. The AI helper cannot reach its endpoint, and the only workaround gives the LocalSystem service outbound access

- **Source:** R4 M3 = R5 V6. The reports disagreed on whether the self-prompt can be allowed; code at HEAD supports R5 (Allow is disabled).
- **Confidence:** code path confirmed; the live block was not run. Narrow impact: the helper is off by default (`TinyWall/Settings.cs:87`).
- **Evidence:** upstream seeded a TCP 443 allow for its own binary (`git show 1df71b1:TinyWall/TinyWallService.cs:152-161`); HEAD starts with an empty list (`TinyWallService.cs:182-183`). The AI call runs in the controller, `SecureWall.exe` (`BlockedConnectionPopup.AiExplain.cs:114-119`, `OpenAiCompatibleExplainClient.cs:47-53`). `SecureWall.exe` is a registered service image and the controller PID hosts no service, so `ServiceAttribution.cs:45-46` returns `ForUnattributedServiceHost` and the prompt's Allow is disabled (`TinyWallService.cs:2464-2472`). The service runs the same image (`MsiSetup/Product.wxs:198`, `TinyWallDoctor.cs:81`).
- **What goes wrong:** with AI enabled, "?" fails with "Could not reach the AI service" and a second, un-allowable popup appears for SecureWall itself. The only route is a manual path-based exception for `SecureWall.exe`, which also covers the LocalSystem service. The requirement is undocumented.
- **Fix:** a controller-only rule scoped by user SID (`FWPM_CONDITION_ALE_USER_ID`), TCP 443 only, installed only while AI is enabled; or hide "?" in Normal mode and document it; warn when a manual exception targets SecureWall's own image.
- **Status:** Fixed in `claude/fix-me` (`ea95440`; follow-up `d4ab8f5`), included in `claude/pre-switch-fixes`: while the assistant is enabled, the service installs a TCP 443 permit for `SecureWall.exe` that denies SYSTEM, LOCAL SERVICE and NETWORK SERVICE tokens and allows interactive ones; endpoints on other ports are rejected; a manual exception for SecureWall's own image asks for confirmation. Needs live test (that the LocalSystem service is denied).

#### SW-20. The app database and hosts blocklist are never refreshed on a machine that already has them

- **Source:** R4 M2.
- **Confidence:** confirmed by code. Does not affect the first install on this PC (no prior tree); affects every later reinstall.
- **Evidence:** `TinyWall/Installer/MachineDataGuard.cs:96-103` copies `profiles.json` and `hosts.bck` only when absent; the MSI has no ProgramData components; the update feed is compiled off (`TinyWall/SecureWallProduct.cs:9`, `TinyWallService.cs:1353`); the database is read from ProgramData (`TinyWall/DatabaseClasses/AppDatabase.cs:17-19`).
- **What goes wrong:** after uninstalling and installing a newer build, the old database stays in force, so database security fixes (such as the removed svchost-wide Windows Update grant advertised at `README.md:25`) never arrive. `hosts.bck` (dated 29 May 2026) never updates.
- **Fix:** during SYSTEM `/install`, replace `profiles.json` and `hosts.bck` atomically from `data-defaults` when their hash differs, leaving `config`, `pwd` and journals alone; or read the database from the protected install directory.
- **Status:** Fixed in `claude/fix-md` (`d3edca5`), included in `claude/pre-switch-fixes`: SYSTEM `/install` replaces `profiles.json` and `hosts.bck` when they differ from the shipped copies and leaves `config`, `pwd` and journals alone. Needs live test.

#### SW-21. The tray icon can vanish after Explorer starts late or restarts

- **Source:** R1 F1 (upstream `7270d96` missing).
- **Confidence:** missing code confirmed; symptom plausible.
- **Evidence:** the logon task runs the controller elevated (`TinyWall/TinyWallDoctor.cs:460`, `TASK_RUNLEVEL_HIGHEST`); no `ChangeWindowMessageFilter` or `TaskbarCreated` exists anywhere (controller startup `TinyWall/TinyWallController.cs:1318-1321`).
- **What goes wrong:** Explorer broadcasts `TaskbarCreated` at medium integrity and UIPI drops it for the high-integrity controller. If Explorer starts after the controller, or restarts, the tray icon never comes back. The tray is the entry to settings, mode switching and unlock. Blocked-connection popups still appear.
- **Fix:** port `7270d96` (`RegisterWindowMessage("TaskbarCreated")` plus `ChangeWindowMessageFilter(msg, MSGFLT_ADD)` before creating the tray icon).
- **Status:** Fixed by upstream port `7270d96` (ports commit `1ef4f5e`).

#### SW-22. WSL2 filters: lost error isolation, a hard-coded adapter alias, and duplicate filters

- **Source:** R2 M3.
- **Confidence:** error path and duplicates confirmed; the alias mismatch on current WSL builds is plausible. No WSL is installed on this PC today.
- **Evidence:** `TinyWall/TinyWallService.cs:849-863` dropped upstream's `try { } catch { }` around `InstallWsl2Filters`; `pylorak.Windows.WFP/FilterCondition.cs:735-755` throws if the alias check fails or the alias vanishes; `TinyWallService.cs:351-355` runs on every `InstallRules`, including each child-inheritance `ADD_TEMPORARY_EXCEPTION` (`TinyWallService.cs:1803`); `TinyWallService.cs:851` hard-codes `"vEthernet (WSL)"`.
- **What goes wrong:** WSL start or stop during a reload aborts the whole policy transaction and triggers SW-01. Each child process start adds 8 more WSL filters until the next full reload. On WSL builds with a different adapter name, or in mirrored mode, the `WSL_2` special exception silently does nothing.
- **Fix:** catch and log WSL filter errors without aborting the transaction; install WSL filters only in the full reload; find the adapter by a stable property and report when WSL is present but not matched.
- **Status:** Fixed in `claude/fix-mf` (`8a1ef1a`), included in `claude/pre-switch-fixes`: WSL filters install once per full reload, not on each child-inheritance add; adapters are found by one interface enumeration matching `vEthernet (WSL)` and `vEthernet (WSL (...))` and bound by LUID; permit failures are isolated per adapter and logged. Block-mode failures still abort the transaction (fail closed). Needs live test (no WSL on this PC).

### LOW

#### SW-23. Every display on/off rebuilds the whole policy, even with DisplayOffBlock off

- **Source:** R2 L1. **Confidence:** confirmed.
- **Evidence:** `TinyWall/TinyWallService.cs:1939-1951` reloads on any display change; rules use it only with `DisplayOffBlock` (`:295-306`, default false at `ServerConfiguration.cs:48`); each reload revokes pending prompt tokens (`TinyWallService.cs:420-425`, `TinyWallService.cs:2369-2372`).
- **What goes wrong:** each monitor sleep and wake cancels pending prompts and is a chance to hit SW-01, with no policy change.
- **Fix:** update the display state always; reload only when `DisplayOffBlock` is on.
- **Status:** Held (LOW).

#### SW-24. DisplayOffBlock widens address-restricted allow rules to the whole local subnet

- **Source:** R2 L2. **Confidence:** confirmed; inherited; opt-in, default off.
- **Evidence:** `TinyWall/TinyWallService.cs:296-305` overwrites `RemoteAddresses` with `LOCALSUBNET_ID` on every allow rule.
- **What goes wrong:** with the display off, an app allowed only to one address, or DNS rules limited to the configured servers, may reach every local-subnet host.
- **Fix:** keep only rules whose addresses are already local, or intersect the sets; never replace a narrower list with a broader one.
- **Status:** Held (LOW).

#### SW-25. An exception while saving learned rules at stop skips cleanup and crashes an orderly stop

- **Source:** R2 L3 (mitigation added by the R2 audit). **Confidence:** plausible; needs a config write failure at stop.
- **Evidence:** `TinyWall/TinyWallService.cs:2612-2613` saves learned rules before `LogWatcher.Dispose`, `HostsFileManager.Dispose` and `FileLocker.UnlockAll` (`:2618-2622`), unguarded; after a normal stop the rethrow at `:2682` is unhandled.
- **What goes wrong:** audit-policy restoration and hosts unlock are skipped and the process crashes during `sc stop`. Audit changes are journaled and restored at the next lease (`AuditPolicyLease.cs:95-98, 146-153`), so the skipped restore is temporary.
- **Fix:** wrap the save and move it after cleanup, or commit learned rules through `ApplyConfiguration` when leaving Learning.
- **Status:** Moot. `claude/fix-nl` (`4ff3bac`) removed Learning mode together with `CommitLearnedRules` and its save at shutdown.

#### SW-26. With Windows fast startup, "until reboot" rules survive shutdown and power-on

- **Source:** R2 L4. **Confidence:** plausible (Windows fast-startup semantics); inherited.
- **Evidence:** reboot detection uses a global atom (`TinyWall/TinyWallService.cs:1651-1665`); fast startup hibernates session 0, so the atom and service survive; `restarting: true` applies only at service init (`:1243`).
- **What goes wrong:** a grant made "until reboot" survives a shutdown and power-on.
- **Fix:** treat hibernate-class resume or a changed boot ID as a reboot boundary, or document it.
- **Status:** Held (LOW).

#### SW-27. Network Activity auto-refresh clears the selection every second, breaking context actions

- **Source:** R3 B8. **Confidence:** code confirmed; exact UI timing plausible.
- **Evidence:** `TinyWall/ConnectionsForm.cs:46-47` (1 s timer, new versus upstream), `:253-255` (clear and repopulate), `:456-465` (Unblock reads `SelectedItems` after the modal unlock), `:471` (no try block); no `Application.ThreadException` handler in `Program.cs`.
- **What goes wrong:** Unblock after an unlock prompt silently does nothing; "Copy remote address" can throw an unhandled UI exception. Each refresh also makes synchronous pipe calls on the UI thread.
- **Fix:** pause the timer while a menu or dialog is open; snapshot the selection when the menu opens; preserve selection by key; wrap line 471.
- **Status:** Held (LOW), together with Codex yellow 6 and 7 (section 7).

#### SW-28. One Allow wipes every other pending prompt, and a revoked token closes the popup as if Allow succeeded

- **Source:** R3 B9. **Confidence:** confirmed.
- **Evidence:** `TinyWall/TinyWallService.cs:420-422` and `:1546-1549` call `ResetPromptCandidates()`, which clears the queue (`PromptCandidateLifecycle.cs:15`); `PromptDisplayCoordinator.cs:179-184` treats `UnknownToken` or `Expired` like `Allowed`.
- **What goes wrong:** after a burst, allowing the first app drops the other queued prompts. If a reload lands just before the click, Allow closes the popup silently and the app stays blocked.
- **Fix:** show "This prompt was replaced; the connection is still blocked" on `UnknownToken` or `Expired`; optionally re-admit still-pending identities after an Allow.
- **Status:** Held (LOW).

#### SW-29. A service Allow can replace an existing executable-wide exception

- **Source:** R3 B10. **Confidence:** confirmed; narrow.
- **Evidence:** `TinyWall/ExceptionSubject.cs:283-291` (`ExecutableSubject.Equals` accepts a `ServiceSubject` with the same path); `ServerConfiguration.cs:82-95` merges and removes the old entry.
- **What goes wrong:** `foo.exe` has an exe-wide rule for port 443. Its service instance prompts and is allowed; the exe-wide rule moves under the service subject, and the non-service `foo.exe` loses port 443.
- **Fix:** compare subject types exactly before merging, or skip merging for prompt Allow (see SW-11).
- **Status:** Fixed as a side effect of `claude/fix-h3` (`acaf454`), included in `claude/pre-switch-fixes`: exceptions now merge only when their subjects are equal in both directions, so a service rule no longer absorbs an executable-wide rule for the same path. The integration added a regression test (`f87ad7a`, `ProfileMergeTests`) that runs the real `AddExceptions` and `Normalize` merge code in both orders; reverting either call site to a one-way comparison makes it fail. This fix is not Learning code and survives the Learning removal.

#### SW-30. The global pipe and service mutex can be squatted or flooded by any local user; pipe failures are silent

- **Source:** R3 B11. **Confidence:** mechanism confirmed; exploit plausible. Fails closed.
- **Evidence:** `TinyWall/PipeServerFactory.cs:22-25` (single instance), `PipeMessageTransport.cs:55-58` and `PipeServerEndpoint.cs:121-129` (recreate after 200 ms; errors discarded unlogged); `TinyWall/Program.cs:47-50` and `SecureWallProduct.cs:8` (`Global\SecureWallService` mutex check).
- **What goes wrong:** a squatter on `\\.\pipe\SecureWallController` after a service restart leaves a dead tray (no prompts, unlock or settings). A pre-created `Global\SecureWallService` mutex stops the service from starting, leaving the deny baseline in place.
- **Fix:** log pipe creation failures; restrict the pipe DACL to interactive users; rely on SCM single-instance semantics in service mode, or use a private namespace for the mutex.
- **Status:** Held (LOW).

#### SW-31. UNLOCK has no attempt limit and runs PBKDF2 on the single service worker thread

- **Source:** R3 B12. **Confidence:** confirmed; inherited.
- **Evidence:** `TinyWall/MessageType.cs:20` (`UNLOCK = 1024`, unprivileged), `TinyWallService.cs:1842-1850`, `Settings.cs:183, 189-202`.
- **What goes wrong:** a same-user process can guess passwords online at service speed, stalling every other request, including prompt polling.
- **Fix:** exponential backoff after failures, tracked in the service; reject UNLOCK during backoff.
- **Status:** Held (LOW).

#### SW-32. Each controller polls every 750 ms with process and SCM queries per exchange

- **Source:** R3 B13. **Confidence:** confirmed.
- **Evidence:** `TinyWall/TinyWallController.cs:356-359`, `:443-481`; each exchange opens a new pipe and authenticates the server (`PipeClientEndpoint.cs:68-73`, `PipeServerIdentity.cs:63-141`).
- **What goes wrong:** constant wakeups and SCM traffic even when nothing is blocked.
- **Fix:** authenticate once per controller session, or have the service signal a named event when the prompt queue changes.
- **Status:** Held (LOW).

#### SW-33. Allow failure reasons are not shown to the owner

- **Source:** R3 B14. **Confidence:** confirmed.
- **Evidence:** `TinyWall/TinyWallService.cs:1466-1470` (executable gone) and `:1500-1501` (explicit block exists, not logged) return false; `PromptQueue.cs:231-236` maps to `ApplyFailed`; `BlockedConnectionPopup.cs:66-69` shows a generic message and re-enables Allow.
- **What goes wrong:** when a block rule exists, Allow can never work but keeps being offered with "The request could not be completed".
- **Fix:** return distinct statuses and show a matching message with Allow disabled.
- **Status:** Held (LOW).

#### SW-34. The x86 MSI detects only a 32-bit TinyWall registration

- **Source:** R4 L2. **Confidence:** confirmed.
- **Evidence:** `MsiSetup/Product.wxs:86-99` compiles only the `Win64="no"` search on x86; `/install` still refuses through SCM (`TinyWall/Installer/InstallationSafety.cs:18-31`).
- **What goes wrong:** a later, less friendly failure (error 1722 after files are copied) instead of a clean launch-condition refusal.
- **Fix:** also search with `Win64="yes"` when `VersionNT64` is set.
- **Status:** Held (LOW).

#### SW-35. AI responses are unbounded in size and prompt fields are passed unsanitized

- **Source:** R4 L3 (mitigations added by the R4 audit). **Confidence:** confirmed.
- **Evidence:** `OpenAiCompatibleExplainClient.cs:55` (`ReadAsStringAsync()`, default buffer cap), despite `docs/HARDENING-VALIDATION.md:54` naming response bounds; `AiExplainComposer.cs:45-56` appends publisher, service and file names verbatim (`AiExplainSubject.cs:102-103` only trims); no "AI-generated" label (`BlockedConnectionPopup.AiExplain.cs:34-42, 159-165`). Mitigations: 30 s timeout (`OpenAiCompatibleExplainClient.cs:48`); the system prompt says to treat every value as unverified (`AiExplainComposer.cs:32-33`).
- **What goes wrong:** a hostile endpoint can make the controller buffer a very large body; a self-signed binary can put instructions in its certificate subject, and the reassuring answer appears next to Allow.
- **Fix:** set `MaxResponseContentBufferSize` (for example 64 KiB); strip control characters, cap and delimit each field; label the output as AI-generated.
- **Status:** Fixed in `claude/fix-me` (`ea95440`), included in `claude/pre-switch-fixes`: responses are capped at 64 KiB with one 30 s deadline, prompt fields are sanitized, capped and quoted, explanation and error text are capped, and the output is labelled AI-generated.

#### SW-36. The stored AI key follows a base-URL change

- **Source:** R4 L4. **Confidence:** confirmed. Owner-error risk only.
- **Evidence:** `AiExplainSettingsForm.cs:124-133` keeps the stored key when the key box shows the placeholder.
- **What goes wrong:** changing the base URL to another host (or a typo host) without retyping the key sends the key to that host.
- **Fix:** clear the stored key, or require re-entry, when the host changes.
- **Status:** Held (LOW).

#### SW-37. Locally built releases skip tests, and no release is signed or dependency-locked

- **Source:** R4 L5, partly corrected by the R4 audit. **Confidence:** confirmed.
- **Evidence:** `tools/release/Build-SecureWallRelease.ps1:76-141` runs no tests and offers `-SuppressValidation` (`:13, 119-120`); no `packages.lock.json` anywhere; no signing step. Correction: `.github/workflows/release.yml:29, 33, 56` and `ci.yml:24, 28, 71` do run the core tests, diagnostics tests and installer checks around the same script, so the test gap applies only to MSIs built locally.
- **What goes wrong:** a locally built MSI can ship untested; `SHA256SUMS.txt` beside the MSIs detects corruption, not tampering.
- **Fix:** run the core tests in the script; enable `RestorePackagesWithLockFile`; sign before hashing; warn on `-SuppressValidation`.
- **Status:** Held (LOW).

#### SW-38. The rule configuration and logs are readable by every local user

- **Source:** R4 audit (scope gap, rated LOW information disclosure).
- **Confidence:** confirmed by code (same ACE as SW-18).
- **Evidence:** `TinyWall/Installer/MachineDataGuard.cs:57` gives BUILTIN\Users read on every file under `%ProgramData%\SecureWall`, including `config` (AES with a key embedded in the binary) and the logs (exception text with paths).
- **What goes wrong:** any local user can read the full rule set and log paths.
- **Fix:** give `config` and the logs the same SYSTEM and Administrators DACL proposed for `pwd`; the controller only needs `profiles.json`.
- **Status:** Held (LOW). The P2 port protects only `pwd`; `config` and the logs stay readable by local users.

#### SW-39. Defender's `MpCmdRun.exe` definition fallback is blocked by default

- **Source:** R1 F4 (upstream `fa57ba0` missing, #127). **Confidence:** database content confirmed; behavior per the upstream reproduction.
- **Evidence:** `TinyWall/Database/SpecialApplications/Special Windows Defender.json:1-36` lists only `MsMpEng.exe` and `NisSrv.exe`; the shipped `profiles.json` has no `MpCmdRun`.
- **What goes wrong:** with Windows Update disabled or unavailable, Defender signature downloads via `MpCmdRun.exe` are blocked. With Windows Update working, nothing breaks.
- **Fix:** add the `MpCmdRun.exe` component and regenerate `profiles.json`; do not add `MpDefenderCoreService.exe`.
- **Status:** Fixed by upstream port `fa57ba0` (ports commit `31fe6e1`). On `claude/pre-switch-fixes` it also reaches an existing tree on reinstall, through the SW-20 shipped-data refresh.

#### SW-40. `Transaction.Abort` throws on `FWP_E_NO_TXN_IN_PROGRESS`

- **Source:** R1 F5 (upstream `8f45054` missing). **Confidence:** code confirmed; trigger plausible.
- **Evidence:** `pylorak.Windows.WFP/Transaction.cs:102-109` (any nonzero abort result throws and skips `DangerousRelease`); `Dispose` at `:114-118` calls it; used at `TinyWallService.cs:331, 394, 464` and `TinyWallDoctor.cs:375`.
- **What goes wrong:** after BFE has ended a transaction, `Dispose` throws during unwinding, replacing the original exception in logs, and the engine handle refcount never returns to zero. Policy outcome is unchanged.
- **Fix:** port `8f45054` (treat `0x8032000D` as closed).
- **Status:** Fixed by upstream port `8f45054` (ports commit `273ba57`).

#### SW-41. Network Activity still offers "Search on ProcessLibrary" over plain HTTP

- **Source:** R1 F7 (upstream `9a25ffb` missing). **Confidence:** confirmed.
- **Evidence:** `TinyWall/ConnectionsForm.cs:503-515`; menu at `ConnectionsForm.Designer.cs:50, 176, 189-193`.
- **What goes wrong:** a click sends the executable name in cleartext to a domain upstream considers dead, which anyone could re-register.
- **Fix:** remove the menu item and handler.
- **Status:** Fixed by upstream port `9a25ffb` (ports commit `1ef4f5e`).

#### SW-42. Prompts show IPv6 destinations in non-canonical, unbracketed form

- **Source:** R1 F8. **Confidence:** confirmed; display only.
- **Evidence:** `pylorak.Windows.WFP/NetEventSubscription.cs:27-55, 75-81` (no zero compression); `TinyWall/Prompting/BlockedConnectionPopup.cs:42` (`{RemoteAddress}:{RemotePort}`); also the AI subject (`AiExplainSubject.cs:52, 98`). Matching is unaffected (`DropCorrelator.cs:41-50` parses both sides).
- **What goes wrong:** `2001:db8::1` port 443 shows as `2001:0db8:0:0:0:0:0:0001:443`.
- **Fix:** normalize with `IPAddress.Parse(...).ToString()` and bracket IPv6 as `[addr]:port`.
- **Status:** Held (LOW). `UPSTREAM.md` on the ports branch records it as open.

#### SW-43. Latent WFP wrapper defects fixed upstream but unreachable today

- **Source:** R1 F18 + R2 audit A2 (upstream `a56488f`, `2329049`, `c2ae8df`). **Confidence:** confirmed; no current caller.
- **Evidence:** `pylorak.Windows.WFP/FilterCondition.cs:574` builds malformed SDDL `$"O:LSD:(A;;CC;;;{sid}))"` in `UserIdFilterCondition` (no callers); `PInvokeHelper.cs:83-89` ignores the `CopySid` result (reached only by the unused `PackageIdFilterCondition(IntPtr)`); `FilterConditionList.cs:22` indexer setter refcounts; `Engine.cs:198-230` leaks a provider GUID handle on a failed enum create (not re-checked by the audit).
- **What goes wrong:** nothing today. A future per-user rule (for example the SID-scoped AI rule in SW-19) using `UserIdFilterCondition` would throw inside a policy transaction and trigger SW-01.
- **Fix:** port the three commits plus `7fce29c` as one hygiene batch, or delete the unused classes.
- **Status:** Fixed by upstream ports `a56488f`, `2329049` and `c2ae8df` (ports commit `273ba57`) and `7fce29c` (ports commit `1ef4f5e`).

#### SW-44. The design doc contradicts the implemented baseline and prompt filter

- **Source:** R5 V1. **Confidence:** confirmed.
- **Evidence:** `docs/design/2026-07-14-securewall-design.md:174` describes "eight permits at `DefaultBlock - 1`" for DHCP and DNS; the code registers only denies (`TinyWall/TinyWallService.cs:459-514`), as README.md:21, SECURITY.md:18 and `.claude/reference/architecture.md:15` say. `design.md:81` says candidates exclude SecureWall itself; the filter excludes only `System` (`TinyWallService.cs:2319-2329`). `docs/HARDENING-VALIDATION.md:60` says the R9B cases "have not run", although `5385cab` reports 249 core tests.
- **What goes wrong:** README links the design doc as the threat model; a reader expects DNS and DHCP to keep working with the service down.
- **Fix:** update "Failure behavior" and step 2 in the design doc, and the R9B status paragraph.
- **Status:** Held (LOW).

#### SW-45. The "24 hours" timed exception expires after 19 hours

- **Source:** R5 V12. **Confidence:** confirmed; inherited (upstream `62b088f` also has 1140).
- **Evidence:** `TinyWall/FirewallException.cs:17` (`For_24_Hours = 1140` minutes), labelled 24 hours at `ApplicationExceptionForm.cs:92-93`; expiry uses the value as minutes (`TinyWall/Prompting/EnforcementPolicy.cs:95-97`).
- **What goes wrong:** the rule expires 5 hours early. Fails closed.
- **Fix:** add a 1440 value and map persisted 1140 to it on load; the numeric value is serialized, so do not just edit the constant.
- **Status:** Held (LOW).

#### SW-46. `UPSTREAM.md` omitted `c4cd972` from its not-backported list

- **Source:** R5 audit A3. **Confidence:** confirmed at HEAD `fe0d011`.
- **Evidence:** `UPSTREAM.md:30-36` does not list `c4cd972`, although `TinyWall/PasswordForm.cs:16` lacks the fix (SW-08).
- **What goes wrong:** the list reads as exhaustive, so the password-locked uninstall defect was invisible from it.
- **Fix:** list it; the `UPSTREAM.md` rewrite in the separate upstream pull request is where this lands.
- **Status:** Fixed by the `UPSTREAM.md` rewrite on the ports branch (`a257605`), which lists `c4cd972` as backported.

## 4. Questions about intent

These are not defects. Each needs a decision from the owner.

**Failure and recovery**

- **Q1. Keep "service loss means fully offline"?** (R5 V2) TinyWall keeps saved allows in persistent filters, so a TinyWall crash leaves networking working. SecureWall's persistent baseline is deny-only (`TinyWall/TinyWallService.cs:84, 459-514`): at boot and whenever the service is down, this PC's single DHCP adapter gets no DHCP or DNS. The owner chose this on 2026-09-12 (`docs/superpowers/plans/2026-09-12-pre-switch-review-fixes.md:57`), reversing #10 a week earlier, and boot timing has never been measured on real hardware. Reconfirm it alongside SW-01 and SW-02.
- **Q2. If net-event subscription fails at startup, should the service stop or run without prompts?** (R1 F11) `TinyWall/TinyWallService.cs:2115` subscribes without a catch, so a persistent `FwpmNetEventSubscribe` failure crashes the service at start; SCM restarts it once, then leaves it stopped behind the deny baseline (see SW-01). Upstream `2ed2d37` now logs and continues. Options: no network, or policy without prompts plus a visible warning.

**Prompt behavior**

- **Q3. Is "Allow" meant to be permanent, all destinations and ports, bound to a path?** (R3 N1, R5 V5) `TinyWall/Prompting/PromptAllowPolicy.cs:7-12`, `FirewallException.cs:46`. No destination, port or time scope, no hash pinning (`ExceptionSubject.HashSha1` exists but is unused), rules never expire, versioned folders re-prompt after each update, and a file replaced at the same path inherits the grant.
- **Q4. Should Ignore stick?** (R5 V4) Ignore, close and the 30 s timeout all set a 5-minute in-memory cooldown (`PromptQueue.cs:54, 261`), so a retrying background app prompts about every 5.5 minutes forever, and restarts reset cooldowns. There is no "Block" button (`BlockedConnectionPopup.Designer.cs:120, 134`).
- **Q5. Is it clear that the popup covers only outbound TCP/UDP?** (R3 N2, R5 V3) Prompts need a Normal-mode default-block drop, outbound, TCP or UDP, and a non-`System` path (`TinyWallService.cs:2319-2329, 423-424`). Inbound, ICMP, kernel `System` traffic (for example SMB to a NAS), unattributed service hosts and every non-Normal mode stay silently blocked. In a burst, the 32-prompt queue and 2-minute token life mean later prompts expire quietly.
- **Q6. Should Learning mode time out?** (R1 F12) `TinyWallService.cs:1745-1746` accepts Learning with no expiry; only a reboot ends it. Upstream plans a forced timeout (#132). Learned rules are also unmarked (#139). See SW-03. **Settled:** the owner had Learning mode removed (`claude/fix-nl`).
- **Q7. Should prompts be per session?** (R3 N3) `TinyWallService.cs:1707-1716` returns every pending prompt to every controller. With one account on this PC it does not matter.
- **Q8. Is a 1.2 s window for service attribution acceptable?** (R3 N4) `DropCandidateBuffer.cs:15`; an uncorrelated drop from a service executable is non-allowable (`TinyWallService.cs:2397-2400`). If 5157 events arrive late, service prompts show Allow disabled and need a manual rule.
- **Q9. Should package prompts show a name instead of a SID?** (R3 N5) The popup shows "App package S-1-15-2-..." (`BlockedConnectionPopup.cs:96`) and the saved rule uses the SID as its name (`TinyWallService.cs:1486`). `UwpPackageList` could resolve it.

**Install, migration and upgrades**

- **Q10. Keep `/autowhitelist` on interactive installs?** (R4 N1) A double-clicked MSI runs `SecureWall.exe /autowhitelist` (`MsiSetup/Product.wxs:147, 162`), which adds full database profiles (some with inbound) for every known app found, with no prompt (`TinyWallController.cs:1284-1290, 1377-1380`). This runs against "unknown apps prompt first". The VM runner uses `/qn` (`tools/vm/Run-SecureWallVmValidation.ps1:130`), so this path is never validated.
- **Q11. Should uninstall keep the password and rules?** (R4 N2) Intended "preserve evidence" behavior (`docs/SECURITY.md:66`, `MachineDataGuard.cs:100-102`); a reinstall comes back password-locked with the old rules. Worth one sentence in README.
- **Q12. Accept an unprotected window on every upgrade?** (R4 N3) Upgrades are refused (`Product.wxs:47-49, 134-136`), so each new build means uninstall (Windows Firewall defaults in between) and reinstall, which reruns `/autowhitelist` under full UI.
- **Q13. How much of the TinyWall 3.3.1 import should be trusted?** (R5 V10, R4 audit scope gap) Import deserializes a whole `ConfigContainer` and replaces mode, blocklists and hosts protection (`TinyWall/SettingsForm.cs:551-569`). No test covers `.tws` import, and nobody has checked how legacy fields map (for example inbound or special-exception grants). TinyWall moved to JSON in 3.3.0, so 3.3.1 exports are plausibly compatible.
- **Q14. Are the removed TinyWall features acceptable?** (R5 V7) Global hotkeys (Ctrl+Shift+W/E/P) are gone; all update traffic (app, database, hosts list) is off, and on `claude/pre-switch-fixes` the update client itself is deleted, so the database and hosts list change only when an installer refreshes them; Learning mode is removed on the same branch; until-reboot exceptions now also expire on service restart; the Windows Update special exception is narrowed to `wuauserv` TCP, so BITS, Delivery Optimization or UsoSvc may need rules (untested); donate and submission controls are hidden.
- **Q15. Is the change of hosts blocklist source acceptable?** (R5 audit A2) This PC's live hosts file is the MVPS list from TinyWall 3.3.1. SecureWall ships Dan Pollock's someonewhocares.org list (`MsiSetup/Sources/CommonAppData/SecureWall/hosts.bck`), so blocked domains will differ.
- **Q16. What should happen to TinyWall's leftover Windows Firewall rules?** (R4 N4) TinyWall creates `TinyWall Compat` allow-all rules in group `TinyWall` (upstream `WindowsFirewall.cs:119-148`) and swallows cleanup errors (`:164-181`). If they survive the uninstall, they expose the PC while no WFP layer runs. The checklist removes them.

**Diagnostics and logging**

- **Q17. Accept Security log churn from always-on 5157 auditing?** (R5 V8) The service leases failure auditing for its whole lifetime (`TinyWall/FirewallLogWatcher.cs:46-52`); upstream audits only in Learning. Every blocked connection, including LAN broadcast noise, writes an event and rolls the 20 MB Security log faster. No doc mentions it.
- **Q18. Should diagnostics record what was allowed?** (R5 V9) The journal omits app paths and destinations by design, and importing a `.tws` turns diagnostic logging off (`ServerConfiguration.cs:157`).
- **Q19. Should the support bundle collect installer and service logs?** (R4 N5) `tools/diagnostics/Collect-SecureWallDiagnostics.ps1:37` gathers only `system`, `service`, `binary`, `events` and `journal`. Adding logs needs an opt-in switch because they contain paths.

**Network scope**

- **Q20. Is unfiltered VM, WSL2 and Docker traffic acceptable?** (R2 N1) No IPFORWARD layers are filtered (`TinyWallService.cs:516-532`), so NAT-forwarded and bridged guest traffic plausibly reaches the internet even in Block All or with the service stopped (WFP architecture, not tested). Not relevant on this PC today (no Hyper-V or WSL).
- **Q21. Should "local subnet" trust adapter prefix lengths?** (R2 N2) `pylorak.Windows/NetworkAdapterEnumerator.cs:156-161` and `TinyWallService.cs:1970-1976` add every up adapter's on-link prefix unchecked, including VPN and tunnel adapters. A floor (for example IPv4 /8, IPv6 /48) and a visible list would bound it.
- **Q22. Will a VPN client be used?** (R2 N3) SecureWall never sets `FWPM_FILTER_FLAG_CLEAR_ACTION_RIGHT` (defined at `pylorak.Windows.WFP/Filter.cs:14`, unused), so a VPN kill switch with hard permits in a higher-weight sublayer overrides SecureWall's blocks. If so, check with `netsh wfp show filters`.

**Dormant and leftover code**

- **Q23. Delete the dormant updater?** (R1 F9) `TinyWall/SecureWallProduct.cs:9` compiles it off, but `TinyWall/UpdateChecker.cs:196` still targets `tinywall.pados.hu` and `:124-147` would run a downloaded MSI unchecked. Flipping one constant would install TinyWall's binaries. Delete it, or own the URL and port `30c1712`, `72ee63a`, `e0e7e22`, `f264655` first. **Settled:** deleted on `claude/fix-up` (`b5f70d8`); a source-scan test fails if the feed URL or updater names return.
- **Q24. Keep `/develtool` in Release builds?** (R1 F10) Parsed at `TinyWall/Program.cs:346-347`, dispatched at `:425-426`, compiled per `TinyWall.csproj:127, 155`. It runs asInvoker, so no privilege gain, only extra surface.
- **Q25. Delete `docs/pad_file.xml`?** (R1 F16) It still describes TinyWall with TinyWall download URLs (`:36, 100`). **Settled:** deleted on `claude/fix-up` (`b5f70d8`).

## 5. Ideas, improvements and hardening

Prioritized. Items marked with a finding ID are the fix direction for that finding.

**Before the switch**

1. **Run a minimal VM gate** (R5 I1). One to two hours in a Windows 10 19045 VM with a snapshot, using `tools/vm/Prepare-SecureWallVmBundle.ps1` and `Run-SecureWallVmValidation.ps1`, plus four manual checks: import a real TinyWall 3.3.1 `.tws`; reboot and time how long until DHCP and DNS work; kill the service and confirm SCM restarts it; MSI uninstall while password-locked. Record results in `docs/validation/<date>-<build>.md` with the fields `docs/HARDENING-VALIDATION.md:3` requires. The owner decided to test on the real PC instead (section 1); the `.tws` import and the reboot timing checks are in the section 6 checklist.
2. **Ship a vetted emergency WFP release** (SW-02; R4 H1 fix, R5 I7). A SYSTEM-only mode or reviewed script that lists, then deletes, only objects under `SECUREWALL_PROVIDER_KEY`, dry-run by default, with a transcript. `DeleteWfpObjects` (`TinyWallService.cs:2012-2034`) already scopes deletion this way.
3. **Stop exiting on environmental reload failures** (SW-01; R2 H1 fixes). Keep the committed policy on abort and retry; rebuild the session when revocation is needed; nonzero exit code plus repeated SCM restarts; controller offers a one-click start.
4. **Backport `c4cd972`** (SW-08; R5 I2).
5. **Make hosts protection best-effort** (SW-05) and **stop post-ready guards from crashing the service** (SW-04).

**Prompt safety and usability**

6. **Harden the popup** (SW-12, SW-14, SW-15; R3 B4-B6, R5 I9). Ignore as default button, 1 s arming delay, hardware-input check, `WS_EX_NOACTIVATE`, inline unlock, second confirmation for unsigned or user-writable executables.
7. **Popup actions that match real use** (Q3, Q4; R5 I4, R3 idea 1). "Block" writes an explicit block rule; "Allow for 1 hour" uses `AppExceptionTimer.For_1_Hour`; optional "this destination only", "this port only", "until reboot". Do not start the cooldown on an unattended timeout; keep such items in a tray backlog.
8. **Show the backlog** (R3 idea 3). "N more waiting" on the popup and a list view, instead of losing prompts to expiry and revocation.
9. **Pin prompt-created rules in user-writable folders to a hash or publisher** (Q3; R3 idea 2), re-prompting when the file changes. Upstream #133's sibling relocation, gated on the same Authenticode publisher (`TinyWall/AuthenticodePublisher.cs` exists), is a related idea for versioned folders.
10. **Show lock state** (SW-13; R3 idea 4). A tray indicator "unlocked, relocks in M min" and a one-click lock.
11. **First-run guidance** (R3 idea 5). Set a password, check the service inventory parses.
12. **Record rule origin** (Q18; R5 I5). Add an `Origin` field (prompt, manual, import) to `FirewallExceptionV3` (`FirewallException.cs:26-42`) and a "recently allowed from prompts" view; optionally a local allow and ignore history kept apart from the scrubbed journal.
13. **Quiet mode** (R5 I10). Defer popups while fullscreen (`SHQueryUserNotificationState`) with a tray counter; traffic stays blocked either way.
14. **Fix or fence the AI helper** (SW-19; R5 I3).

**Enforcement and install**

15. **Own sublayer GUIDs and TinyWall provider detection** (SW-06; R2 M2).
16. **Refresh shipped data on install** (SW-20; R4 M2) and **a hash-pinned manual blocklist update** (R5 I14).
17. **Event-log reason on guard failure and log rotation** (SW-09; R4 M5).
18. **Local-subnet sanity floor and visible list** (Q21; R2 N2).
19. **Net-event health diagnostic** (R1, upstream #130). Warn when no net events arrive while drops are expected, instead of a blind re-subscribe.
20. **Security log size** (Q17; R5 I8). Document it, or raise the size at install as a journaled, restorable change.
21. **Tested upgrade path** (Q12; R5 I13). A transactional upgrade avoids repeating the uninstall and reinstall risk every release.

**Release and tests**

22. **Signed, attestable releases** (SW-37; R5 I6). Add `actions/attest-build-provenance`, pin `actions/*@v4` to commit SHAs (`ci.yml:14, 16, 93`; `release.yml`), then Authenticode signing.
23. **Close test gaps** (R5 I11). A `.tws` import fixture from real 3.3.1 and 3.5.1 exports; `AppExceptionTimer` minutes versus labels; `PasswordForm` with a null controller; run the Debug protocol self-tests in the release job.
24. **Add live-matrix rows** (R3 idea 6, R4 H1 fix 4, R4 M1, R4 N1, R3 AF1). Focus and Enter behavior; locked-Allow flow; relock with a noisy blocked app; LAN-only rule then prompt Allow; Network Activity menu during refresh; deleted data folder, moved-in file, UAC off, `icacls` on Program Files; forced `/install` failure with MpsSvc stopped; full-UI install; an own-process SidType NONE service Allow.
25. **Upstream ports** (R1, R5 I12). See the ranked list in section 2.

## 6. Safe switch checklist for this PC

This PC, observed read-only on 2026-09-29: TinyWall 3.3.1.0 running with a password set; hosts file is TinyWall's 327 KB MVPS list (`hosts.orig` is 824 B); Windows 10 build 19045; one DHCP Ethernet adapter; no VPN, Hyper-V or WSL; no SecureWall state. Run commands in an elevated PowerShell at the local console.

This checklist assumes a build of `claude/pre-switch-fixes` at the audited integration head `b192054` or later, which contains the fixes in section 3. The release-prep commits after it (`ca2cc3d`, `d68b4b8`) only set version 0.4.0 and add release notes. Once published, the v0.4.0 pre-release (tagged after this branch merges) is the build to use. Do not use v0.3.0 for the switch: it has none of the fixes, and the finding text in section 3 describes its behavior.

The owner has decided to test on this PC without a VM run, so no row of the VM matrix has run on this build, and this PC is the first live test of every fix. The checklist is the safety net for that: steps 1 to 4 (offline copies of both installers, a TinyWall `.tws` export, a restore point or disk image, a local administrator who can sign in offline) and "Getting back online" are what make a failed test recoverable. Do every step at the local console, never over remote access.

**Before switching, while online**

1. Save to local disk and a USB stick: the fixed `SecureWall_x64.msi` and its `SHA256SUMS.txt` (check with `Get-FileHash`); the matching source zip (for `tools/diagnostics`); a TinyWall installer (the Apps entry has no cached MSI); these instructions. SecureWall no longer checks for updates (the updater was removed), so keep the installer you used; later builds have to be fetched and installed by hand.
2. Unlock TinyWall, then Settings > Maintenance > Export to a `.tws` file. Screenshot the Application Exceptions and Special Exceptions tabs as a fallback (Q13). Copy `C:\ProgramData\TinyWall` somewhere safe; a later TinyWall reinstall may wipe it (SW-10).
3. Create a System Restore point, and ideally a full disk image. The image is the reliable fallback; whether a registry restore removes persistent WFP objects is unverified.
4. Confirm a local administrator account can sign in with the network down.
5. Check UAC is on: `(Get-ItemProperty HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System).EnableLUA` must be `1`. With UAC off, controller logs can plausibly make the service refuse to start (SW-02; the trigger is not fixed). MSI removal still works in that state through the emergency release. Do not use the built-in Administrator account for SecureWall.
6. If you rely on Security log history, raise its size, for example `wevtutil sl Security /ms:<bytes>` (Q17).
7. Decide now: no AI helper unless you need it (SW-19; its LocalSystem denial has not run live). SecureWall has no Learning mode; unknown apps are handled through prompts. Any uninstall path works, including `SecureWall.exe /uninstall` while password-locked, which now asks for the password (SW-08).

**Switch**

8. Unlock TinyWall and uninstall it from Apps. Reboot.
9. Verify:
   - `sc.exe query TinyWall` reports that the service does not exist;
   - the hosts file is back to about 824 B, not the 327 KB list;
   - `Get-NetFirewallProfile` shows Windows Firewall on;
   - `Get-NetFirewallRule -Group TinyWall -ErrorAction SilentlyContinue` returns nothing (without that switch, a "No MSFT_NetFirewallRule objects found" error also means none remain); remove any leftovers with `Remove-NetFirewallRule -Group TinyWall` (Q16);
   - `netsh wfp show state file="$env:TEMP\wfp-state.xml"`, then `Select-String -Path "$env:TEMP\wfp-state.xml" -Pattern '66CA412C','TinyWall'` finds no TinyWall provider, sublayer or filter (SW-06). If it finds any, stop: do not install SecureWall. The fixed installer also refuses in that case and names the objects;
   - browsing works;
   - `C:\ProgramData\SecureWall` does not exist.
10. Install `SecureWall_x64.msi` with the full UI at the local console. Expect `/autowhitelist` to add rules for known apps (Q10). On a generic error 1722, stop, look in the Application event log for a SecureWall event 1000 or 1001 with the reason (SW-09; if the MSI rollback already removed the event source, `docs/SECURITY.md` says the event still reaches the log, without its formatted message text), and keep the logs (README "Installation recovery").

**First tests, in this order**

11. The tray icon appears and the mode is Normal. Restart Explorer once and confirm the icon comes back (SW-21, fixed by port). Open Network Activity; blocked rows now show the block reason.
12. Browser: a prompt appears. Allow stays disabled for about a second after the popup settles, then Allow, retry, it connects. Enter no longer allows (SW-14); still avoid typing while a popup is on screen until the VM focus row passes.
13. A second app: Ignore. It stays blocked; expect a re-prompt in about 5 minutes (Q4).
14. Settings > Import the `.tws`. Review exceptions, mode, blocklists and hosts protection before saving, because import replaces everything, including the browser rule allowed in step 12. An export whose startup mode is Learning imports as Normal, and rules TinyWall learned are imported as they are: look for an executable-wide `svchost.exe` rule with all ports and delete it (SW-03). Imported "local network only" rules stay LAN-only when a prompt Allow later adds an outbound rule for the same app; expect two entries for that app (SW-11). Save, enable diagnostic logging (import turns it off), save again and reopen Settings to confirm.
15. Services without their own SID: when a service's SID type is `NONE`, its prompt shows the service with Allow disabled and gives the fix command, `sc.exe sidtype "<name>" unrestricted`, then a restart of that service (SW-16). Run that command, or add a manual executable rule, knowing that an executable rule also covers every other process started from that file. `sc.exe qsidtype <name>` shows the current type.
16. Reboot. Note how many seconds pass before DHCP and DNS work (Q1). Run a Windows Update scan (Q14). Test sleep and resume, and a USB stick insert, then confirm browsing still works (SW-01).
17. Set a SecureWall password only after reading "Getting back online". With a password, pressing Allow while locked opens the password dialog; after you unlock, the popup retries that Allow and relocks SecureWall (SW-15). During that one Allow the whole service is unlocked, not just that prompt. Enter the password only for a prompt you recognize.

**Rolling back to TinyWall**

18. Uninstall SecureWall first, through Apps or `msiexec /x`, and reboot. Never install TinyWall while SecureWall is installed (SW-04, SW-10).
19. Confirm `sc.exe query SecureWall` reports no service and the WFP state export has no `053FC8F9` provider. Then install TinyWall from the offline copy and import the `.tws`.

### Getting back online

At the local console, in an elevated PowerShell:

- **Offline, service running:** tray > mode Disable (needs the password if set). Disabled mode adds an allow-all above the baseline while you investigate.
- **Offline, service stopped or restarting** (SW-01): the fixed service rebuilds its policy in process after a revocation, and if it still exits with a failure, SCM restarts it after 5, 30 and 60 s, then every 60 s. If it stays offline, the cause persists: collect `C:\ProgramData\SecureWall\logs` and the Application event log, fix the cause, then `sc.exe start SecureWall` or reboot. Opening the tray menu does not start the service; it only shows a balloon.
- **Do not disable the service to get online.** The deny baseline now loads at boot whatever the service start type, so a Disabled or Manual service leaves the PC offline until SecureWall is removed (Codex red 1 fix; confirm in the VM).
- **Service refuses to start:** a `TinyWall` service still stops SecureWall at start (SW-04 correction); uninstall SecureWall with `msiexec /x`, or uninstall TinyWall. Leftover TinyWall WFP objects or a foreign filter in SecureWall's sublayers also stop it, and the log names them (SW-06): remove their owner, reboot, and start the service. A missing or held hosts file no longer blocks startup (SW-05); a failed hosts lock is logged and shown as a health warning.
- **Remove SecureWall:** Apps > SecureWall > Uninstall, or `msiexec /x SecureWall_x64.msi /l*v "$env:TEMP\sw-uninstall.log"`. Elevated `SecureWall.exe /uninstall` also works and asks for the password when locked (SW-08). If only the install-tree ACL is wrong, elevated `SecureWall.exe /uninstall` still reaches WFP removal; the MSI does not.
- **Uninstall refuses because of machine data** (SW-02): use Apps or `msiexec /x`, not `SecureWall.exe /uninstall`. MSI removal runs the emergency release: it does not read `C:\ProgramData\SecureWall`, leaves the hosts file as it is (restore it by hand if hosts protection was on), and removes SecureWall's WFP objects, service and task. If Windows Firewall or audit restoration fails, it stops before WFP removal and the baseline stays. Failures now appear in the Application event log: each failed release step writes one event 1001 from source `SecureWall` naming the step (for example Windows Firewall compatibility restore, audit policy restore or WFP object removal) and the exception, and a failed release adds one 1001 summary with the exit code and the failed steps. Event 1000 records the guard rejection that started the release. Fix the named cause (for example start the Windows Defender Firewall service) and uninstall again. Keep the event log and the folder for inspection.
- **Uninstall refuses because of a foreign filter** (SW-06): the log names the filter and its owner. Remove that owner, reboot, and run the uninstall again. Until then the service stays stopped and the baseline blocks external traffic.
- **MSI removal fails on restoration** (hosts, audit or Windows Firewall): for an ordinary removal, by design the deny baseline stays. Keep the log and use the disk image or restore point. A failed first install's rollback continues past hosts and audit failures and keeps the baseline only while SecureWall's Windows Firewall allow rules may remain (SW-07); then start MpsSvc, install from local media and uninstall.

## 7. Codex cross-vendor review

Every Codex finding below was re-checked by a fresh local verifier that read the cited code, callers and upstream equivalent and tried to refute it. The verdicts and severities are the verifiers', not Codex's.

**Step-2 full review.** OpenAI Codex CLI 0.159.0, model `gpt-6.1-sol` at high reasoning, run as an impartial-review manager with 5 fresh-context sub-reviewers (only the intent reviewer received the brief). Scope: `git diff 1df71b1 fe0d011` limited to the product paths `TinyWall`, `pylorak.Utilities`, `pylorak.Windows`, `pylorak.Windows.Services`, `pylorak.Windows.WFP`, `MsiSetup`, `tests` and `tools` (298 files, 17,444 additions, 2,270 deletions). Codex reported 3 red and 8 yellow findings, ran no builds and no live tests, and recommended fixing the blockers and running the VM matrix before replacing TinyWall.

| # | Codex finding | Verified verdict | Final severity | Relation and status |
|---|---|---|---|---|
| Red 1 | Failed rollback can disable the retained baseline: the baseline provider names the SecureWall service, so BFE drops it at boot when the service is not auto-start | Confirmed | MEDIUM | Same trigger as SW-07; corrects SW-07's outcome after a reboot (any Manual or Disabled start type also dropped the baseline). Fixed in `claude/fix-h2` (`984fe7c`) |
| Red 2 | Learning can turn loopback-only traffic into a permanent unrestricted exception | Confirmed, narrower than stated (Learning only, sockets bound before Learning) | MEDIUM | New, same family as SW-03. Fixed in `claude/fix-h3` (`b05466d`); moot since Learning was removed (`4ff3bac`) |
| Red 3 | Queued mutations can run after the password lock engages | Kept with caveat (the request was approved while unlocked; upstream is similar) | LOW | New. Held |
| Yellow 1 | Service image-path parsing truncates at the first `.exe` substring | Confirmed, narrow trigger | LOW | New. Fixed in `claude/fix-ma` (`37aaa87`) |
| Yellow 2 | Service Allow succeeds while its filter cannot match | Confirmed in code; Windows behavior documented, not live-tested | MEDIUM | SW-16. Fixed in `claude/fix-ma` |
| Yellow 3 | Learning starts even after the audit-log subscription failed | Confirmed | LOW | New. Moot since Learning was removed (`4ff3bac`) |
| Yellow 4 | One malformed service registration disables unrelated Allow prompts | Confirmed | MEDIUM | SW-17. Fixed in `claude/fix-ma` |
| Yellow 5 | Leftover TinyWall filters collide with SecureWall sublayers | Kept with caveat (needs TinyWall leftovers) | MEDIUM | SW-06. Fixed in `claude/fix-mg` (`587d917`) |
| Yellow 6 | Network Activity refresh blocks the controller UI | Confirmed (about 2.4 s per tick while the service is stopped) | LOW | Partly SW-27. Held |
| Yellow 7 | Refresh erases the targets of context actions | Confirmed | LOW | SW-27. Held |
| Yellow 8 | AI responses can exhaust controller memory | Kept with caveat | LOW | SW-35. Fixed in `claude/fix-me` (`ea95440`) |

**Codex check of the three HIGH findings.** A second Codex run (`gpt-6.1-sol`, high reasoning, read-only at `fe0d011`) tried to refute SW-01 to SW-03:

- **SW-01: confirmed, HIGH (availability).** Corrected mechanism: the outage lasts until an explicit service start, a controller relaunch or a reboot, not permanently. The separate claim that a startup failure gets exactly one SCM retry is unproven, because the failure-count reset behavior for `dwResetPeriod = 0` is not established.
- **SW-02: partly confirmed, MEDIUM** for an owner with local administrator access. The product had no recovery path, but Windows offered an escape at `fe0d011`: disabling the service and rebooting would drop the baseline, because its provider named the service (inference from Microsoft's documentation, not VM-tested). The UAC-off trigger is unproven. The Red 1 fix removes that escape by design; the SW-02 fix adds the MSI emergency release in its place.
- **SW-03: partly confirmed, HIGH for svchost** when Learning is used. "Every learned app gets inbound listeners" is disproved: known apps got their database profile (for example SmartScreen is outbound TCP only); unknown apps got the broad fallback.

**Codex reviews of each fix branch and the ports branch.** Every fix branch and the ports branch then received its own Codex full review on its branch diff (same model and effort), with local verification of each finding:

| Branch | Codex findings | Confirmed HIGH or MEDIUM | Outcome |
|---|---|---|---|
| `claude/fix-ma` | 7 | 1 MEDIUM: the SID notice clipped long service names | Fixed in `884d27a` (with a LOW quoting fix) |
| `claude/fix-mb` | 9 | 1 MEDIUM: the popup unlock is global for one Allow round trip, and SECURITY.md overclaimed | Docs corrected and relock made unconditional in `ec4dc8f`; the owner decided against a scoped unlock |
| `claude/fix-mc` | 1 | none | 1 LOW doc inaccuracy, fixed in the integration doc commit `00d9933` |
| `claude/fix-md` | 5 | none | 5 LOW; the stale `docs/TESTING.md` line fixed in `00d9933`, the rest held |
| `claude/fix-me` | 4 | 1 MEDIUM: a non-443 provider URL was accepted though the permit covers TCP 443 only | Fixed in `d4ab8f5` (with a LOW settings-reload fix) |
| `claude/fix-mf` | 8 | none | 8 LOW; the `docs/SECURITY.md` hosts-protection wording fixed in `00d9933`, the rest held |
| `claude/fix-mg` | 4 | none | 4 LOW; two `docs/SECURITY.md` wording fixes applied in `00d9933`, the rest held |
| `claude/fix-h1` | 2 | 1 MEDIUM regression: address sets were published before their filter conditions were built | Fixed in `1e20bcd` |
| `claude/fix-h2` | 3, plus one "remaining limits" note | none | 3 LOW; the `docs/SECURITY.md` overclaims about the emergency release fixed in `00d9933`; the note (journal retention after a full first-install rollback) needs the VM |
| `claude/fix-h3` | 7 | 1 HIGH regression: a learned svchost service rule absorbed an executable-wide svchost rule | Fixed in `acaf454` by making subjects merge only when equal in both directions; that merge rule stays, while the Learning code around it was later removed (`4ff3bac`) |
| `claude/upstream-tinywall-ports` | 2 | none | 2 LOW, held |

Totals: 52 findings; 1 HIGH and 4 MEDIUM confirmed. The HIGH and three MEDIUMs are fixed in code and re-audited; the fourth MEDIUM (`claude/fix-mb`) has corrected docs, and the owner decided not to change the code. Every other finding was verified as LOW.

Run disclosures. Each run's session evidence was checked (exit 0, `gpt-6.1-sol` at high effort, number of sub-reviewers). Two runs were incomplete:

- **`claude/fix-me`:** only 3 sub-reviewers ran (correctness, data flow, security and performance). The fourth spawn was rejected by Codex's agent thread limit, so the remaining reviewers, including the intent reviewer, never ran, and no reviewer read the author brief.
- **`claude/fix-mg`:** one of 6 spawn attempts failed; 5 sub-reviewers ran.

**First integration.** Branch `claude/pre-switch-fixes` (worktree `sw-integration`) at `f6a1c52` holds the five port commits, one merge per fix branch (10), integration regression tests (`f87ad7a`), a doc reconciliation (`00d9933`) and a doc fix (`f6a1c52`). Results (audited at `00d9933`; `f6a1c52` changes three docs only): 396 core tests pass; MSBuild Debug and Release builds and both self-tests (`/protocolselftest`, `/pipeintegrationtest`) pass; installer static checks 156 passed, 0 failed (no WiX, so the MSI was not built). An independent integration audit passed with three LOW items: D1 and D2 (doc wording) were fixed in `f6a1c52`, and D3 is moot:

- **D3 (LOW, moot):** a rule Learning created for a svchost service did not apply the service SID-type check that prompt Allow uses (SW-16), so for a service whose SID type is NONE the learned rule never matched. Not fail-open. The Learning removal (`4ff3bac`) deleted that code.

**Codex review of the integrated branch.** Codex full review of `git diff fe0d011 f6a1c52`: `gpt-6.1-sol` at high reasoning, run as an impartial-review manager with 5 fresh-context sub-reviewers; only one sub-reviewer received the author brief; session evidence checked. Codex reported no red, 3 yellow and 2 green findings, ran no builds and no live tests. A local verifier re-checked each one:

| # | Codex finding | Verified verdict | Final severity | Status |
|---|---|---|---|---|
| Yellow 1 | Learning saves service exceptions that cannot match (no SID-type check; same as D3) | Moot once Learning is removed | n/a | Moot: Learning removed in `claude/fix-nl` (`4ff3bac`, merged `2842c3e`) |
| Yellow 2 | Emergency cleanup loses its actual failure reason: with the data directory rejected, step failures reached only stderr, which the MSI discards, so nothing durable named the failed step | Confirmed (partly mitigated: warnings reached the log, step failures did not); not a regression (the emergency path is new on this branch) | MEDIUM | Fixed in `claude/fix-y2` (`ad08cf2`, Codex follow-up `ba0dfd8`, merged `cb54b5c`): each failed step writes one Application event 1001, and a failed release adds one summary event |
| Yellow 3 | Stale Learning events still perform full SCM service scans | Moot once Learning is removed | n/a | Moot: Learning removed |
| Green 1 | `UPSTREAM.md` status claims are stale (`fa57ba0` reaches reinstalls; `a56488f` and `c2ae8df` run in live code) | Confirmed | LOW | Fixed in the second-integration follow-up `b192054` |
| Green 2 | The Learning confirmation text overstates outbound grants | Moot once Learning is removed | n/a | Moot: the text was removed with Learning |

**Codex reviews of the follow-up branches.** Each follow-up branch (base `f6a1c52`) received its own Codex full review on its branch diff (`gpt-6.1-sol` at high reasoning, 5 sub-reviewers, only one given the author brief, session evidence checked), with local verification of each finding:

| Branch | Codex findings | Verified verdicts | Outcome |
|---|---|---|---|
| `claude/fix-up` (`b5f70d8`, updater removal) | 0 | none | Nothing to fix. Audit: 397 tests, no new warnings, self-tests and installer checks pass |
| `claude/fix-nl` (`4ff3bac`, Learning removal) | 1 green | LOW, confirmed as a test gap only, no defect: no test imports a full legacy `.tws` that names Learning through the production serializer and checks readback | Held. The HARDENING-VALIDATION "Learning mode removed" row covers it live |
| `claude/fix-y2` (`ad08cf2`, emergency release events) | 4 yellow, 1 green | All LOW. Confirmed: 1, 1a (added by the verifier), 4 and 5; kept with caveat: 2 and 3. (1) one failed step could write two 1001 events; (1a) a successful release could log a spurious 1001 when the service stopped on its own during the stop request; (2) controller termination failures are still not reported (pre-existing); (3) the summary claimed the rejected directory "was not read or changed", which overstates what the code guarantees; (4) a HARDENING-VALIDATION row expected a Disabled service in the foreign-ImagePath case, where the startup type stays unchanged; (5) an exception in the Windows Firewall rule check is hidden (pre-existing) | 1, 1a, 3 and 4 (regressions in the branch's own new code) fixed in `ba0dfd8` and re-audited (398 tests, builds, self-tests, installer checks pass). 2 and 5 held. The follow-up `ba0dfd8` itself was audited, not Codex-reviewed |

Totals for these four runs: 11 Codex findings plus one verifier addition; 1 MEDIUM confirmed and fixed; 5 LOW fixed; 3 LOW held; 3 moot.

**Second integration.** `claude/fix-up`, `claude/fix-nl` and `claude/fix-y2` were merged in that order (`b49cd71`, `2842c3e`, `cb54b5c`), with no conflicts; each merge commit's tree equals Git's automatic merge result. The follow-up commit `b192054` corrected the stale `UPSTREAM.md` claims (Green 1 above), changed `faq.html` and `.claude/reference/deployment.md` from "update feed disabled" to "removed", and added the emergency 1001 events to README. Results at `b192054`, run by the independent auditor: 391 core tests pass (396, plus 1 net from the updater removal, minus 8 net from the Learning removal, plus 2 from the emergency events); Debug and Release builds pass with no new warnings (8 fewer, all from the deleted updater code); both self-tests pass; installer static checks 156 passed, 0 failed; no conflict markers; the only remaining `learning` hits in product code are the reserved enum value, the mode-policy helper and comments; the only updater-name hits are the removal test's own pattern list. The audit passed with no defects and two notes: unused updater strings and one icon remain in the resources, and README's event wording is terse but not wrong.

## 8. Method and limits

- **Rounds.** R1 classified the 58 upstream commits `1df71b1..62b088f` and triaged 31 upstream issues and pull requests. R2 reviewed WFP enforcement, service lifecycle and fail-closed paths. R3 reviewed the prompt pipeline, IPC, authentication and controller. R4 reviewed the installer, machine data, migration, updates, diagnostics and the AI helper. R5 covered vision alignment, switch readiness and ideas. The `UPSTREAM.md` update came from the upstream-ports branch, which is merged into `claude/pre-switch-fixes`. This document (R7) merges R1 to R5. R8 added the status lines, port status, Codex results and owner decisions from the audited fix, port and Codex rounds; those rounds built with MSBuild and ran the core tests, but none ran on a VM. Later passes added the integration results, the integrated-branch and follow-up Codex reviews, the updater and Learning removals, and the owner's recorded decisions, as of the audited integration head `b192054` of `claude/pre-switch-fixes`, plus the release-prep commits `ca2cc3d` and `d68b4b8` named in the header.
- **Independent audits.** Each of R1 to R5 was re-checked by a separate auditor that re-read every cited location. Results: R1 54 verified, 2 wrong (classification only, corrected above); R2 12 verified, 1 wrong sub-claim (startup path, corrected in SW-01), 2 auditor-found; R3 14 verified plus 5 notes, 1 auditor-found; R4 16 verified, 1 partly wrong (SW-37), trigger corrections to SW-02, 1 auditor-found; R5 26 verified, 3 auditor-found. Audit corrections, re-rated severities and auditor-found items are applied here; nothing an audit marked wrong is kept.
- **Unverified items.** Whether the popup takes focus when shown (SW-14); whether a registry restore removes persistent WFP objects (checklist step 3); the R5 claim that no other worktree holds VM evidence; SCM's handling of `dwResetPeriod = 0` (SW-01); Windows Installer's handling of a failing rollback action (SW-07); the UAC-off owner trigger (SW-02).
- **Source reading only (R1 to R5).** No live WFP testing, no VM runs, no installs, no service or firewall changes, no MSBuild. The later fix and port rounds built with MSBuild and ran the core tests; none ran on a VM. Host facts come from read-only observations on 2026-09-29 (`sc query`, `Get-NetAdapter`, file versions, a registry scan of service ImagePaths and SID types).
- **Areas not examined:** whether an elevated controller's popup blocks medium-integrity input; settings persistence in the controller; behavior after a Windows Update; a resume-specific handler (`OnPowerEvent` handles only display state); how `.tws` legacy fields map to SecureWall policy; whether services sharing one svchost token share each other's service-SID grants; the signed/unsigned risk probe beyond one line.
- **Revisions.** Product HEAD `fe0d011` (2026-09-29). Fix branch `claude/pre-switch-fixes`: audited integration head `b192054` (2026-09-30), followed by the v0.4.0 release-prep commits `ca2cc3d` and `d68b4b8` (2026-09-30). v0.3.0 is `5385cab`, published 2026-09-13, unsigned pre-release; CI passed on `fe0d011`. Upstream pin `1df71b1` (TinyWall 3.5.1); upstream head `62b088f` (2026-08-15), confirmed unchanged on 2026-09-29.
