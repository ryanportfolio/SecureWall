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
Assert-True ($project -match '<Version>0\.5\.2</Version>') 'application version is 0.5.2'

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
Assert-True ($removeSequence.Before -eq 'UnpublishFeatures') 'cleanup precedes MSI registry value removal and file removal'
Assert-True ($product.Contains('NOT Installed OR (REMOVE="ALL" AND NOT REINSTALL)')) 'launch condition rejects repair before transaction'
Assert-True ($product.Contains('NOT EXISTINGSECUREWALLSERVICE AND SECUREWALLDELETEPENDING <> "#1"')) 'fresh MSI rejects existing and delete-pending service registrations'

# In-place upgrade contract (docs/plans/2026-11-01-in-place-upgrade-and-updater.md,
# replacing the SW10 "no in-place upgrade" assertions from v0.5.0 on).
function Get-SequenceCondition {
    param([string]$Action, [string]$Sequence = 'InstallExecuteSequence')
    $node = $wix.SelectSingleNode("//w:$Sequence/w:Custom[@Action='$Action']", $ns)
    if ($null -eq $node) { return $null }
    return $node.InnerText.Trim()
}
$launchConditions = @($wix.SelectNodes('/w:Wix/w:Product/w:Condition', $ns) | ForEach-Object { $_.InnerText.Trim() })
Assert-True ($product -match '<\?define OldestUpgradeableVersion="0\.5\.0" \?>') 'v0.5.0 is the oldest version upgradeable in place'
Assert-True (($launchConditions | Where-Object { $_ -match 'UPGRADINGPRODUCTCODE' }).Count -eq 0) 'no launch condition refuses removal inside a newer package''s upgrade'
$upgradeRows = @{}
foreach ($row in $wix.SelectNodes('//w:Upgrade[@Id="{D257A00C-9D9B-4948-910F-5524F20819D0}"]/w:UpgradeVersion', $ns)) { $upgradeRows[$row.Property] = $row }
$incompatible = $upgradeRows['OLDER_INCOMPATIBLE_FOUND']
$upgradeable = $upgradeRows['OLDER_UPGRADEABLE_FOUND']
$self = $upgradeRows['SELF_FOUND']
$newer = $upgradeRows['NEWER_FOUND']
Assert-True ($upgradeRows.Count -eq 4) 'Upgrade table has exactly the incompatible, upgradeable, equal and newer rows'
Assert-True ($null -ne $incompatible -and $incompatible.OnlyDetect -eq 'yes' -and $incompatible.Maximum -eq '$(var.OldestUpgradeableVersion)' -and $incompatible.IncludeMaximum -eq 'no' -and -not $incompatible.Minimum) 'versions below the upgradeable floor are only detected'
Assert-True ($null -ne $upgradeable -and $upgradeable.OnlyDetect -eq 'no' -and $upgradeable.MigrateFeatures -eq 'yes' -and $upgradeable.Minimum -eq '$(var.OldestUpgradeableVersion)' -and $upgradeable.IncludeMinimum -eq 'yes' -and $upgradeable.Maximum -eq '$(var.ProductVersion)' -and $upgradeable.IncludeMaximum -eq 'no') 'only the older upgradeable range is removed'
Assert-True ($null -ne $self -and $self.OnlyDetect -eq 'yes' -and $self.Minimum -eq '$(var.ProductVersion)' -and $self.IncludeMinimum -eq 'yes' -and $self.Maximum -eq '$(var.ProductVersion)' -and $self.IncludeMaximum -eq 'yes') 'an equal version is only detected'
Assert-True ($null -ne $newer -and $newer.OnlyDetect -eq 'yes' -and $newer.Minimum -eq '$(var.ProductVersion)' -and $newer.IncludeMinimum -eq 'no') 'a newer version is only detected'
Assert-True ($launchConditions -contains 'Installed OR NOT SELF_FOUND') 'equal-version rebuild is refused before any action'
Assert-True ($launchConditions -contains 'Installed OR (NOT OLDER_INCOMPATIBLE_FOUND AND NOT NEWER_FOUND)') 'downgrade and pre-0.5.0 installations are refused before any action'
Assert-True ((Get-SequenceCondition 'FoundNewerVersion') -eq 'NEWER_FOUND' -and (Get-SequenceCondition 'FoundIncompatibleVersion') -eq 'OLDER_INCOMPATIBLE_FOUND') 'newer and incompatible products keep their specific errors'
Assert-True ($project -match '<Version>\d+\.\d+\.\d+</Version>') 'product version has three fields, the ones Windows Installer compares'
$rep = $wix.SelectSingleNode('//w:InstallExecuteSequence/w:RemoveExistingProducts', $ns)
Assert-True ($null -ne $rep -and $rep.After -eq 'InstallInitialize' -and -not $rep.Before -and -not $rep.Sequence) 'old product removal shares the new installation''s transaction and rollback script'
Assert-True ($launchConditions -contains 'Installed OR OLDER_UPGRADEABLE_FOUND OR (NOT EXISTINGSECUREWALLSERVICE AND SECUREWALLDELETEPENDING <> "#1")') 'existing service registration is allowed only for an upgradeable installation'
Assert-True ($launchConditions -contains 'Installed OR SECUREWALLDELETEPENDING <> "#1"') 'a delete-pending service is refused for upgrades too'
$upgradeDir = $wix.SelectSingleNode('//w:Property[@Id="UPGRADEINSTALLDIR"]/w:RegistrySearch', $ns)
$upgradePlatform = $wix.SelectSingleNode('//w:Property[@Id="UPGRADEPLATFORM"]/w:RegistrySearch', $ns)
Assert-True ($null -ne $upgradeDir -and $upgradeDir.Root -eq 'HKLM' -and $upgradeDir.Key -eq 'Software\SecureWall' -and $upgradeDir.Name -eq 'InstallDir' -and $upgradeDir.Win64 -eq '$(var.Win64)') 'upgrade reads the recorded INSTALLDIR in its own registry view'
Assert-True ($null -ne $upgradePlatform -and $upgradePlatform.Name -eq 'Platform' -and $upgradePlatform.Win64 -eq '$(var.Win64)') 'upgrade reads the recorded package platform'
$setDir = $wix.SelectSingleNode('//w:CustomAction[@Id="SetInstallDirFromRegistry"]', $ns)
Assert-True ($null -ne $setDir -and $setDir.Property -eq 'INSTALLDIR' -and $setDir.Value -eq '[UPGRADEINSTALLDIR]') 'upgrade installs into the recorded directory'
foreach ($sequence in 'InstallExecuteSequence', 'InstallUISequence') {
    $node = $wix.SelectSingleNode("//w:$sequence/w:Custom[@Action='SetInstallDirFromRegistry']", $ns)
    Assert-True ($null -ne $node -and $node.After -eq 'AppSearch' -and $node.InnerText.Trim() -eq 'OLDER_UPGRADEABLE_FOUND AND UPGRADEINSTALLDIR AND NOT Installed') "$sequence sets INSTALLDIR from the registry only for an upgrade, before costing"
}
Assert-True ($launchConditions -contains 'Installed OR NOT OLDER_UPGRADEABLE_FOUND OR (UPGRADEINSTALLDIR AND UPGRADEPLATFORM = "$(var.BuildArch)")') 'upgrade requires a recorded directory and the same package platform'

