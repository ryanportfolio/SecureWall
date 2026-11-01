# SecureWall testing

To try SecureWall on a physical PC with diagnostic logging, see [LIVE-TESTING.md](LIVE-TESTING.md). That trial records normal use and does not cover the failure cases below.

## Automated and non-enforcing checks

Run the pure test harness, native .NET Framework build, debug protocol self-test, and authenticated named-pipe integration self-test using `.claude/reference/commands.md`. The debug `/promptpreview` switch shows the prompt UI with synthetic data; it starts neither the service nor WFP.

Source checks must confirm:

- listener fields are never populated by prompt allow;
- promptable IDs originate only from outbound default-block filters and publish after commit;
- modes other than Normal clear/ignore promptable IDs;
- controller action messages contain token only;
- Allow is in the password-gated message range;
- GPL and upstream provenance files exist;
- runtime service, pipe, task, data directory, installer, executable, and WFP provider identities are SecureWall;
- product sources contain no TinyWall update client: no TinyWall feed host, update descriptor, `/updatenow` switch, or `WebClient`, `DownloadFile`, `DownloadData` or `DownloadString` call; `HttpClient` appears only in the AI client and the release client (`ReleaseCheckClient.cs`); the service, `TinyWallDoctor.cs` and the installer code never reference the release client; the release client names only GitHub's hosts; legacy `AutoUpdateCheck` settings still load;
- Check for updates: the signed release manifest, P1363 signature checks with the embedded keys (which must equal `tools/release/keys`), release selection by signed version and prerelease line, page and candidate limits, redirect and asset-URL policy, the update permit's lease lifetime and user condition, and the `/apply-update` path, package and staging decisions;
- the MSI declarations cover staging's runtime and localization lists, including `System.IO.Pipelines.dll`; compare actual packaged files against every assembly emitted by a clean Release build.
- both defaults target `INSTALLDIR/data-defaults`; only guarded SYSTEM setup writes machine-data defaults, after full-tree validation: an absent `profiles.json` or `hosts.bck` is created, an identical one is left untouched and a different one is replaced; configuration, password, `hosts.orig` and journals are never written, and MSI has no ProgramData writes/deletes.
- source and packaged Windows_Update entries retain the exact `wuauserv` outbound TCP service rule and contain no executable-wide allow.
- persistent and boot-time filters contain only external denial, including DNS/DHCP; no recovery permits survive service loss.
- default-block, block and every other filter registration failure propagates and rolls back instead of being swallowed; only an optional WSL 2 permit failure is logged and skipped;
- Network Activity shows observed allows and blocks, exact TCP states, and local-only listener wording, with one-second refresh.

Build all three MSI packages and `tests\SecureWall.NetworkProbe`, then run `tools\vm\Prepare-SecureWallVmBundle.ps1` to create an ignored, hash-manifested VM bundle. Its guarded runner verifies the selected MSI hash and baseline connectivity, installs that MSI silently, checks the WFP provider, drives separate Allow and Ignore probes, captures 5157/WFP/audit evidence, uninstalls through MSI, and compares service, provider, task, audit policy, firewall rules, notification flags and hosts state. This guided path does not execute every failure case below.

The additional pre-switch fault, reboot, identity and MSI matrix is in [HARDENING-VALIDATION.md](HARDENING-VALIDATION.md). Source tests and package ICE validation do not substitute for it.

## Installer source evidence

Run `tests/installer/Test-SecureWallInstaller.ps1` without an artifacts argument for non-enforcing source checks. This checks XML declarations, guarded seeding order and JSON defaults without building or installing. An artifacts argument requires separately built packages and does not prove live behavior.

## Manual local-console VM matrix

Do not run this matrix on the development host. Use an expendable Windows VM with a named clean snapshot and local/virtual console recovery. The runner in `tools\vm` enforces VM, hostname, elevation, TinyWall-absence, SecureWall-absence, target, and explicit-confirmation guards before installation.

