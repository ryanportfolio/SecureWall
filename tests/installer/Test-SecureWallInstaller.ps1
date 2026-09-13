[CmdletBinding()]
param(
    [string]$ArtifactsDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:Failures = New-Object System.Collections.Generic.List[string]
$script:PassCount = 0

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if ($Condition) {
        $script:PassCount++
        Write-Host "PASS $Message"
    }
    else {
        $script:Failures.Add($Message)
        Write-Host "FAIL $Message"
    }
}

function Read-RepoFile {
    param([string]$RelativePath)

    $path = Join-Path $repoRoot $RelativePath
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "exists: $RelativePath"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return ''
    }

    return [System.IO.File]::ReadAllText($path)
}

function Get-MsiProperty {
    param(
        [__ComObject]$Installer,
        [string]$MsiPath,
        [string]$PropertyName
    )

    $database = $Installer.OpenDatabase($MsiPath, 0)
    $view = $database.OpenView("SELECT `Value` FROM `Property` WHERE `Property`='$PropertyName'")
    $null = $view.Execute()
    $record = $view.Fetch()
    if ($null -eq $record) {
        return $null
    }

    $value = $record.StringData(1)
    return [string]$value
}

$project = Read-RepoFile 'TinyWall\TinyWall.csproj'
Assert-True ($project -match '<AssemblyName>SecureWall</AssemblyName>') 'application assembly is SecureWall.exe'
Assert-True ($project -match '<Product>SecureWall</Product>') 'application product metadata is SecureWall'
Assert-True ($project -match '<AssemblyTitle>SecureWall</AssemblyTitle>') 'application title is SecureWall'
Assert-True ($project -match '<Version>0\.2\.0</Version>') 'application version is 0.2.0'

$product = Read-RepoFile 'MsiSetup\Product.wxs'
Assert-True ($product -match '<\?define ProductName="SecureWall" \?>') 'MSI product name is SecureWall'
Assert-True ($product -match 'InstallScope="perMachine"') 'MSI installs per machine'
Assert-True ($product -match 'Name="SecureWall"') 'MSI installs under SecureWall directories'
Assert-True ($product -match 'SecureWall\.exe') 'MSI carries SecureWall.exe'
Assert-True ($product -match 'Software\\SecureWall') 'MSI writes SecureWall registry identity'
Assert-True ($product -match 'TINYWALLINSTALLDIR32') 'MSI detects 32-bit TinyWall installation'
Assert-True ($product -match 'TINYWALLINSTALLDIR64') 'MSI detects 64-bit TinyWall installation'
Assert-True ($product -match "ExeCommand='/install'") 'MSI invokes guarded application install lifecycle'
Assert-True ($product -match "ExeCommand='/msi-cleanup'") 'MSI invokes explicit noninteractive maintenance cleanup'
Assert-True ($product -match "Execute='rollback'") 'MSI has rollback cleanup action'

