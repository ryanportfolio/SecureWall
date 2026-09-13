# A diagnostic trial on your PC

Diagnostic logging helps investigate everyday SecureWall behavior on a physical PC. It records service and policy outcomes while you use your applications. It does not run destructive tests, change the firewall's security rules, or send a report anywhere.

This is an exploratory trial. The owner may choose it while boot/crash packet leakage remains unverified. That choice does not establish the broader production assurance described in `HARDENING-VALIDATION.md`.

## Before switching

Use the local keyboard and screen, with administrator access available. Save the exact SecureWall MSI and its SHA-256 hash, the previous firewall's installer, and recovery instructions locally so they remain accessible without a network connection. Keep a record of the applications you need to allow. Verify the test build and the collector come from the same reviewed source.

SecureWall refuses coexistence with TinyWall. A switch requires removing TinyWall and rebooting before installing SecureWall through its MSI. Do not run an extracted executable as an installer. Existing SecureWall installations require full removal and a fresh install; in-place repair and upgrades are unsupported. A failed removal or an unsafe legacy data directory needs investigation before proceeding. Do not delete recovery files or change their permissions to force installation.

The baseline blocks external traffic while the service is unavailable, including DNS and DHCP. A service failure may therefore leave the PC offline until recovery succeeds. Keep the console available throughout the trial.

## Enable logging

In SecureWall Settings, select **Enable diagnostic logging** and save. It is off by default. The preference changes only after the service successfully applies the settings. A failed save keeps the previous preference.

Disabling the checkbox stops new detailed diagnostic records; retained records remain available for collection. Existing ordinary error logging continues. Early startup before trusted configuration is available and periods with logging disabled are observation gaps.

The journal records service lifecycle, policy and recovery events, blocklist state, hosts operations, audit health, and aggregate filtering observations. See [diagnostic coverage](DIAGNOSTIC-COVERAGE.md) for the evidence available for each operation and its limits. It avoids packet contents, destination addresses, application paths, prompt tokens, passwords and API settings. It uses a bounded queue and retained log files, so old or overloaded observations can be lost. The collector reports gaps and recorded loss counters rather than treating missing events as success.

## Exercise normal use

Record the approximate time and expected outcome of each action:

1. Start an application that should be blocked. Confirm it cannot connect. Allow it through the popup and retry. For a different application, choose Ignore and confirm it remains blocked.
2. Save a small settings change and reopen Settings to confirm it persisted. If the save fails, collect evidence immediately and record what the UI showed.
3. Use your usual browser, updates and other applications. Note missing prompts, repeated prompts, unexpected allows or blocks, and slowdowns.
4. When convenient, exercise your normal sleep/resume, network reconnect and VPN flows. Preserve a bundle before and after an ordinary reboot to compare service runs.
5. Turn diagnostic logging off, save, and confirm the checkbox remains off when Settings is reopened. Old logs remaining on disk are expected.

Do not deliberately corrupt policy, change protected ACLs, terminate BFE, kill the firewall service, or interrupt installation on your everyday machine to obtain diagnostic events. Unexercised failure paths remain untested.

## Collect evidence

Run `tools/diagnostics/Collect-SecureWallDiagnostics.ps1` as described in its adjacent README. The collector is read-only with respect to firewall, service and system policy; its writes are a new report directory and archive. It records missing logs, denied reads, timeouts and other partial results explicitly.

Default collection avoids raw configuration and packet data. Review the README before opting into the network inventory, which discloses addresses, ports and process IDs. The collector does not export a WFP dump. Review the resulting archive before sharing it. The collector does not upload it automatically.

For a useful incident report, include the bundle, the local time, what you did, what you expected, what happened and whether connectivity returned. Application names and destinations you choose to add manually can help reproduce a problem.

## Interpret results

An observed policy commit establishes that the service reported completing that operation. A WFP allow/drop observation records a filtering decision, not end-to-end delivery. A heartbeat establishes that diagnostic code ran at that time; it is not a proof of correct enforcement.

A missing shutdown marker can result from abrupt termination, disabled logging, retention, queue loss or a write failure. Treat it as incomplete evidence, not a proven crash. The same caution applies to missing failure events.

External boot/crash packet checks, counterfeit-service rejection, hostile ACL tests, installer failure rollback, signing trust and physical power-loss durability require their own evidence. They do not become passes because a normal-use trial appears successful.

## If the trial breaks networking

Use the local console and collect a report before changing anything. The controller can request a start of an existing validated SecureWall service. Missing registration requires MSI recovery. If removal reports a failure, preserve the error, machine-data directory and recovery files; failed restoration can intentionally retain the deny baseline. Investigate that failure before attempting another installation or manually removing firewall objects.

Return to the previous firewall only after SecureWall removal completes successfully. Do not install both together or bypass an unsafe-tree rejection.