$freshOnly = 'NOT Installed AND NOT OLDER_UPGRADEABLE_FOUND'
Assert-True ((Get-SequenceCondition 'InstallCustom') -eq $freshOnly -and (Get-SequenceCondition 'InstallCustomRollback') -eq $freshOnly) 'first-install activation and its teardown rollback never run during an upgrade'
$detect = $wix.SelectSingleNode('//w:CustomAction[@Id="StartControllerAndDetect"]', $ns)
Assert-True ($detect.ExeCommand -eq '/autowhitelist' -and (Get-SequenceCondition 'StartControllerAndDetect') -eq "$freshOnly AND UILevel=5 AND NOT SECUREWALL_SUPPRESS_USER_ACTIONS") 'autowhitelist runs only on a fresh full-UI install'
$autowhitelistActions = @($wix.SelectNodes('//w:CustomAction[contains(@ExeCommand, "/autowhitelist")]', $ns))
Assert-True ($autowhitelistActions.Count -eq 1 -and @($autowhitelistActions | Where-Object { (Get-SequenceCondition $_.Id) -notmatch 'NOT OLDER_UPGRADEABLE_FOUND' }).Count -eq 0) 'every /autowhitelist action excludes upgrades'
$upgradeActions = @(
    @{ Id = 'UpgradeStop'; Command = '/msi-upgrade-stop [UPGRADINGPRODUCTCODE]'; Execute = 'deferred'; Condition = 'Installed AND REMOVE="ALL" AND UPGRADINGPRODUCTCODE' },
    @{ Id = 'UpgradeStopRollback'; Command = '/msi-upgrade-restore [UPGRADINGPRODUCTCODE]'; Execute = 'rollback'; Condition = 'Installed AND REMOVE="ALL" AND UPGRADINGPRODUCTCODE' },
    @{ Id = 'UpgradeStart'; Command = '/msi-upgrade-start [ProductCode]'; Execute = 'deferred'; Condition = 'NOT Installed AND OLDER_UPGRADEABLE_FOUND' },
    @{ Id = 'UpgradeStartRollback'; Command = '/msi-upgrade-abort [ProductCode]'; Execute = 'rollback'; Condition = 'NOT Installed AND OLDER_UPGRADEABLE_FOUND' },
    @{ Id = 'UpgradeCommit'; Command = '/msi-upgrade-commit [ProductCode]'; Execute = 'commit'; Condition = 'NOT Installed AND OLDER_UPGRADEABLE_FOUND' }
)
foreach ($expected in $upgradeActions) {
    $action = $wix.SelectSingleNode("//w:CustomAction[@Id='$($expected.Id)']", $ns)
    Assert-True ($null -ne $action -and $action.FileKey -eq 'SecureWallEXE' -and $action.ExeCommand -eq $expected.Command -and $action.Execute -eq $expected.Execute -and $action.Impersonate -eq 'no') "$($expected.Id) runs $($expected.Command) as SYSTEM $($expected.Execute) action"
    Assert-True ((Get-SequenceCondition $expected.Id) -eq $expected.Condition) "$($expected.Id) runs only on its side of an upgrade"
}
Assert-True ($wix.SelectSingleNode('//w:CustomAction[@Id="UpgradeStop"]', $ns).Return -eq 'check' -and $wix.SelectSingleNode('//w:CustomAction[@Id="UpgradeStart"]', $ns).Return -eq 'check') 'a failed stop or start rolls the upgrade back'
Assert-True ($wix.SelectSingleNode('//w:InstallExecuteSequence/w:Custom[@Action="UpgradeStopRollback"]', $ns).Before -eq 'UpgradeStop' -and $wix.SelectSingleNode('//w:InstallExecuteSequence/w:Custom[@Action="UpgradeStartRollback"]', $ns).Before -eq 'UpgradeStart') 'each upgrade rollback twin is registered before its step'
Assert-True ($wix.SelectSingleNode('//w:InstallExecuteSequence/w:Custom[@Action="UpgradeStop"]', $ns).Before -eq 'UninstallCustomRollback') 'old package stops itself before its files and registry values are removed'
# Old-side steps must end up Before UnpublishFeatures (so before RemoveRegistryValues,
# which removes the event source their failure events need, and RemoveFiles). Follow
# each Before chain of custom actions until it reaches a standard action.
function Get-BeforeAnchor {
    param([string]$Action)
    $seen = @{}
    while ($true) {
        if ($seen.ContainsKey($Action)) { return $null }
        $seen[$Action] = $true
        $node = $wix.SelectSingleNode("//w:InstallExecuteSequence/w:Custom[@Action='$Action']", $ns)
        if ($null -eq $node) { return $Action }
        if (-not $node.Before) { return $null }
        $Action = $node.Before
    }
}
foreach ($oldSide in 'UpgradeStop', 'UpgradeStopRollback') {
    Assert-True ((Get-BeforeAnchor $oldSide) -eq 'UnpublishFeatures') "$oldSide is sequenced before UnpublishFeatures"
}
# FindRelatedProducts does not run in the old package during its nested removal, so
# an old-side condition that reads one of its properties would silently be false.
$relatedProperties = @($wix.SelectNodes('//w:Upgrade/w:UpgradeVersion', $ns) | ForEach-Object { $_.Property })
Assert-True ($relatedProperties.Count -ge 2) 'FindRelatedProducts properties are found for the old-side condition check'
foreach ($oldSide in 'UpgradeStop', 'UpgradeStopRollback', 'UninstallCustom', 'UninstallCustomRollback', 'RemoveProductKey', 'RemoveProductKey64') {
    $condition = Get-SequenceCondition $oldSide
    Assert-True ($null -ne $condition -and @($relatedProperties | Where-Object { $condition -match "\b$_\b" }).Count -eq 0) "$oldSide condition reads no FindRelatedProducts property"
}
# Explicit removal deletes HKLM\SOFTWARE\SecureWall in the commit phase, so a rolled-back
# uninstall keeps InstallDir and Platform.
foreach ($expected in @(
    @{ Id = 'RemoveProductKey64'; Flag = ' /reg:64'; Condition = 'Installed AND REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE AND VersionNT64' },
    @{ Id = 'RemoveProductKey'; Flag = ''; Condition = 'Installed AND REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE AND NOT VersionNT64' })) {
    $action = $wix.SelectSingleNode("//w:CustomAction[@Id='$($expected.Id)']", $ns)
    Assert-True ($null -ne $action -and $action.Execute -eq 'commit' -and $action.Impersonate -eq 'no' -and $action.Return -eq 'ignore' -and
        $action.ExeCommand -eq ('"[SystemFolder]reg.exe" delete "HKLM\SOFTWARE\SecureWall" /f' + $expected.Flag)) "$($expected.Id) deletes the product key as a SYSTEM commit action"
    Assert-True ((Get-SequenceCondition $expected.Id) -eq $expected.Condition) "$($expected.Id) runs only on explicit removal"
}
Assert-True ($wix.SelectSingleNode('//w:InstallExecuteSequence/w:Custom[@Action="UpgradeStart"]', $ns).Before -eq 'InstallCustomRollback') 'new package starts itself after its files and registry values are installed'
$startController = $wix.SelectSingleNode('//w:CustomAction[@Id="StartController"]', $ns)
Assert-True ($startController.ExeCommand -eq '' -and $startController.Execute -eq 'commit' -and -not $startController.Impersonate -and (Get-SequenceCondition 'StartController') -eq 'NOT Installed AND OLDER_UPGRADEABLE_FOUND AND UILevel=5 AND NOT SECUREWALL_SUPPRESS_USER_ACTIONS' -and $wix.SelectSingleNode('//w:InstallExecuteSequence/w:Custom[@Action="UpgradeCommit"]', $ns).Before -eq 'StartController') 'upgrade restarts the installing user''s tray without autowhitelist after the commit step'
$registryKey = $wix.SelectSingleNode('//w:Component[@Id="RegistryKeys"]/w:RegistryKey', $ns)
Assert-True ($null -ne $registryKey -and $registryKey.Key -eq 'Software\SecureWall' -and -not $registryKey.ForceDeleteOnUninstall -and -not $registryKey.Action) 'MSI never deletes HKLM\Software\SecureWall, which holds the recovery journals'
$registryValues = @{}
foreach ($value in $registryKey.SelectNodes('w:RegistryValue', $ns)) { $registryValues[$value.Name] = $value.Value }
Assert-True ($registryValues.Count -eq 2 -and $registryValues['InstallDir'] -eq '[INSTALLDIR]' -and $registryValues['Platform'] -eq '$(var.BuildArch)') 'MSI owns only the InstallDir and Platform values'
Assert-True ($null -eq $wix.SelectSingleNode('//*[@ForceDeleteOnUninstall="yes"]', $ns)) 'no MSI registry key is force-deleted on uninstall'
$wixProject = Read-RepoFile 'MsiSetup\MsiSetup.wixproj'
Assert-True ($product -match '(?s)<\?ifdef PreUpgradeVersion \?>\s*<\?undef OldestUpgradeableVersion \?>\s*<\?define OldestUpgradeableVersion="\$\(var\.PreUpgradeVersion\)" \?>' -and $product -match "(?s)<\?ifndef PreUpgradeVersion \?>\s*<UpgradeVersion OnlyDetect='no' MigrateFeatures='yes' Property='OLDER_UPGRADEABLE_FOUND'.*?/>\s*<\?endif \?>" -and $wixProject -match "VersionLessThanOrEquals\('\`$\(SecureWallVersion\)', '0\.5\.0'\)") 'builds at or below 0.5.0, v0.5.0 included, keep a valid Upgrade table with no upgradeable range'

