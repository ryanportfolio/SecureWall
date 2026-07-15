# PromptWall

PromptWall is a Windows default-deny firewall derived from TinyWall. It keeps TinyWall's service-owned Windows Filtering Platform enforcement and adds a tightly scoped, bottom-right prompt when a previously unknown application or service is blocked while attempting an outbound connection.

The tray menu's **Network Activity** window refreshes every second and separates observed WFP decisions (`Allowed` / `Blocked`), exact TCP transport states, and local listening endpoints. `Listening (local endpoint)` deliberately does not claim that a port is externally reachable.

PromptWall is early development software. Do not install it on a machine you cannot recover locally. A firewall defect can interrupt networking or weaken host isolation.

## Intended prompt behavior

- Normal mode blocks inbound and outbound traffic unless an explicit exception applies.
- Only outbound drops caused by PromptWall's default-block filters may produce prompts.
- **Allow outgoing** creates a permanent outbound TCP/UDP exception for the service-owned subject represented by the prompt token.
- **Ignore**, closing the prompt, or prompt expiry leaves policy unchanged.
- Explicit user blocks, blocklists, inbound drops, and ambiguous shared-service identities are never converted into broad allow rules.

The implementation and verification plan is in [docs/superpowers/plans/2026-07-14-promptwall.md](docs/superpowers/plans/2026-07-14-promptwall.md). The threat model and architecture are in [docs/design/2026-07-14-promptwall-design.md](docs/design/2026-07-14-promptwall-design.md).

## Build and test

PromptWall targets .NET Framework 4.8 and must be compiled with full Visual Studio MSBuild because it uses COM references. Run the dependency-free prompt/security harness first:

```powershell
dotnet run --project tests\PromptWall.Core.Tests\PromptWall.Core.Tests.csproj
& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" TinyWall\TinyWall.csproj /t:Build /p:Configuration=Debug /p:RestorePackages=false
```

The debug-only `/protocolselftest`, `/pipeintegrationtest`, and `/promptpreview` switches verify IPC serialization, authenticated pipe exchange, and the synthetic popup without starting the service or changing WFP. WiX 3 is needed to build the MSI; run `MsiSetup\PrepareSources.ps1` after the application build. `tools\vm\Prepare-PromptWallVmBundle.ps1` packages the guarded real-firewall validation runner for an expendable Windows VM. Full commands and the privileged VM matrix are in [docs/TESTING.md](docs/TESTING.md). Security and recovery rules are in [docs/SECURITY.md](docs/SECURITY.md).

## Lineage and license

PromptWall is based on TinyWall 3.5.1, pinned to upstream commit `1df71b146d01d734d5b5a45a814b29e6a073f4d0`. See [UPSTREAM.md](UPSTREAM.md) for provenance and modification notes.

The firewall application and this combined work are licensed under GNU GPL version 3; see [LICENSE](LICENSE). Starter automation retained in `.claude/`, `.agents/`, and `.codex/` remains under its separately preserved terms in [LICENSES/STARTER-MIT.txt](LICENSES/STARTER-MIT.txt) and any per-skill notices.

PromptWall is not TinyWall and is not endorsed by TinyWall's author.
