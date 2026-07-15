# SecureWall Installer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish reproducible, unsigned SecureWall v0.1.1 Windows MSI installers that install the service and tray controller, start them through the existing guarded lifecycle, upgrade/uninstall cleanly, and refuse to activate while TinyWall is installed.

**Architecture:** Keep the existing WiX 3 per-machine MSI and application-owned `/install`/`/uninstall` lifecycle. Rebrand all active runtime and installer identities from PromptWall to SecureWall while preserving TinyWall source namespaces and upstream provenance. Add a PowerShell package contract test and release builder, then have GitHub Actions build the same x86, x64, and ARM64 artifacts and attach them only to a matching prerelease tag.

**Tech Stack:** C# 9, .NET Framework 4.8, Visual Studio MSBuild, WiX Toolset 3.14.1, PowerShell 5.1, GitHub Actions.

---

### Task 1: Lock the installer contract with a failing test

**Files:**
- Create: `tests/installer/Test-SecureWallInstaller.ps1`
- Modify: `.github/workflows/validate-template.yml`

- [ ] **Step 1: Write the failing source/package contract**

Create a PowerShell 5.1 test that requires `SecureWall.exe`, `SecureWall_x86.msi`, `SecureWall_x64.msi`, `SecureWall_arm64.msi`, SecureWall MSI product/path/service/task identities, TinyWall conflict detection, per-machine scope, and absence of active PromptWall identities in shipping source. Accept an optional `-ArtifactsDirectory` and inspect MSI metadata through `WindowsInstaller.Installer` when artifacts exist.

- [ ] **Step 2: Run test to verify RED**

