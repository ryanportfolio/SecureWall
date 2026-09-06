# Hardening release validation

This is the release gate for the pre-switch hardening change. Every live case below is **not yet run** unless an evidence record names the exact source revision, MSI hash, Windows build/architecture, VM snapshot, result, and supporting files. Source tests and packaging checks do not establish packet behavior.

Use a disposable Windows VM with working local/virtual console, a clean snapshot, and a second VM for inbound probes. Never exercise service termination, policy fault injection, audit changes, or installation on the development host. Build all Release MSIs, then prepare the bundle using `tools/vm/Prepare-SecureWallVmBundle.ps1`. Its guarded runner covers only the basic fresh-install path through the actual MSI.

| Case | Required result and evidence |
|---|---|
| Fresh MSI, full UI and silent | Own deny filters commit before compatibility allows; unknown TCP/UDP remain blocked; capture MSI log, WFP state and external probes |
| Normal prompts over IPv4/IPv6 | Only eligible outbound drops prompt; runtime filter IDs are promptable; Allow creates only permanent subject-wide outbound TCP/UDP access; Ignore/close/expiry change no policy |
| Save, registration and commit failures | Request reports failure; no false state/success display; prior policy remains coherent or a visible blocked/degraded state results; explicit deny never silently disappears |
| Expiring grant plus storage/registration failure | At the expiry maintenance pass, any snapshot/save/enforcement failure withdraws the dynamic session; traffic cannot retain the expired permission through repeated failures |
| Recovery failure with active event subscription | Native session close completes before compatibility cleanup; retained SafeHandle references cannot leave candidate grants active |
| Malformed explicit block address | Rule construction fails the replacement transaction, including mixed valid/invalid lists; a valid opposite-family address alone may be skipped for the other address family |
| BlockAll with LAN and WSL_2 enabled | Both inbound and outbound LAN/WSL traffic remain denied through supported paths; no higher-priority optional permit |
| Service stop/crash, startup failure and BFE restart | Runtime allowances disappear; persistent deny baseline remains; saved settings restore only after successful service start; no temporary allow on next boot |
| Cold boot/fast startup from every mode | Disabled/Learning and timed/until-reboot grants never become persistent permissive policy; measure early outbound and inbound traffic |
| Password inactivity | Unlock, leave tray polling for eleven minutes, confirm relock; background activity cannot refresh the user-activity deadline |
| Counterfeit pipe and multi-user sessions | Wrong process/token/service PID rejected before credentials; real SYSTEM service works; prompt tokens remain service-owned, bounded, and single use |
| Paused popup/backlog | AI expansion, service restart, repeated drops and multiple controllers cannot keep expired or absent tokens actionable or grow an unbounded display queue |
| TinyWall coexistence | MSI, direct install, elevated controller recovery and service entry refuse activation before mutation when TinyWall exists |
| Unsafe payload tree | Downloads, writable Program Files subdirectories, writable DLL/config files, untrusted owners and reparse paths are rejected before service registration |
| Fresh install rollback at each stage | No leftover service/task/provider/compatibility allowances; restore original notifications, audit and hosts state; no inaccessible UI or password dialog |
| Startup stuck beyond its advertised deadline | Failed-install SYSTEM rollback disables restart and attempts bounded graceful cleanup, then can terminate only the validated held service process; no PID-reuse victim or running service survives payload rollback |
| Explicit MSI removal, unlocked and password locked | Privileged MSI cleanup remains noninteractive; ordinary interactive recovery retains its confirmation path; failure returns nonzero |
| Unresponsive service during ordinary removal | Bounded stop fails before protection or files are removed; preserve logs and recover via snapshot/local console |
| Uninstall after abrupt exit | Restore owned compatibility state even with service stopped; remove own deny baseline only after fallback restoration succeeds |
| MpsSvc stopped or disabled | Fresh installation rejects an unavailable prerequisite before registration; dependency ordering starts MpsSvc at boot; removal skips COM only with no recorded compatibility state; recorded restoration failure retains deny baseline |
| Slow service disposal | SCM stays stop-pending through late cleanup; within the bounded deadline it reaches stopped, otherwise the process exits and runtime grants disappear; no running state with a disposed worker |
| Process exits during failed-install rollback | A failed process open triggers a fresh SCM status check; cleanup continues only when stopped and restoration succeeds |
| Held service handle during removal | Completed cleanup accepts checked deferred deletion; fresh install remains refused until handles close or reboot removes the old registration |
| Corrupt config.recovery | Startup fails closed; preserve evidence and restore a known-good backup or perform full MSI removal/reinstall from the local console |
| Repair, equal-version rebuild and upgrade attempts | Unsupported operations reject before any teardown; existing service, files and effective policy remain unchanged |
| Mixed notification settings and colliding rule name | Restore each original profile value; preserve unrelated rules whose names contain SecureWall; a foreign reserved-name rule alone rejects compatibility acquisition before notification/rule mutation; inspect durable journal cleanup |
| Other networking | DNS/DHCP changes, VPN reconnect, sleep/resume, ICMP/path-MTU, existing flow revocation, WSL NAT/mirrored networking, Hyper-V/containers and each supported CPU architecture |

Capture the complete before/after firewall rule inventory, profile notification values, audit policy, hosts hash, service configuration, scheduled tasks and WFP objects. The basic runner compares these for its happy path; crash and fault cases need separate captures. Any group-policy or unrelated administrator change during a run must be recorded rather than attributed automatically to SecureWall.

The AI feature must reject HTTP and redirects, disclose the actual transmitted identity fields, and label publisher information as unverified. Pure tests cover construction and validation; a local HTTPS test endpoint can verify transport/cancellation/response bounds without sending real user data or paid-provider credentials.

Signed production artifacts and an upstream/dependency maintenance process remain separate release requirements. No automatic update feed is introduced by this change. Preserve the exact build inputs and hash manifest, and monitor upstream/dependency fixes before adopting later releases.
