# Diagnostic coverage

Enable **Settings > General > Enable diagnostic logging** before exercising a feature. The journal records software operations and observed filtering decisions. The collector summarizes their outcomes in `coverage.json` and preserves sanitized records in `journal.json`.

## Read the evidence

An attempt shows that an operation started. A success shows that its instrumented call completed. A failure carries a numeric error code without an exception message. Enabled, disabled, present and absent describe the state observed at that point. Skipped means a diagnostic check could not run within its bounds and remains incomplete. A fallback means the service took a recovery/default path that needs context.

Missing completion, missing files, record loss and unobserved paths remain gaps. A successful collection only establishes that the collector read its inputs. It does not establish that every firewall operation succeeded.

## Coverage map

| Area | Evidence to inspect | What it establishes |
| --- | --- | --- |
| Service lifecycle | Start, ready, failure, stop request and shutdown | Which lifecycle boundaries were reached |
| Policy changes | Journal, persistence, enforcement, publication, rollback and recovery | Which part of a settings change succeeded or failed |
| Configuration and database | Load outcomes and fallback | Whether saved policy and bundled exception data were available |
| Port blocklist | Requested state, rule availability and attributed drop count | Whether the blocklist was configured and whether WFP reported drops against its committed filters |
| Hosts blocklist | State, backup, install, restore and restore comparison | Which file operations completed and whether the restored bytes matched the saved original when comparison was available |
| Hosts protection and DNS | Protection and cache-flush outcomes | Whether those calls succeeded; applications may retain their own DNS results |
| WFP and audit observations | Subscription, audit lease/recovery outcomes, health and record errors | Whether the observation channels and their audit configuration were available |
| Windows Firewall compatibility | Start and stop outcomes | Whether compatibility operations completed |
| Rule lifetime | Expiry processing | Whether the service processed expiring exceptions |
| Prompt actions | Allow and Ignore outcomes, including refusal reasons | Whether the service accepted an action |
| Missing prompts or rules | Coalesced suppression, attribution failures and unavailable rule paths | Whether those conditions occurred; coalesced event counts are not counts of every affected connection |
| Environment changes | Network and display reload outcomes | Whether policy reapplication completed |
| Journal health | Sequence gaps, dropped records and write failures | Whether the diagnostic history has known losses |
| Installed service | State, startup mode, PID, exit code and expected registration facts | What SCM reported during collection |
| Installed binary | Registered file hash, version and signature result | Identity of the file on disk, not proof of the bytes already loaded by a running process |

A settings save reapplies policy as usual. After a successful save enables logging, the service records its effective blocklist, hosts-protection and audit state; the snapshot itself does not reapply policy or replay earlier operations. State observations inside apply and rollback paths can also appear, preserving evidence when an operation fails before the final snapshot. The collector accepts legacy journal records as well as the expanded schema. Old records cannot supply observations or counters that did not exist when they were written.

## Live trial actions

Record the time, expected behavior and actual result while using the app. For port blocking, choose a reachable test endpoint you control and a listed port; a failed connection alone cannot identify its cause. The attributed drop counter provides additional WFP evidence without exporting the destination.

For domain blocking, use a known entry from the installed list and check the application's actual behavior. Hosts-file installation does not establish that every application's resolver used it. A hosts-based block has no corresponding WFP domain-drop event. Both bundled blocklists are static in this release; diagnostics do not refresh their contents or establish that each entry is appropriate.

When you normally turn the domain blocklist off, inspect the restore and restore-comparison observations. Missing comparison or failed comparison must remain unresolved. Comparison is opt-in and capped at 1 MiB; it reads local files synchronously during restoration. The byte cap bounds the amount read, but cannot impose a deadline on Windows filesystem I/O. Do not deliberately corrupt the hosts file or recovery data on an everyday PC to create test events.

## Remaining boundaries

The logging checkbox applies to the running service's detailed journal. Installation, early trust rejection and removal outside that service lifetime may have no journal. The collector includes bounded relevant SCM/Application event metadata when available; it does not copy raw installer or controller logs. Preserve an installer error or controller error separately when one occurs.

The journal avoids application paths, destinations, raw hosts contents, configuration, prompt tokens and credentials. To associate an incident with a particular application, include its name and the time in your report. Optional network inventory is a separate, explicitly selected snapshot.

Neither journal nor collector proves packet delivery, absence of boot/crash leakage, hostile filesystem race resistance, installer rollback, or power-loss durability. Disabled logging, retention and abrupt termination can leave missing evidence. Keep those limits alongside any live trial results.
