# Commands

Run from repository root in PowerShell.

## Pure tests

```powershell
dotnet run --project tests\SecureWall.Core.Tests\SecureWall.Core.Tests.csproj
```

## Restore

```powershell
dotnet restore TinyWall\TinyWall.csproj
```

## Production build

`dotnet build` cannot resolve this .NET Framework project's COM references. Locate Visual Studio Build Tools MSBuild with `vswhere`, then build:

```powershell
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
& $msbuild TinyWall\TinyWall.csproj /t:Build /p:Configuration=Debug /p:RestorePackages=false /v:minimal
```

## Safe debug verification

```powershell
$exe = Resolve-Path TinyWall\bin\Debug\SecureWall.exe
(Start-Process $exe -ArgumentList '/protocolselftest' -Wait -PassThru).ExitCode
(Start-Process $exe -ArgumentList '/pipeintegrationtest' -Wait -PassThru).ExitCode
& $exe /promptpreview
```

`/pipeintegrationtest` exercises the real authenticated named-pipe server and controller without starting the service or WFP. Run it under a normal interactive desktop token; a restricted automation token can be rejected by the pipe ACL and return `1`. `/promptpreview` is visible and interactive but does not start the service or register filters. Do not run `/install`, `/service`, or `/selfhosted` without the explicit VM/local-console approval described in `docs/TESTING.md`.

## Isolated-VM bundle

After Release builds of SecureWall and the dependency-free network probe:

```powershell
dotnet build tests\SecureWall.NetworkProbe\SecureWall.NetworkProbe.csproj --configuration Release
& .\tools\vm\Prepare-SecureWallVmBundle.ps1 -Configuration Release
```

Move the emitted zip to an expendable snapshotted Windows VM. Follow `tools\vm\README.md`; never run the privileged validation runner on the development host.

## MSI packages

Use the official WiX 3.14.1 portable binaries. After the Release application build and `MsiSetup\PrepareSources.ps1`, point the WiX project at the extracted tool directory and rebuild each platform with Visual Studio MSBuild. Full ICE validation requires a normal desktop token.

```powershell
$wixRoot = (Resolve-Path '.tmp\tools\wix314').Path
foreach ($platform in 'x86', 'x64', 'arm64') {
    & $msbuild MsiSetup\MsiSetup.wixproj /t:Rebuild /p:Configuration=Release /p:Platform=$platform /p:WixTargetsPath="$wixRoot\wix.targets" /p:WixInstallPath="$wixRoot" /p:WixToolPath="$wixRoot" /p:WixExtDir="$wixRoot" /p:WixTasksPath="$wixRoot\WixTasks.dll" /p:DefineSolutionProperties=false /v:minimal
}
```

Expected output: `MsiSetup\bin\Release\SecureWall_x86.msi`, `SecureWall_x64.msi`, and `SecureWall_arm64.msi`, with no ICE warnings or errors.

## Manual acceptance bundle

After Release builds of SecureWall and the network probe:

```powershell
& .\tools\manual\Prepare-SecureWallManualTestBundle.ps1 -Configuration Release
```

The emitted ignored zip contains all three unsigned MSIs, the unpackaged app, distinct Allow/Ignore probes, hashes, and a short physical/spare-PC test and recovery guide. Building or extracting it does not install SecureWall or touch WFP.