[xml]$wix = $product
$ns = New-Object System.Xml.XmlNamespaceManager($wix.NameTable)
$ns.AddNamespace('w', 'http://schemas.microsoft.com/wix/2006/wi')
$installAction = $wix.SelectSingleNode('//w:CustomAction[@Id="InstallCustom"]', $ns)
$installRollback = $wix.SelectSingleNode('//w:CustomAction[@Id="InstallCustomRollback"]', $ns)
$removeAction = $wix.SelectSingleNode('//w:CustomAction[@Id="UninstallCustom"]', $ns)
$removeSequence = $wix.SelectSingleNode('//w:InstallExecuteSequence/w:Custom[@Action="UninstallCustom"]', $ns)
Assert-True ($installAction.Execute -eq 'deferred' -and $installAction.Return -eq 'check') 'first install executes inside transaction with checked failures'
Assert-True ($installRollback.Execute -eq 'rollback' -and $installRollback.Impersonate -eq 'no' -and $installRollback.ExeCommand -eq '/msi-rollback-install') 'failed first install has a distinct SYSTEM rollback entry'
Assert-True ($removeAction.Impersonate -eq 'no' -and $removeAction.ExeCommand -eq '/msi-cleanup') 'MSI cleanup uses LocalSystem noninteractive entry'
Assert-True ($removeSequence.InnerText -eq 'Installed AND REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE') 'teardown only runs on explicit full removal'
Assert-True ($removeSequence.Before -eq 'UnpublishFeatures') 'cleanup precedes both registry journal removal and file removal'
Assert-True ($null -eq $wix.SelectSingleNode('//w:RemoveExistingProducts', $ns)) 'automatic old-product teardown is absent'
Assert-True ($product.Contains('NOT Installed OR (REMOVE="ALL" AND NOT REINSTALL)')) 'launch condition rejects repair before transaction'
Assert-True ($product.Contains('Installed OR (NOT OLDER_UPGRADEABLE_FOUND AND NOT SELF_FOUND')) 'launch condition rejects upgrades and equal-version replacement'
Assert-True ($product.Contains('NOT EXISTINGSECUREWALLSERVICE AND SECUREWALLDELETEPENDING <> "#1"')) 'fresh MSI rejects existing and delete-pending service registrations'
Assert-True ($product -match 'Name="DeleteFlag" Type="raw"' -and $product.Contains('SECUREWALLDELETEPENDING <> "#1"')) 'MSI recognizes raw DWORD delete-pending flag and requires completed deletion before reinstall'
Assert-True ($product.Contains('Close Services and other service-management tools, or restart Windows')) 'pending service deletion has an actionable installer message'
Assert-True ($product.Contains('NOT RollbackDisabled')) 'launch condition rejects disabled rollback'
Assert-True ($product -match "Id='UninstallCustomRollback'.*ExeCommand='/install'.*Execute='rollback'") 'failed full removal rebuilds saved protection after file rollback'

