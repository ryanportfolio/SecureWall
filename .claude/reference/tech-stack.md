# Tech stack

- C# 9 / .NET Framework 4.8 SDK-style WinForms application.
- Windows Filtering Platform through local P/Invoke wrappers; no kernel driver.
- LocalSystem Windows service plus interactive tray controller.
- Named pipes with source-generated `System.Text.Json` 10.0.9 messages; `System.Memory` is pinned to 4.6.3 and the installer carries the resolved runtime assemblies.
- Windows Security event 5157 for PID/service enrichment; WFP net events remain enforcement-authoritative.
- Dependency-free `net9.0` console harness for pure prompt-domain tests.
- WiX 3.14.1 installer source; clean x86, x64, and ARM64 Release packages pass WiX ICE validation. Test artifacts remain unsigned.

The .NET Framework target and COM references are inherited from TinyWall 3.5.1. Keep security-sensitive fork divergence small unless a migration has explicit test and packaging coverage.
