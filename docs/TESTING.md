# SecureWall testing

## Automated and non-enforcing checks

Run the pure test harness, native .NET Framework build, debug protocol self-test, and authenticated named-pipe integration self-test using `.claude/reference/commands.md`. The debug `/promptpreview` switch is the only UI check permitted without firewall-activation approval; it starts neither the service nor WFP.

Source checks must confirm:

- listener fields are never populated by prompt allow;
- promptable IDs originate only from outbound default-block filters and publish after commit;
- modes other than Normal clear/ignore promptable IDs;
- controller action messages contain token only;
- Allow is in the password-gated message range;
- GPL and upstream provenance files exist;
- runtime service, pipe, task, data directory, installer, executable, and WFP provider identities are SecureWall;
- upstream binary updates are disabled.
- the staged MSI payload includes every runtime assembly emitted by a clean Release build, including `System.IO.Pipelines.dll`.
- every filter registration failure propagates and rolls back instead of being swallowed;
- Network Activity shows observed allows and blocks, exact TCP states, and local-only listener wording, with one-second refresh.

Build all three MSI packages and `tests\SecureWall.NetworkProbe`, then run `tools\vm\Prepare-SecureWallVmBundle.ps1` to create an ignored, hash-manifested VM bundle. Its guarded runner verifies the selected MSI hash and baseline connectivity, installs that MSI silently, checks the WFP provider, drives separate Allow and Ignore probes, captures 5157/WFP/audit evidence, uninstalls through MSI, and compares service, provider, task, audit policy, firewall rules, notification flags and hosts state. This guided path does not execute every failure case below.

The additional pre-switch fault, reboot, identity and MSI matrix is in [HARDENING-VALIDATION.md](HARDENING-VALIDATION.md). It is a required release gate; source tests and package ICE validation cannot substitute for it.

## Manual local-console VM matrix

Do not run this matrix on the development host. Use an expendable Windows VM with a named clean snapshot and local/virtual console recovery. The runner in `tools\vm` enforces VM, hostname, elevation, TinyWall-absence, SecureWall-absence, target, and explicit-confirmation guards before installation.

| Case | Expected result |
|---|---|
| Unknown executable makes outbound TCP connection | One bottom-right prompt; connection stays blocked until Allow |
| Network Activity live view | Fresh install shows allowed, blocked, active, and listening filters; rows refresh within about one second |
| Allowed / blocked evidence | WFP permit is labeled Allowed; WFP drop is labeled Blocked; app/service, protocol, endpoints, direction, and timestamp are visible |
| Local listener | TCP/UDP endpoint is labeled Listening (local endpoint); UI does not claim external reachability |
| TinyWall remains installed | SecureWall MSI and `/install` both refuse; no SecureWall service/provider/filter is activated |
| Allow outgoing | Permanent exact executable exception; retry succeeds; no inbound listener rule |
| Ignore, close, and 30-second timeout | No policy change; five-minute identity cooldown; traffic remains blocked |
| Repeated drops | Coalesced prompt, bounded queue, no notification flood |
| Explicitly blocked executable | No prompt; remains blocked |
| BlockAll / AllowOutgoing / Learning / Disabled | No default-block prompt |
| UWP/AppContainer | Prompt identifies package SID; Allow scopes to package |
| Single Windows service PID | Prompt identifies exact service and path; Allow scopes to both |
| Multiple services in one PID | Warning shown; Allow disabled |
| Registered service executable without 5157/PID enrichment | Warning shown; Allow disabled |
| Password lock enabled | Ignore works; Allow returns locked and creates no exception |
| Controller exits/restarts | Service keeps enforcing; pending tokens expire; no grant on shutdown |
| Service restarts/reboot | Default deny returns early; saved allows persist; audit flags are correct |
| Audit category initially success-only/failure-only/both/none | SecureWall adds required flags and restores exact original flags on stop |
| Config save or WFP reload fault injection | Allow reports failure and no new permission survives; recoverable failure preserves the pending token; failed recovery withdraws runtime grants |
| Queue/candidate overflow | New entries are dropped fail-closed; memory stays bounded |
| Uninstall | Service/task/provider/filters/compat rules removed; prior host settings restored |

Record OS build, architecture, SecureWall commit, each result, Event Viewer evidence, WFP filter IDs, and recovery outcome. Real firewall behavior remains unverified until every applicable row passes.

## Current live evidence

On 2026-07-14, the user reported that a live SecureWall test "seems to work well." After that test, the development host showed TinyWall running with no SecureWall service, process, scheduled task, data directory, or installed-app entry. This supports basic operation and cleanup, but the test coverage was not recorded row by row and therefore does not replace the full matrix above.