$doctor = Read-RepoFile 'TinyWall\TinyWallDoctor.cs'
$safety = Read-RepoFile 'TinyWall\Installer\InstallationSafety.cs'
$managedInstaller = Read-RepoFile 'TinyWall\Installer\TinyWallServiceInstaller.cs'
$firewall = Read-RepoFile 'TinyWall\WindowsFirewall.cs'
$program = Read-RepoFile 'TinyWall\Program.cs'
Assert-True ($program.Contains('RollbackFailedInstallForMsi()') -and $program.Contains('/msi-rollback-install')) 'Program routes failed-install rollback to dedicated lifecycle'
Assert-True ($doctor.Contains('UninstallForMsi() => CleanupForMsi(false)') -and $doctor.Contains('RollbackFailedInstallForMsi() => CleanupForMsi(true)')) 'only failed-install rollback enables forced service recovery'
Assert-True ($doctor.Contains('ServiceLifecyclePolicy.StartupTimeout')) 'activation uses shared startup allowance'
Assert-True ($safety -match '(?s)using var process = OpenProcess.*?IsRollbackProcess.*?SetStartupMode.*?TerminateProcess\(process.*?WaitForSingleObject\(process') 'rollback authenticates and retains exact process handle through restart suppression and exit wait'
Assert-True ($firewall -match '(?s)AcquireForRules\(ReadRuleIdentities\(policy\).*?OpenRecoveryKey\(\).*?NotificationsDisabled\[profile\] = true') 'production collection acquisition preflights foreign names before journal or firewall mutation'
Assert-True ($doctor -match '(?s)EnsureServiceInstalledAndRunning.*?RequireNoTinyWall\(\).*?IsServiceRunning') 'shared elevated activation checks TinyWall before running-service shortcut'
Assert-True ($managedInstaller -match '(?s)void Install\(.*?RequireNoTinyWall\(\).*?RequireProtectedInstallation\(\).*?base.Install') 'managed installer validates coexistence and protected paths before registration'
Assert-True ($safety -match 'identity.IsSystem') 'MSI maintenance explicitly checks LocalSystem token'
Assert-True ($safety -match 'ReparsePoint' -and $safety -match 'GetOwner' -and $safety -match 'GetAccessRules') 'privileged install checks reparse points, ownership, and write ACLs'
Assert-True ($safety -match 'CheckTree\(entry\)' -and $safety -match 'SpecialFolder.ProgramFiles') 'privileged install validates Program Files and all dependencies recursively'
Assert-True ($doctor.IndexOf('WindowsFirewall.RestoreOwnedState();') -lt $doctor.IndexOf('TinyWallServer.DeleteWfpObjects')) 'crash cleanup restores compatibility before removing protective WFP objects'
Assert-True ($firewall -notmatch 'Contains\(SecureWallProduct.Name\)') 'firewall cleanup does not delete product-substring matches'
Assert-True ($firewall -match 'journal.Flush\(\)' -and $firewall -match 'RegistryView.Registry64') 'notification recovery is durable and architecture-independent'
Assert-True ($doctor -match '(?s)RequireServiceNotPendingDeletion\(\).*?WindowsFirewall.RequireServiceRunning\(\).*?InstallHelper') 'activation rejects pending deletion and unavailable Windows Firewall before registration'
Assert-True ($firewall -match '(?s)existingJournal\?\.GetValue\(NotificationValue\).*?INetFwPolicy2 policy = GetFwPolicy2') 'stopped-service recovery checks durable compatibility ownership before COM access'
Assert-True ($firewall.Contains('service.Status == ServiceControllerStatus.Stopped') -and $firewall.Contains('CanSkipStoppedServiceRecovery')) 'only confirmed stopped Windows Firewall can use no-journal recovery skip'
Assert-True ($safety -match '(?s)if \(process.IsInvalid\).*?RequireStoppedAfterProcessOpenFailure.*?ReadStatus\(service\)') 'rollback rechecks stopped SCM state after process-open failure'
Assert-True ($doctor -match '(?s)InstallHelper\(new string\[\] \{ "/u".*?EnsureStoppedServiceDeletion\(\)') 'managed uninstall is followed by a checked native deletion postcondition'
Assert-True ($safety -match '(?s)EnsureStoppedServiceDeletion.*?ReadStatus\(service\).CurrentState != 1.*?if \(!DeleteService\(service\)\).*?error != 1072') 'native deletion requires stopped state and rejects errors other than already-marked deletion'
Assert-True ($doctor.Contains('return succeeded ? 0 : -1;') -and $doctor.Contains('service deletion was accepted by Windows')) 'accepted deferred service deletion permits successful teardown and reports pending handles'

$installUi = Read-RepoFile 'MsiSetup\WixUI_InstallDir_Custom.wxs'
[xml]$uiXml = $installUi
$uiNs = New-Object System.Xml.XmlNamespaceManager($uiXml.NameTable)
$uiNs.AddNamespace('w', 'http://schemas.microsoft.com/wix/2006/wi')
Assert-True ($null -eq $uiXml.SelectSingleNode('//w:Publish[@Dialog="InstallDirDlg" or @Value="InstallDirDlg" or @Value="BrowseDlg"]', $uiNs)) 'full install UI offers no unsupported directory chooser'
Assert-True ($null -ne $uiXml.SelectSingleNode('//w:Publish[@Dialog="LicenseAgreementDlg" and @Control="Next" and @Value="VerifyReadyDlg"]', $uiNs)) 'license acceptance advances directly to installation confirmation'
Assert-True ($null -ne $uiXml.SelectSingleNode('//w:Publish[@Dialog="VerifyReadyDlg" and @Control="Back" and @Value="LicenseAgreementDlg"]', $uiNs)) 'installation confirmation returns to the license dialog'

$installerProject = Read-RepoFile 'MsiSetup\MsiSetup.wixproj'
Assert-True ($installerProject -match '<OutputName>SecureWall</OutputName>') 'MSI filenames use SecureWall'