# Fault injection for the upgrade VM cases lives only in the UpgradeFaultTest configuration.
Assert-True ($product -match '(?s)<\?ifdef UpgradeFaultTest \?>\s*<!--.*?-->\s*<Property Id="SECUREWALL_FAULT_TEST" Value="1" />\s*<\?endif \?>') 'fault-test packages carry a property release checks can refuse'
Assert-True ($product -match '(?s)<\?ifdef UpgradeFaultTest \?>\s*<\?define PackageName="SecureWall \(fault test\)" \?>\s*<\?else \?>\s*<\?define PackageName="\$\(var\.ProductName\)" \?>' -and $product -match '<Product Id="\*" Codepage="1250" Name="\$\(var\.PackageName\)"') 'fault-test packages show a (fault test) ProductName suffix'
Assert-True ((Test-Path -LiteralPath (Join-Path $repoRoot 'tools\vm\Build-SecureWallUpgradeFaultTest.ps1') -PathType Leaf) -and (Read-RepoFile 'tools\vm\Build-SecureWallUpgradeFaultTest.ps1') -match "artifacts\\upgrade-fault-test") 'fault-test packages have their own builder and output folder'
Assert-True ($wixProject -match "(?s)Condition="" '\`$\(Configuration\)' == 'UpgradeFaultTest' "">\s*<DefineConstants>UpgradeFaultTest</DefineConstants>") 'only the UpgradeFaultTest installer configuration defines the fault property'
Assert-True ($project -match "(?s)<PropertyGroup Condition=""'\`$\(Configuration\)' == 'UpgradeFaultTest'"">.*?UPGRADE_FAULT_TEST.*?\+upgrade-fault-test</InformationalVersion>" -and ([regex]::Matches($project, 'UPGRADE_FAULT_TEST')).Count -eq 1) 'only the UpgradeFaultTest application configuration compiles fault points and marks its version'
$faultSource = Read-RepoFile 'TinyWall\Installer\UpgradeFault.cs'
Assert-True ($faultSource -match '\[Conditional\("UPGRADE_FAULT_TEST"\)\]' -and $faultSource -match '(?s)static void Trip\(string point\)\s*\{\s*#if UPGRADE_FAULT_TEST') 'other builds drop every fault call and the fault body'
# Check for updates: an update approved with another administrator's credentials runs no
# action as that administrator on the requesting user's desktop.
$suppress = $wix.SelectSingleNode('//w:Property[@Id="SECUREWALL_SUPPRESS_USER_ACTIONS"]', $ns)
Assert-True ($null -ne $suppress -and $suppress.Secure -eq 'yes' -and -not $suppress.Value) 'the update helper''s suppress property is declared secure and unset by default'
Assert-True ((Get-SequenceCondition 'StartController') -match 'NOT SECUREWALL_SUPPRESS_USER_ACTIONS$' -and (Get-SequenceCondition 'StartControllerAndDetect') -match 'NOT SECUREWALL_SUPPRESS_USER_ACTIONS$') 'tray starts are skipped when user actions are suppressed'
$fatal = Read-RepoFile 'MsiSetup\SecureWallFatalErrorDlg.wxs'
[xml]$fatalXml = $fatal
foreach ($button in 'OpenSecureWallLog', 'OpenSetupLog') {
    $hide = $fatalXml.SelectSingleNode("//w:Control[@Id='$button']/w:Condition[@Action='hide']", $ns)
    Assert-True ($null -ne $hide -and $hide.InnerText.Trim() -eq 'SECUREWALL_SUPPRESS_USER_ACTIONS') "failure page hides $button when user actions are suppressed"
}
$applyPolicy = Read-RepoFile 'TinyWall\Prompting\UpdateApplyPolicy.cs'
Assert-True ($applyPolicy.Contains('SuppressUserActionsProperty = "SECUREWALL_SUPPRESS_USER_ACTIONS"') -and $applyPolicy.Contains('UpgradeCode = "{D257A00C-9D9B-4948-910F-5524F20819D0}"')) 'the update helper names the package''s suppress property and UpgradeCode'
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
Assert-True ($doctor -match '(?s)ProtectionReleasePolicy\.Release\(.*?RemoveProductRegistry\(\);\s*return released \? 0 : -1;') 'explicit removal deletes the product key last, after the journals were restored'
foreach ($mode in 'StopMode', 'RestoreMode', 'StartMode', 'AbortMode', 'CommitMode') {
    Assert-True ($program.Contains("RunUpgradeStep(args, UpgradeLifecyclePolicy.$mode, TinyWallDoctor.Upgrade")) "Program dispatches upgrade step $mode"
}
Assert-True ($doctor -match '(?s)internal static bool EnsureServiceInstalledAndRunning\(.*?UpgradeLifecyclePolicy\.BlocksStart\(UpgradeMarker\.Read\(\), UpgradeMarker\.InstallerRunning\(\), upgradeTransaction\).*?if \(mayInstall\)') 'controller repair and /install refuse to start the service during an upgrade'
Assert-True ($doctor.Contains('ServiceLifecyclePolicy.StartupTimeout')) 'activation uses shared startup allowance'
Assert-True ($safety -match '(?s)using var process = OpenProcess.*?IsRollbackProcess.*?SetStartupMode.*?TerminateProcess\(process.*?WaitForSingleObject\(process') 'rollback authenticates and retains exact process handle through restart suppression and exit wait'
Assert-True ($firewall -match '(?s)AcquireForRules\(ReadRuleIdentities\(policy\).*?OpenRecoveryKey\(\).*?NotificationsDisabled\[profile\] = true') 'production collection acquisition preflights foreign names before journal or firewall mutation'
Assert-True ($doctor -match '(?s)EnsureServiceInstalledAndRunning.*?RequireNoTinyWall\(\).*?IsServiceRunning') 'shared elevated activation checks TinyWall before running-service shortcut'
Assert-True ($managedInstaller -match '(?s)void Install\(.*?RequireNoTinyWall\(\).*?RequireProtectedInstallation\(\).*?base.Install') 'managed installer validates coexistence and protected paths before registration'
Assert-True ($safety -match 'identity.IsSystem') 'MSI maintenance explicitly checks LocalSystem token'
Assert-True ($safety -match 'ReparsePoint' -and $safety -match 'GetOwner' -and $safety -match 'GetAccessRules') 'privileged install checks reparse points, ownership, and write ACLs'
Assert-True ($safety -match 'CheckTree\(entry\)' -and $safety -match 'SpecialFolder.ProgramFiles') 'privileged install validates Program Files and all dependencies recursively'
Assert-True ($doctor.IndexOf('WindowsFirewall.RestoreOwnedState()') -ge 0 -and $doctor.IndexOf('WindowsFirewall.RestoreOwnedState()') -lt $doctor.IndexOf('TinyWallServer.DeleteWfpObjects')) 'crash cleanup restores compatibility before removing protective WFP objects'
Assert-True ($firewall -notmatch 'Contains\(SecureWallProduct.Name\)') 'firewall cleanup does not delete product-substring matches'
Assert-True ($firewall -match 'journal.Flush\(\)' -and $firewall -match 'RegistryView.Registry64') 'notification recovery is durable and architecture-independent'
Assert-True ($doctor -match '(?s)RequireServiceNotPendingDeletion\(\).*?WindowsFirewall.RequireServiceRunning\(\).*?InstallHelper') 'activation rejects pending deletion and unavailable Windows Firewall before registration'
Assert-True ($firewall -match '(?s)existingJournal\?\.GetValue\(NotificationValue\).*?INetFwPolicy2 policy = GetFwPolicy2') 'stopped-service recovery checks durable compatibility ownership before COM access'
Assert-True ($firewall.Contains('service.Status == ServiceControllerStatus.Stopped') -and $firewall.Contains('CanSkipStoppedServiceRecovery')) 'only confirmed stopped Windows Firewall can use no-journal recovery skip'
Assert-True ($safety -match '(?s)if \(process.IsInvalid\).*?RequireStoppedAfterProcessOpenFailure.*?ReadStatus\(service\)') 'rollback rechecks stopped SCM state after process-open failure'
Assert-True ($doctor -match '(?s)InstallHelper\(new string\[\] \{ "/u".*?EnsureStoppedServiceDeletion\(\)') 'managed uninstall is followed by a checked native deletion postcondition'
Assert-True ($safety -match '(?s)EnsureStoppedServiceDeletion.*?ReadStatus\(service\).CurrentState != 1.*?if \(!DeleteService\(service\)\).*?error != 1072') 'native deletion requires stopped state and rejects errors other than already-marked deletion'
Assert-True ($doctor.Contains('return succeeded;') -and $doctor.Contains('return released ? 0 : -1;') -and $doctor.Contains('service deletion was accepted by Windows')) 'accepted deferred service deletion permits successful teardown and reports pending handles'
$serviceSource = Read-RepoFile 'TinyWall\TinyWallService.cs'
$baselineStart = $serviceSource.IndexOf('private void RegisterRestrictiveBaseline()')
$baselineEnd = $serviceSource.IndexOf('transaction.Commit();', $baselineStart)
$baseline = if ($baselineStart -ge 0 -and $baselineEnd -gt $baselineStart) { $serviceSource.Substring($baselineStart, $baselineEnd - $baselineStart) } else { '' }
Assert-True ($baseline.Contains('FWPM_PROVIDER_FLAG_PERSISTENT') -and $baseline -notmatch 'serviceName\s*=') 'deny baseline provider names no service, so a Disabled or Manual service cannot drop it at boot'
Assert-True ($doctor.Contains('ReleaseForMsiWithRejectedMachineData(bool failedInstallRollback)') -and $doctor.Contains('CleanupForMsi(failedInstallRollback, false)') -and $doctor -match '(?s)ReleaseForMsiWithRejectedMachineData.*?CleanupForMsi\(bool failedInstallRollback, bool machineDataTrusted = true\).*?InstallationSafety.RequireSystemMaintenance\(\);.*?if \(machineDataTrusted\) InstallationSafety.RequireProtectedMachineData\(\);.*?ValidateRegisteredServiceImage\(!machineDataTrusted\);') 'emergency release is SYSTEM MSI maintenance that authenticates installation and LocalSystem service registration'

