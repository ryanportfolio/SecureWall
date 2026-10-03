param([Parameter(Mandatory)][string]$Msi)
# Child of Preview-SetupPages.ps1. Opens the package as a Windows Installer
# session and shows each setup page through DoAction. No ExecuteAction or
# InstallExecute sequence runs, so clicking Install only closes the page.
$installer = New-Object -ComObject WindowsInstaller.Installer
$installer.UILevel = 5
$session = $installer.OpenPackage($Msi, 1)
foreach ($action in 'CostInitialize', 'FileCost', 'CostFinalize') { "$action -> $($session.DoAction($action))" }
"welcome -> $($session.DoAction('SecureWallWelcomeDlg'))"
"progress -> $($session.DoAction('SecureWallProgressDlg'))"
Start-Sleep -Seconds 4
"exit -> $($session.DoAction('SecureWallExitDlg'))"
$session.Property('ACTION') = 'ADMIN'
"exit-admin -> $($session.DoAction('SecureWallExitDlg'))"
$session.Property('ACTION') = 'INSTALL'
"cancelled -> $($session.DoAction('SecureWallCancelledDlg'))"
"failed -> $($session.DoAction('SecureWallFatalError'))"