$prepareSources = Read-RepoFile 'MsiSetup\PrepareSources.ps1'
Assert-True ($prepareSources -match "'SecureWall\.exe'") 'staging requires SecureWall.exe'
Assert-True ($prepareSources -match 'ProgramFiles\\SecureWall') 'staging targets SecureWall payload directory'

# Defaults are MSI payload under Program Files. Only the guarded SYSTEM entry
# may seed machine data; static source checks do not replace hostile-path VM tests.
$defaultsDir = $wix.SelectSingleNode('//w:Directory[@Id="INSTALLDIR"]/w:Directory[@Id="DataDefaultsDir"]', $ns)
Assert-True ($null -ne $defaultsDir -and $defaultsDir.Name -eq 'data-defaults') 'defaults install beneath protected INSTALLDIR/data-defaults'
Assert-True ($null -eq $wix.SelectSingleNode('//w:Directory[@Id="CommonAppDataFolder"] | //w:DirectoryRef[@Id="CommonAppDataFolder"]', $ns)) 'MSI has no ProgramData destination tree'
Assert-True ($product -notmatch '\[CommonAppDataFolder\]|\[CommonAppData\]|Name=["'']ProgramData["'']') 'MSI has no alternate ProgramData destination'
Assert-True ($null -eq $wix.SelectSingleNode('//w:RemoveFile | //w:CopyFile | //w:MoveFile | //w:Directory[@Id="INSTALLDIR"]//w:CreateFolder | //w:Directory[@Id="INSTALLDIR"]//w:RemoveFolder', $ns)) 'MSI has no data copy/removal or early payload-directory creation operations'
foreach ($default in @(@{ Id = 'DatabaseJson'; Name = 'profiles.json' }, @{ Id = 'HostsBCK'; Name = 'hosts.bck' })) {
    $file = $wix.SelectSingleNode("//w:Directory[@Id='DataDefaultsDir']/w:Component/w:File[@Id='$($default.Id)']", $ns)
    Assert-True ($null -ne $file -and $file.Source -eq "Sources\CommonAppData\SecureWall\$($default.Name)" -and $file.Vital -eq 'yes') "protected default payload: $($default.Name)"
    Assert-True (Test-Path -LiteralPath (Join-Path $repoRoot "MsiSetup\Sources\CommonAppData\SecureWall\$($default.Name)") -PathType Leaf) "default source exists: $($default.Name)"
    if ($null -ne $file) {
        Assert-True ($null -ne $wix.SelectSingleNode("//w:Feature/w:ComponentRef[@Id='$($file.ParentNode.Id)']", $ns)) "default is selected by MSI feature: $($default.Name)"
    }
}
$machineData = Read-RepoFile 'TinyWall\Installer\MachineDataGuard.cs'
$machinePolicy = Read-RepoFile 'TinyWall\Prompting\MachineDataPolicy.cs'
$utils = Read-RepoFile 'TinyWall\Utils.cs'
Assert-True ($installAction.Impersonate -eq 'no') 'default seeding install action runs as SYSTEM'
Assert-True ($safety -match '(?s)void RequireSystemMaintenance\(\).*?if \(!identity.IsSystem\).*?throw new UnauthorizedAccessException.*?RequireProtectedInstallation\(\);') 'SYSTEM maintenance validates protected default source tree before seeding'
Assert-True ($program -match '(?s)static int Main\(string\[\] args\).*?RequireSystemMaintenance\(\);\s*Installer.MachineDataGuard.InstallDefaults\(\);.*?else\s*Installer.MachineDataGuard.Require\(\);.*?HierarchicalStopwatch.Enable') 'release entry validates or seeds machine data before timing and logging access'
$mainStart = $program.IndexOf('static int Main(string[] args)')
$guardStart = $program.IndexOf('Installer.InstallationSafety.RequireSystemMaintenance();', $mainStart)
$beforeGuard = if ($guardStart -gt $mainStart) { $program.Substring($mainStart, $guardStart - $mainStart) } else { $program }
Assert-True ($beforeGuard -notmatch 'Utils\.(AppDataPath|LogException)|File\.(Write|Copy|Move|Delete|Create)|Directory\.Create|HierarchicalStopwatch\.') 'entry has no machine-data IO before guard'
$guardCatchStart = $program.IndexOf('catch (Exception exception)', $guardStart)
$guardCatchEnd = $program.IndexOf('#endif', $guardCatchStart)
$guardCatch = if ($guardCatchStart -ge 0 -and $guardCatchEnd -gt $guardCatchStart) { $program.Substring($guardCatchStart, $guardCatchEnd - $guardCatchStart) } else { '' }
Assert-True ($guardCatch -match 'Console\.Error\.WriteLine\(diagnostic\)' -and $guardCatch -match 'return -1;' -and $guardCatch -match 'MachineDataRecoveryMessage') 'guard rejection explains recovery on stderr and returns failure'
Assert-True ($guardCatch -notmatch 'Utils\.(AppDataPath|Log|LogException)\b|File\.(Write|Copy|Move|Delete|Create)|Directory\.Create') 'guard rejection performs no file logging or machine-data writes'
Assert-True ($guardCatch -match 'if \(!maintenance\) Utils.ShowControllerFailure\(diagnostic\);' -and @('/install', '/uninstall', '/msi-cleanup', '/msi-rollback-install', '/service').Where({ -not $guardCatch.Contains('"' + $_ + '"') }).Count -eq 0) 'guard failure dialog excludes every maintenance and service entry mode'
Assert-True ($utils -match '(?s)SpecialFolder.CommonApplicationData.*?MachineDataGuard.Require\(\);\s*return dir;') 'production AppDataPath access enters shared guard'
Assert-True ($machineData -match '(?s)void InstallDefaults\(\)\s*\{\s*Require\(true, true\);.*?"data-defaults".*?new\[\] \{ "profiles.json", "hosts.bck" \}.*?Require\(\);.*?if \(!File.Exists\(target\)\) File.Copy\(Path.Combine\(source, name\), target, false\);.*?Require\(false, true\);') 'seeding validates full tree first, copies exactly two absent defaults without overwrite, then revalidates'
Assert-True ($machineData -match 'identity.IsSystem' -and $machineData -match 'Directory.CreateDirectory\(path, acl\)' -and $machineData -match 'O:SYG:SYD:P') 'missing data directory is created with protected ACL by SYSTEM'
Assert-True ($machineData -match 'MachineDataPolicy.CheckTree' -and $machineData -match 'ReparsePoint' -and $machineData -match 'raw.Owner' -and $machineData -match 'raw.DiscretionaryAcl') 'machine-data guard recursively checks reparse state, ownership and DACL'
Assert-True ($machinePolicy -match '(?s)verifyAncestors\(\);.*?if \(!exists\(\)\).*?if \(!allowCreation\).*?createProtected\(\);.*?verifyTree\(\);') 'ancestors precede creation and existing trees are validated without repair'