$installUi = Read-RepoFile 'MsiSetup\WixUI_InstallDir_Custom.wxs'
[xml]$uiXml = $installUi
$uiNs = New-Object System.Xml.XmlNamespaceManager($uiXml.NameTable)
$uiNs.AddNamespace('w', 'http://schemas.microsoft.com/wix/2006/wi')
Assert-True ($null -eq $uiXml.SelectSingleNode('//w:Publish[@Dialog="InstallDirDlg" or @Value="InstallDirDlg" or @Value="BrowseDlg"]', $uiNs)) 'full install UI offers no unsupported directory chooser'
Assert-True ($null -ne $uiXml.SelectSingleNode('//w:Publish[@Dialog="SecureWallChecklistDlg" and @Control="Next" and @Value="SecureWallReadyDlg"]', $uiNs)) 'pre-install checklist advances directly to installation confirmation'
Assert-True ($null -ne $uiXml.SelectSingleNode('//w:Publish[@Dialog="SecureWallReadyDlg" and @Control="Back" and @Value="SecureWallChecklistDlg"]', $uiNs)) 'installation confirmation returns to the checklist'
Assert-True ($null -eq $uiXml.SelectSingleNode('//w:Publish[@Value="LicenseAgreementDlg" or @Dialog="LicenseAgreementDlg"]', $uiNs)) 'GPL notice is shown without an acceptance step'

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
Assert-True ($guardCatch -match '(?s)"/msi-cleanup"\)\)\s*return TinyWallDoctor.ReleaseForMsiWithRejectedMachineData\(false\);.*?"/msi-rollback-install"\)\)\s*return TinyWallDoctor.ReleaseForMsiWithRejectedMachineData\(true\);' -and $guardCatch -notmatch '"/(uninstall|service)"\)\)\s*return TinyWallDoctor') 'only SYSTEM MSI removal and failed-install rollback reach emergency release after guard rejection'
Assert-True ($guardCatch -match 'IsUpgradeStep\(args\);' -and $guardCatch -match '(?s)UpgradeLifecyclePolicy\.AbortMode\)\)\s*return RunUpgradeStep\(args, UpgradeLifecyclePolicy\.AbortMode, TinyWallDoctor\.UpgradeAbortForMsi\);') 'upgrade steps open no dialog on guard failure; only upgrade rollback still stops the upgraded service'
Assert-True ($utils -match '(?s)SpecialFolder.CommonApplicationData.*?MachineDataGuard.Require\(\);\s*return dir;') 'production AppDataPath access enters shared guard'
$shippedPolicy = Read-RepoFile 'TinyWall\Prompting\ShippedDataPolicy.cs'
Assert-True ($machineData -match '(?s)void InstallDefaults\(\)\s*\{\s*Require\(true, true\);.*?"data-defaults".*?foreach \(string name in ShippedDataPolicy.Names\).*?Require\(\);.*?ShippedDataPolicy.Refresh\(source, PathName, name\);.*?Require\(false, true\);') 'default refresh validates full tree first, refreshes shipped data only, then revalidates'
Assert-True ($shippedPolicy -match 'Names = \{ "profiles.json", "hosts.bck" \};' -and $shippedPolicy -match 'AtomicFileWriter.Write\(target' -and $shippedPolicy -notmatch '"(config|pwd|hosts.orig)"') 'shipped data is exactly profiles.json and hosts.bck, replaced atomically'
$eventSource = $wix.SelectSingleNode('//w:Component[@Id="EventLogSource"]', $ns)
Assert-True ($null -ne $eventSource -and $eventSource.InnerXml -match 'EventSource' -and $eventSource.InnerXml -match 'Name="SecureWall"' -and $eventSource.InnerXml -match 'Log="Application"' -and $null -ne $wix.SelectSingleNode("//w:Feature/w:ComponentRef[@Id='EventLogSource']", $ns)) 'MSI registers the SecureWall Application event source'
Assert-True ($guardCatch -match 'MaintenanceEventLog.ReportError\(Installer.MaintenanceEventLog.GuardFailureId') 'guard rejection writes one line to the Application event log'
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