| Case | Expected result |
|---|---|
| Unknown executable makes outbound TCP connection | One bottom-right prompt; connection stays blocked until Allow |
| Network Activity live view | Fresh install shows allowed, blocked, active, and listening filters; rows refresh within about one second. A blocked `svchost.exe` row (default deny or raw socket) shows its PID about six seconds after the block and, in the Services column, the services in that process, both with the tray elevated and with it not elevated (a standard user, or the tray started from the Start menu); for the raw-socket row, the Security log holds a matching 5159 event. Unblock on such a row creates one service rule per listed service; note whether the exception editor appears. A remote address a program looked up shows a name in parentheses within about 30 seconds, and selecting that row survives the name appearing; Copy remote address and the web search use the address alone |
| Network Activity beside Manage | Both windows open and usable at once, and a prompt popup still takes clicks; unblock an app from Network Activity, then press OK in Manage after also changing the theme and the password: the save is refused with the retry warning, the new rule stays, and the theme and password are unchanged. Close Network Activity before pressing OK in an unrefused Manage: its window size and columns are kept. Click the popup while Manage is in front, then press Space and Alt+F4: the popup stays unanswered |
| Allowed / blocked evidence | WFP permit is labeled Allowed; WFP drop is labeled Blocked; app/service, protocol, endpoints, direction, and timestamp are visible |
| Local listener | TCP/UDP endpoint is labeled Listening (local endpoint); UI does not claim external reachability |
| TinyWall remains installed | SecureWall MSI and `/install` both refuse; no SecureWall service/provider/filter is activated |
| Allow outgoing | Permanent exact executable exception; retry succeeds; no inbound listener rule |
| Close (X) | No policy change; five-minute identity cooldown; traffic remains blocked; Manage, Ignored is unchanged |
| Ignore | Traffic remains blocked and no filter changes. Manage, Ignored lists the app (an executable by its full path, a UWP app by package SID, a service by name and path), and service.log notes it. Other prompts on screen stay answerable. Run the app again after five minutes: no prompt, Network Activity shows its outbound TCP/UDP drops as "Blocked (ignored)" (a drop that the prompt pipeline dropped under load stays "Blocked (default deny)"), and the tray's "Blocked apps not answered" list does not gain it. Copy the same exe to another folder and run it: that copy is prompted. Ignore a prompt for a shared service host (several services, Allow disabled): only the five-minute cooldown applies and nothing is listed. Remove the entry in Manage and press OK, within five minutes of the Ignore: the next block prompts again |
| Unanswered popup | Popup stays until its token expires, with no cooldown; typing Enter, Space, Escape or Alt+F4 in another window leaves it open; the expired prompt appears under "Blocked apps not answered" in the tray menu with a balloon |
| Prompts revoked by another Allow | Queued prompts for other apps leave the screen and appear in "Blocked apps not answered"; allowing one there asks for confirmation (and the password when locked), writes an outbound-only rule, and leaves SecureWall locked again; a fresh block from that app replaces the entry with a live prompt |
| Always allow | For a signed app (claude.exe, signed by Anthropic, PBC), the popup's Always allow is enabled and its tooltip names the publisher; for an unsigned exe it disappears once the check fails. Choose it: the exact path is allowed, service.log names the saved signer, and Manage, Always allow lists the rule with the full signer as its tooltip. Copy the signed exe to another folder and run it: its first connection fails, the retry passes without a prompt, service.log notes the added rule, and Network Activity shows that first drop as "Blocked, then allowed by Always allow". Rename an unsigned exe to the rule's name and run it repeatedly: it is prompted each time, and service.log notes the signature mismatch once. Run the signed copy from a network share: it is prompted. Allow a third copy for TCP 443 only through Manage, then make it connect on another port: it is prompted, not widened. Remove the rule in Manage: a fourth copy prompts again |
| Repeated drops | Coalesced prompt, bounded queue, no notification flood |
| Explicitly blocked executable | No prompt; remains blocked |
| BlockAll / AllowOutgoing / Disabled | No default-block prompt |
| Learning mode (removed) | Tray menu has no Autolearn entry; a configuration whose startup mode is Learning (4) starts Normal; a `MODE_SWITCH` to Learning is refused and the mode is unchanged |
| UWP/AppContainer | Prompt identifies package SID; Allow scopes to package |
| Single Windows service PID | Prompt identifies exact service and path; Allow scopes to both |
| Multiple services in one PID | Warning shown; Allow disabled |
| Registered service executable without 5157/PID enrichment | Warning shown; Allow disabled |
| Password lock enabled | While locked, Ignore on an app that can be listed opens the password dialog. Cancel or wrong password closes the popup with only the five-minute cooldown: Manage, Ignored is unchanged. Correct password adds the app to Manage, Ignored, closes the popup, and the service is locked again afterwards. Ignore on a shared service host asks for no password and closes the popup. Allow opens the password dialog and the popup stays open while it is shown. Cancel or wrong password creates no exception and leaves the popup open. Correct password creates the rule for that prompt and the service is locked again afterwards (see HARDENING-VALIDATION) |
| Controller exits/restarts | Service keeps enforcing; pending tokens expire; no grant on shutdown |
| Service restarts/reboot | Strict external deny, including DNS/DHCP, covers the baseline-only interval; saved allows return only after successful startup; audit flags are correct |
| Audit category initially success-only/failure-only/both/none | SecureWall adds required flags and restores exact original flags on stop |
| Config save or WFP reload fault injection | Allow reports failure and no new permission survives; recoverable failure preserves the pending token; failed recovery withdraws runtime grants |
| Queue/candidate overflow | New entries are dropped fail-closed; memory stays bounded |
| Uninstall | Service/task/provider/filters/compat rules removed; prior host settings restored |

Record OS build, architecture, SecureWall commit, each result, Event Viewer evidence, WFP filter IDs, and recovery outcome.