# Parse the declared staging lists without executing staging or requiring build artifacts.
$requiredMatch = [regex]::Match($prepareSources, '(?s)\$requiredFiles\s*=\s*@\((.*?)\)')
$requiredRuntime = @([regex]::Matches($requiredMatch.Groups[1].Value, "'([^']+)'" ) | ForEach-Object { $_.Groups[1].Value })
Assert-True ($requiredRuntime.Count -ge 11 -and $requiredRuntime -contains 'System.IO.Pipelines.dll') 'staging declares complete known runtime including pipelines'
foreach ($runtimeName in $requiredRuntime) {
    Assert-True ($null -ne $wix.SelectSingleNode("//w:File[@Source='Sources\ProgramFiles\SecureWall\$runtimeName']", $ns)) "MSI declares staged runtime: $runtimeName"
}
$cultureMatch = [regex]::Match($prepareSources, '(?s)\$cultures\s*=\s*@\((.*?)\)')
$cultures = @([regex]::Matches($cultureMatch.Groups[1].Value, "'([^']+)'" ) | ForEach-Object { $_.Groups[1].Value })
Assert-True ($cultures.Count -eq 17) 'staging declares all 17 localization satellites'
foreach ($culture in $cultures) {
    Assert-True ($null -ne $wix.SelectSingleNode("//w:File[@Source='Sources\ProgramFiles\SecureWall\$culture\SecureWall.resources.dll']", $ns)) "MSI declares staged satellite: $culture"
}