# profiles.json is compiled from TinyWall\Database by the interactive develtool,
# so a source edit without a matching payload edit would ship stale rules.
$sourceProfiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'TinyWall\Database') -Filter '*.json' -Recurse -File |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) | ConvertFrom-Json | ConvertTo-Json -Depth 32 -Compress } | Sort-Object)
$payloadProfiles = @($packagedDatabase.KnownApplications | ForEach-Object { $_ | ConvertTo-Json -Depth 32 -Compress } | Sort-Object)
Assert-True ($sourceProfiles.Count -gt 0 -and (Compare-Object $sourceProfiles $payloadProfiles -CaseSensitive -SyncWindow 0).Count -eq 0) 'packaged profiles.json matches the Database source profiles'
$defenderPaths = @($packagedDatabase.KnownApplications | Where-Object { $_.Name -eq 'Windows_Defender' } | ForEach-Object { $_.Components } | ForEach-Object { Split-Path -Leaf $_.Subject.ExecutablePath })
Assert-True ($defenderPaths -contains 'MpCmdRun.exe') 'payload Windows_Defender allows MpCmdRun.exe protection updates'
Assert-True ($defenderPaths -notcontains 'MpDefenderCoreService.exe') 'payload Windows_Defender excludes MpDefenderCoreService.exe'

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
Assert-True ($builder -notmatch 'UpgradeFaultTest') 'release builder never references the fault-test configuration'
Assert-True ($builder -match "-match 'fault-test'" -and $builder.Contains("Contains('SecureWallFaultTest')") -and $builder -match 'refusing to package it') 'release builder refuses an executable with either fault-test stamp'