Run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\installer\Test-SecureWallInstaller.ps1
```

Expected: failure identifying current `PromptWall` assembly/MSI identity and missing release builder.

- [ ] **Step 3: Add the test to CI**

Replace starter-only CI with Windows jobs that run the pure test harness, source contract, PowerShell parse checks, Release application build, and installer build. Keep Codex adapter and JSON validation checks where relevant.

### Task 2: Rebrand active product identities

**Files:**
- Rename: `TinyWall/PromptWallProduct.cs` to `TinyWall/SecureWallProduct.cs`
- Modify: `TinyWall/TinyWall.csproj`
- Modify: active C# and `.resx` resources under `TinyWall/`
- Modify: `MsiSetup/Product.wxs`
- Modify: `MsiSetup/MsiSetup.wixproj`
- Modify: `MsiSetup/PrepareSources.ps1`
- Rename: `MsiSetup/Sources/ProgramFiles/PromptWall` to `MsiSetup/Sources/ProgramFiles/SecureWall`
- Rename: `MsiSetup/Sources/CommonAppData/PromptWall` to `MsiSetup/Sources/CommonAppData/SecureWall`
- Modify: `README.md`, `docs/SECURITY.md`, `docs/TESTING.md`, `.claude/reference/*.md`

- [ ] **Step 1: Change product constants and assembly metadata**

Use one product class:

```csharp
internal static class SecureWallProduct
{
    internal const string Name = "SecureWall";
    internal const string AppDataFolderName = "SecureWall";
    internal const string ControllerPipeName = "SecureWallController";
    internal const string ServiceMutexName = @"Global\SecureWallService";
    internal const bool UpdateFeedEnabled = false;
}
```

Set assembly/product/title/output to `SecureWall` and version to `0.1.1`.

- [ ] **Step 2: Change active machine identities**

Use `SecureWall` for service, scheduled task, named pipe, mutex, Program Files, ProgramData, registry, Add/Remove Programs, WFP display strings, and installer filenames. Preserve existing SecureWall-specific GUIDs so this remains the same product lineage and uninstall code can remove its own objects.

- [ ] **Step 3: Change user-visible resources and docs**

Replace active PromptWall branding with SecureWall. Keep historical file/directory names only when renaming would create needless source churn; keep upstream TinyWall attribution intact.

- [ ] **Step 4: Run source contract to verify GREEN**

Run the Task 1 command. Expected: source checks pass; artifact checks skip because no artifact directory was supplied.

### Task 3: Add one-command release packaging

**Files:**
- Create: `tools/release/Build-SecureWallRelease.ps1`
- Create: `.github/workflows/release.yml`
- Modify: `.gitignore`
- Modify: `.claude/reference/commands.md`

- [ ] **Step 1: Extend test to require builder behavior**

Require PowerShell 5.1 compatibility, WiX 3.14.1 input, explicit configuration/platform validation, clean artifact output, three MSI files, SHA-256 manifest, and no automatic install/launch command.

- [ ] **Step 2: Run test to verify RED**

Expected: missing `tools/release/Build-SecureWallRelease.ps1`.

- [ ] **Step 3: Implement minimal builder**

Builder locates Visual Studio MSBuild, restores/builds `TinyWall/TinyWall.csproj`, stages runtime files, builds requested platforms with official WiX 3.14.1 portable binaries, copies MSIs to `artifacts/release`, and emits `SHA256SUMS.txt`. It never invokes `msiexec`, `/install`, or the built controller.

- [ ] **Step 4: Add release workflow**

`workflow_dispatch` and `v*` prerelease tags build on `windows-latest`, run all non-enforcing checks, upload an Actions artifact, and attach files to an existing GitHub prerelease only when tag version equals assembly/MSI version. Use least privilege: `contents: read` for CI and `contents: write` only for release upload job.

- [ ] **Step 5: Run source contract to verify GREEN**

Expected: all source and builder checks pass.

### Task 4: Build and inspect release artifacts

**Files:**
- Generated/ignored: `.tmp/tools/wix314/`
- Generated/ignored: `artifacts/release/`

- [ ] **Step 1: Restore and build application**

Run:

```powershell
dotnet restore TinyWall\TinyWall.csproj
& $msbuild TinyWall\TinyWall.csproj /t:Rebuild /p:Configuration=Release /p:RestorePackages=false /v:minimal
```

Expected: `TinyWall/bin/Release/SecureWall.exe`, version `0.1.1.0`, zero errors.

- [ ] **Step 2: Build all MSI architectures**

Run:

```powershell
& .\tools\release\Build-SecureWallRelease.ps1 -WixRoot .tmp\tools\wix314 -Configuration Release -Platforms x86,x64,arm64
```

Expected: three MSIs plus `SHA256SUMS.txt`.

- [ ] **Step 3: Inspect packages without installing**

Run installer test with `-ArtifactsDirectory artifacts\release`. Confirm ProductName, ProductVersion, Manufacturer, per-machine scope, architecture templates, embedded cab, required payloads, TinyWall block condition, install/rollback/uninstall custom actions, and file hashes.

### Task 5: Full non-enforcing verification and handoff

**Files:**
- Modify only if verification finds a defect.

- [ ] **Step 1: Run full automated checks**

Run pure tests (46 expected), Release MSBuild, `/protocolselftest`, `/pipeintegrationtest`, PowerShell 5.1 parse, installer contract, Codex adapter sync check, JSON checks, `git diff --check`, and MSI ICE validation.

- [ ] **Step 2: Adversarially inspect failure paths**

Verify the MSI cannot install over TinyWall, custom-action failure triggers rollback, uninstall invokes service/WFP cleanup, x64 and ARM64 packages mark components 64-bit, and release workflow cannot upload artifacts for a mismatched version/tag.

- [ ] **Step 3: Prepare GitHub integration**

Commit intended files, push `codex/securewall-installer`, and open a PR. Do not move v0.1.0. After explicit merge approval, create v0.1.1 prerelease from the merged commit and attach all three unsigned MSIs plus `SHA256SUMS.txt`.

- [ ] **Step 4: Preserve host firewall state**

Do not install an MSI on this development host. Report that TinyWall must be uninstalled and the machine rebooted before SecureWall installation; real WFP/boot/audit behavior still requires the local-console matrix in `docs/TESTING.md`.