$sourceUpdate = (Read-RepoFile 'TinyWall\Database\SpecialApplications\Special Windows Update.json') | ConvertFrom-Json
$packagedDatabase = (Read-RepoFile 'MsiSetup\Sources\CommonAppData\SecureWall\profiles.json') | ConvertFrom-Json
$packagedUpdate = @($packagedDatabase.KnownApplications | Where-Object { $_.Name -eq 'Windows_Update' })
Assert-True ($sourceUpdate.Name -eq 'Windows_Update' -and $packagedUpdate.Count -eq 1) 'source and payload each identify Windows_Update'
foreach ($entry in @(@{ Name = 'source'; Rules = @($sourceUpdate.Components) }, @{ Name = 'payload'; Rules = @($packagedUpdate | ForEach-Object { $_.Components }) })) {
    # Any executable subject in this service-only profile is a regression, even
    # if an allow changes policy type or uses different ports in the future.
    $executableRules = @($entry.Rules | Where-Object { $_.Subject.SubjectType -eq 2 })
    Assert-True ($executableRules.Count -eq 0) "$($entry.Name) Windows_Update has no executable-wide rule"
    $serviceRules = @($entry.Rules | Where-Object {
        $_.Subject.SubjectType -eq 3 -and $_.Subject.ServiceName -eq 'wuauserv' -and
        $_.Subject.ExecutablePath -eq '{folder:sys32}\svchost.exe' -and
        $_.Policy.PolicyType -eq 3 -and $_.Policy.LocalNetworkOnly -eq $false -and
        $_.Policy.AllowedRemoteTcpConnectPorts -eq '*' -and
        -not $_.Policy.AllowedLocalTcpListenerPorts -and -not $_.Policy.AllowedLocalUdpListenerPorts
    })
    Assert-True ($serviceRules.Count -eq 1) "$($entry.Name) Windows_Update retains intended wuauserv outbound TCP service rule"
}

$productConstants = Read-RepoFile 'TinyWall\SecureWallProduct.cs'
Assert-True ($productConstants -match 'internal const string Name = "SecureWall"') 'runtime product name is SecureWall'
Assert-True ($productConstants -match 'ControllerPipeName = "SecureWallController"') 'named pipe identity is SecureWall'
Assert-True ($productConstants -match 'ServiceMutexName = @"Global\\SecureWallService"') 'service mutex identity is SecureWall'

$service = Read-RepoFile 'TinyWall\TinyWallService.cs'
Assert-True ($service -match 'SERVICE_NAME = "SecureWall"') 'Windows service identity is SecureWall'
Assert-True ($service -match 'TinyWall') 'TinyWall conflict and upstream compatibility code remains present'
Assert-True ($service -match '(?s)ServiceDependencies.*?"MpsSvc"') 'SCM starts Windows Firewall before the SecureWall service'
Assert-True ($doctor -match '(?s)string.Equals\(srv, "MpsSvc".*?continue;.*?ServiceStartMode.Disabled') 'dependency repair preserves explicitly disabled Windows Firewall setting'