# Release manifest (docs/plans/2026-11-01-in-place-upgrade-and-updater.md, Part 2).
foreach ($line in @(
    "'# SecureWall-Release-Manifest: 1'",
    "'# Repository: ryanportfolio/SecureWall'",
    '"# Tag: v$builtVersion"',
    '"# Version: $builtVersion"',
    '"# Prerelease: $(if ($FullRelease) { ''false'' } else { ''true'' })"',
    '"# Commit: $commit"')) {
    Assert-True ($builder.Contains($line)) "release builder writes manifest header line $line"
}
Assert-True ($builder.Contains('git -C $repoRoot rev-parse HEAD') -and $builder -match '\[switch\]\$FullRelease') 'manifest header records the built commit and defaults to prerelease'

$releaseWorkflow = Read-RepoFile '.github\workflows\release.yml'
Assert-True ($releaseWorkflow -match 'contents:\s*write') 'release workflow grants upload job write access'
Assert-True ($releaseWorkflow -match 'Build-SecureWallRelease\.ps1') 'release workflow uses repository release builder'
Assert-True ($releaseWorkflow -match 'SHA256SUMS\.txt') 'release workflow publishes hash manifest'
Assert-True ($releaseWorkflow -notmatch 'SuppressValidation') 'release workflow never bypasses MSI ICE validation'
Assert-True ($releaseWorkflow -notmatch 'ValidationVersion') 'release workflow never overrides the package version'
$ciWorkflow = Read-RepoFile '.github\workflows\ci.yml'
Assert-True ($ciWorkflow -match 'Build-SecureWallRelease\.ps1[^\r\n]*`\s+-ValidationVersion\s+0\.5\.1\s') 'CI validates the upgradeable row in a 0.5.1 build'
Assert-True ($builder -match "\[version\]\`$ValidationVersion -le \[version\]'0\.5\.0'") 'release builder refuses a validation version with no upgradeable row'
foreach ($bundleScript in 'tools\manual\Prepare-SecureWallManualTestBundle.ps1', 'tools\vm\Prepare-SecureWallVmBundle.ps1') {
    Assert-True ((Read-RepoFile $bundleScript) -match '\$builtVersion -ne "\$projectVersion\.0"') "$bundleScript refuses an executable built at another version"
}
Assert-True ($releaseWorkflow -notmatch 'UpgradeFaultTest' -and $ciWorkflow -notmatch 'UpgradeFaultTest') 'release and CI workflows never build the fault-test configuration'
$stagedExecutable = Join-Path $repoRoot 'MsiSetup\Sources\ProgramFiles\SecureWall\SecureWall.exe'
if (Test-Path -LiteralPath $stagedExecutable -PathType Leaf) {
    $stagedText = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($stagedExecutable))
    Assert-True ((Get-Item -LiteralPath $stagedExecutable).VersionInfo.ProductVersion -notmatch 'fault-test' -and -not $stagedText.Contains('SecureWallFaultTest')) 'staged installer payload is not a fault-test build'
}

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
        $manifestVersion = [regex]::Match($project, '<Version>([0-9]+\.[0-9]+\.[0-9]+)</Version>').Groups[1].Value
        Assert-True ($manifestText -match "(?s)\A# SecureWall-Release-Manifest: 1\r?\n# Repository: ryanportfolio/SecureWall\r?\n# Tag: v$([regex]::Escape($manifestVersion))\r?\n# Version: $([regex]::Escape($manifestVersion))\r?\n# Prerelease: (true|false)\r?\n# Commit: [0-9a-f]{40}\r?\n") 'hash manifest starts with the release header'
        foreach ($name in $expectedMsi) {
            $hash = (Get-FileHash -LiteralPath (Join-Path $artifactRoot $name) -Algorithm SHA256).Hash.ToLowerInvariant()
            Assert-True ($manifestText -match ([regex]::Escape("$hash  $name"))) "hash manifest matches $name"
        }

        $installer = New-Object -ComObject WindowsInstaller.Installer
        foreach ($name in $expectedMsi) {
            $msiPath = Join-Path $artifactRoot $name
            Assert-True ((Get-MsiProperty $installer $msiPath 'ProductName') -eq 'SecureWall') "$name ProductName is SecureWall"
            Assert-True ((Get-MsiProperty $installer $msiPath 'ProductVersion') -eq '0.5.2.0') "$name ProductVersion is 0.5.2.0"
            Assert-True ((Get-MsiProperty $installer $msiPath 'ALLUSERS') -eq '1') "$name is per-machine"
            Assert-True ($null -eq (Get-MsiProperty $installer $msiPath 'SECUREWALL_FAULT_TEST')) "$name is not an upgrade fault-test package"
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
