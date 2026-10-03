[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Msi,
    [Parameter(Mandatory)][string]$OutputDirectory
)

# Captures every SecureWall setup page to PNG without installing anything.
# Show-SetupPages.ps1 opens the MSI as a Windows Installer session and shows the
# pages through DoAction; this script clicks through them and captures each one.
# Pages stay where Windows opens them but are faded to alpha 1, because a window
# created offscreen leaves its compositor surface unpainted. Each new page may
# flash for a moment before it is faded. Run it from an interactive desktop.

$ErrorActionPreference = 'Stop'
$Msi = (Resolve-Path -LiteralPath $Msi).ProviderPath
# .NET resolves relative paths against the process directory, not the PowerShell location.
$OutputDirectory = (New-Item -ItemType Directory -Force -Path $OutputDirectory).FullName
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class SetupPreviewWindows {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static string Text(IntPtr h) { var s = new StringBuilder(1024); GetWindowText(h, s, s.Capacity); return s.ToString(); }
    public static void Fade(IntPtr h) { SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x80000); SetLayeredWindowAttributes(h, 0, 1, 2); }
    public static List<IntPtr> TopWindows(uint pid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
    // Visible controls only: each page also holds the hidden text of its other variants.
    public static List<IntPtr> Children(IntPtr parent) {
        var list = new List<IntPtr>();
        EnumChildWindows(parent, (h, l) => { if (IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
}
'@

$childLog = Join-Path $OutputDirectory 'show-setup-pages.log'
$childErrors = Join-Path $OutputDirectory 'show-setup-pages.err.log'
$child = Start-Process powershell.exe -WindowStyle Hidden -PassThru -RedirectStandardOutput $childLog -RedirectStandardError $childErrors -ArgumentList @(
    '-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass',
    '-File', "`"$(Join-Path $PSScriptRoot 'Show-SetupPages.ps1')`"", '-Msi', "`"$Msi`"")

function Find-Page([string]$marker, [int]$seconds = 20) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($top in [SetupPreviewWindows]::TopWindows([uint32]$child.Id)) {
            foreach ($control in [SetupPreviewWindows]::Children($top)) {
                if ([SetupPreviewWindows]::Text($control) -like "*$marker*") { return $top }
            }
        }
        if ($child.HasExited) { throw "Show-SetupPages.ps1 exited while waiting for '$marker'." }
        Start-Sleep -Milliseconds 150
    }
    throw "Timed out waiting for the page containing '$marker'."
}

function Save-Page($window, [string]$name) {
    [SetupPreviewWindows]::Fade($window)
    Start-Sleep -Milliseconds 700
    $rect = New-Object SetupPreviewWindows+RECT
    [SetupPreviewWindows]::GetWindowRect($window, [ref]$rect) | Out-Null
    $bitmap = New-Object System.Drawing.Bitmap ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $hdc = $graphics.GetHdc()
            # PW_RENDERFULLCONTENT copies the compositor surface; plain WM_PRINT drops
            # MSI's transparent text controls.
            try { $printed = [SetupPreviewWindows]::PrintWindow($window, $hdc, 2) }
            finally { $graphics.ReleaseHdc($hdc) }
        }
        finally { $graphics.Dispose() }
        if (-not $printed) { throw "PrintWindow failed for $name." }
        $bitmap.Save((Join-Path $OutputDirectory "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
    "Captured $name.png"
}

function Click-Button($window, [string]$caption) {
    foreach ($control in [SetupPreviewWindows]::Children($window)) {
        if (([SetupPreviewWindows]::Text($control) -replace '&', '') -eq $caption) {
            [SetupPreviewWindows]::PostMessage($control, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null  # BM_CLICK
            return
        }
    }
    throw "Button '$caption' was not found."
}

try {
    $page = Find-Page 'Welcome to SecureWall';     Save-Page $page '01-welcome';      Click-Button $page 'Next'
    $page = Find-Page 'Before you install';        Save-Page $page '02-checklist';    Click-Button $page 'Next'
    $page = Find-Page 'Ready to install';          Save-Page $page '03-ready';        Click-Button $page 'View license'
    $page = Find-Page 'You do not need to accept'; Save-Page $page '04-license';      Click-Button $page 'Close'
    Start-Sleep -Milliseconds 1000
    $page = Find-Page 'Ready to install';          Click-Button $page 'Install'
    $page = Find-Page 'Installing SecureWall';     Save-Page $page '05-progress'
    $page = Find-Page 'SecureWall is installed';   Save-Page $page '06-finish';       Click-Button $page 'Finish'
    $page = Find-Page 'Setup files extracted';     Save-Page $page '07-finish-admin'; Click-Button $page 'Finish'
    $page = Find-Page 'Setup was cancelled';       Save-Page $page '08-cancelled';    Click-Button $page 'Finish'
    $page = Find-Page 'setup failed';              Save-Page $page '09-failed';       Click-Button $page 'Finish'
    $page = Find-Page 'Removing SecureWall';       Save-Page $page '10-progress-removal'
    $page = Find-Page 'SecureWall was removed';    Save-Page $page '11-finish-removal'; Click-Button $page 'Finish'
    if (-not $child.WaitForExit(15000)) { throw 'Show-SetupPages.ps1 did not exit after the last page.' }
}
finally {
    if (-not $child.HasExited) { $child.Kill() }
}
