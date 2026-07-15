# Deployment

SecureWall is a Windows desktop/controller plus LocalSystem service packaged as a per-machine WiX MSI. Debug output is `TinyWall/bin/Debug/SecureWall.exe`; the source directory retains its upstream name to minimize fork churn.

There is no SecureWall binary update feed. Upstream TinyWall updates are disabled and must never replace this fork. The MSI has a distinct product name, installation/data paths, service, named pipe, WFP provider GUID, scheduled task, UpgradeCode, and auto-generated component GUIDs.

No signing, publishing, deployment, or real firewall activation is authorized by ordinary build work. Local unsigned test MSIs may be built without installing them. An explicitly requested public prerelease may carry unsigned test MSIs only when the release and README label them as unsigned, alpha, and not end-to-end verified. Production shipping requires a successful release build, signed artifacts, GPL source availability, and the complete local-console matrix in `docs/TESTING.md`.
