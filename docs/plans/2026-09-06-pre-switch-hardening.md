# SecureWall pre-switch hardening plan

Baseline: `2aca2ab816c841ad897715a1fce81dd735dc7974`. The user requested implementation of the fourteen findings from the independent pre-switch review. Work is local on `codex/pre-switch-hardening`; publication and firewall activation are outside this change.

## Acceptance

Every finding below must have a code change or explicit safe behavior that removes the defective path, regression coverage at the feasible layer, and an independent audit. Builds and pure tests establish source correctness only. The exact Release MSI still needs a disposable Windows VM with a snapshot and local console before production use.

| Finding | Intended outcome | Files/interfaces | Verification |
|---|---|---|---|
| SW1 | Failed settings, mode and prompt changes preserve coherent prior state and never return success | Service policy application and new pure transition helper | Save/install/rollback fault injection; source integration; VM failure cases |
| SW2 | Missing required rules abort replacement | WFP registration and rule compilation | Registration-failure regression; VM explicit-deny probes |
| SW3 | BlockAll excludes optional LAN/WSL permits | Rule assembly/mode decisions | Pure mode decisions; v4/v6/WSL VM probes |
| SW4 | Temporary permits cannot persist into boot or service failure | Persistent restrictive baseline and dynamic runtime WFP session | Lifetime selection/transaction tests; boot/crash/BFE VM matrix |
| SW5 | Own deny baseline precedes compatibility allowances; uninstall restores fallback even after crash | Service startup, WindowsFirewall, Doctor | Source ordering and cleanup fault tests; MSI/crash VM tests |
| SW6 | Background reads never postpone password inactivity locking | Service activity tracking | Fake-clock background-read regression |
| SW7 | All activation entry points reject TinyWall coexistence | Shared activation guard, installer, program | Shared-guard tests and caller checks; elevated-controller VM case |
| SW8 | LocalSystem cannot be registered from a user-writable payload tree | Installation path/ACL checks | Pure path/trust decisions and source wiring; VM ACL cases |
| SW9 | MSI privileged cleanup is noninteractive and failure-aware | Program/Doctor/MSI custom actions | Installer contract checks; silent/full-UI rollback VM cases |
| SW10 | Unsupported repair and in-place upgrade fail before side effects | MSI launch/execute conditions | XML contract tests; real MSI rejection tests |
| SW11 | Pipe server is authenticated before any credential/request is sent | Pipe client/server identity and self-tests | Pure identity decisions and hostile-server synthetic pipe test |
| SW12 | Controller reconciles, expires and bounds prompts | Display coordinator/popup | Fake-clock expiry, replaced snapshot, overflow and paused UI tests |
| SW13 | Remote AI requires HTTPS; actual payload matches disclosure | AI settings, transport and UI | URI/redirect/payload regressions; no paid/network call needed |
| SW14 | Cleanup affects only owned compatibility rules and restores prior notifications | WindowsFirewall ownership and durable journal | Ownership/journal/failure tests; mixed-profile/crash VM cases |

## Execution order and ownership

1. Three isolated implementation areas run concurrently: enforcement/service; lifecycle/MSI; controller/IPC/AI. Each has exclusive files and its own regression suite. Shared test-runner integration belongs to the manager.
2. Integrate interfaces and regression suites, compile the complete .NET Framework application, and update security/recovery documentation. No executor writes files during its independent audit.
3. Audit the combined diff with fresh independent context, repair findings in bounded rounds, rerun affected checks, and run a fresh final audit against the whole acceptance table.

## Deliberate behavior choices

The persistent baseline contains restrictive filters. Runtime allowances belong to a dynamic WFP session and disappear when that session ends. This favors blocking traffic on service failure, including traffic previously allowed by saved exceptions, until the service restores policy. Baseline priority must remain below the live default-block filters in the same sublayer so promptable runtime IDs still identify normal drops.

The current MSI cannot safely restore a removed prior version. This change rejects repair and in-place upgrades before mutation. Installing a later release requires explicit uninstall followed by a fresh installation until a separately tested transactional upgrade design exists. That limitation must be visible in release/recovery documentation.

Direct installation must use a protected payload directory; extracting a bundle to Downloads does not authorize running a LocalSystem service from that directory. MSI cleanup is a distinct privileged operation with no dialog in the system context. Ordinary interactive removal retains its user-facing confirmation path.

No automatic update feed, signing key, destination-specific grant feature, or general redesign of application identity is added. Existing grant scope and claimed publisher information must be stated accurately. Unresolved guest/VPN/IPv6 compatibility, service-attribution races, and real Windows recovery behavior remain explicit validation cases.

## Commands and release gate

Run the dependency-free core tests and installer contract checks, then use full Visual Studio MSBuild for Debug and Release (`dotnet build` cannot resolve the COM references). Run the isolated protocol and pipe self-tests only, using unique test pipes. Inspect/build MSI packages if the pinned WiX toolset is available. Never run `/install`, `/service`, `/selfhosted`, a real uninstall, or privileged VM validation on the development host.

Record exact commands, exit codes, source/diff fingerprints and audit verdicts in the long-horizon evidence directory. Any unavailable check must remain unavailable. A successful source hardening pass is not approval to replace TinyWall without the VM release gate.