$activeExtensions = @('.cs', '.resx', '.csproj', '.wxs', '.wixproj', '.ps1', '.md', '.txt', '.html', '.config', '.manifest')
$oldBrandFiles = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'TinyWall'), (Join-Path $repoRoot 'MsiSetup'), (Join-Path $repoRoot 'tools'), (Join-Path $repoRoot 'tests') -Recurse -File |
    Where-Object { $_.FullName -ne $PSCommandPath } |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Where-Object { $activeExtensions -contains $_.Extension } |
    Where-Object { [System.IO.File]::ReadAllText($_.FullName) -match 'PromptWall' }
Assert-True (($oldBrandFiles | Measure-Object).Count -eq 0) 'active source contains no PromptWall identities'

$builder = Read-RepoFile 'tools\release\Build-SecureWallRelease.ps1'
Assert-True ($builder -match 'ValidateSet\(''x86'', ''x64'', ''arm64''\)') 'release builder validates supported architectures'
Assert-True ($builder -match 'SHA256SUMS\.txt') 'release builder emits SHA-256 manifest'
Assert-True ($builder -match '\[switch\]\$SuppressValidation') 'release builder exposes explicit sandbox ICE bypass'
Assert-True ($builder -notmatch '(?im)^\s*(Start-Process\s+msiexec|&\s*msiexec|msiexec\.exe)') 'release builder never installs generated MSI files'

$releaseWorkflow = Read-RepoFile '.github\workflows\release.yml'
Assert-True ($releaseWorkflow -match 'contents:\s*write') 'release workflow grants upload job write access'
Assert-True ($releaseWorkflow -match 'Build-SecureWallRelease\.ps1') 'release workflow uses repository release builder'
Assert-True ($releaseWorkflow -match 'SHA256SUMS\.txt') 'release workflow publishes hash manifest'
Assert-True ($releaseWorkflow -notmatch 'SuppressValidation') 'release workflow never bypasses MSI ICE validation'

if ($ArtifactsDirectory) {
    $artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $ArtifactsDirectory))
    $expectedMsi = @(
        'SecureWall_x86.msi',
        'SecureWall_x64.msi',
        'SecureWall_arm64.msi'
    )

    foreach ($name in $expectedMsi) {
        Assert-True (Test-Path -LiteralPath (Join-Path $artifactRoot $name) -PathType Leaf) "artifact exists: $name"
    }

    $hashManifest = Join-Path $artifactRoot 'SHA256SUMS.txt'
    Assert-True (Test-Path -LiteralPath $hashManifest -PathType Leaf) 'artifact exists: SHA256SUMS.txt'

    if ((Test-Path -LiteralPath $hashManifest -PathType Leaf) -and
        (($expectedMsi | Where-Object { -not (Test-Path -LiteralPath (Join-Path $artifactRoot $_) -PathType Leaf) }).Count -eq 0)) {
        $manifestText = [System.IO.File]::ReadAllText($hashManifest)
        foreach ($name in $expectedMsi) {
            $hash = (Get-FileHash -LiteralPath (Join-Path $artifactRoot $name) -Algorithm SHA256).Hash.ToLowerInvariant()
            Assert-True ($manifestText -match ([regex]::Escape("$hash  $name"))) "hash manifest matches $name"
        }

        $installer = New-Object -ComObject WindowsInstaller.Installer
        foreach ($name in $expectedMsi) {
            $msiPath = Join-Path $artifactRoot $name
            Assert-True ((Get-MsiProperty $installer $msiPath 'ProductName') -eq 'SecureWall') "$name ProductName is SecureWall"
            Assert-True ((Get-MsiProperty $installer $msiPath 'ProductVersion') -eq '0.2.0.0') "$name ProductVersion is 0.2.0.0"
            Assert-True ((Get-MsiProperty $installer $msiPath 'ALLUSERS') -eq '1') "$name is per-machine"
        }
    }
}

if ($script:Failures.Count -gt 0) {
    Write-Output ""
    Write-Output "$($script:Failures.Count) failed, $script:PassCount passed"
    foreach ($failure in $script:Failures) {
        Write-Output " - $failure"
    }
    exit 1
}

Write-Output ""
Write-Output "$script:PassCount passed, 0 failed"
