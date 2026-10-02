# Deployment

SecureWall is a Windows desktop/controller plus LocalSystem service packaged as a per-machine WiX MSI. Debug output is `TinyWall/bin/Debug/SecureWall.exe`; the source directory retains its upstream name to minimize fork churn.

The product version has one source: `<Version>` in `TinyWall/TinyWall.csproj`. The SDK derives `FileVersion` from it (no `<FileVersion>` or `<AssemblyVersion>` override), `MsiSetup/Product.wxs` binds `ProductVersion` to `!(bind.fileVersion.SecureWallEXE)`, `.github/workflows/release.yml` refuses a tag that does not equal `v<Version>`, and `tests/installer/Test-SecureWallInstaller.ps1` asserts both the csproj value and the four-part MSI value. Bump all of them together, plus the `workflow_dispatch` default tag in `release.yml`, and add `docs/releases/v<Version>.md`.

There is no SecureWall binary update feed. The inherited TinyWall updater and its feed URL were removed from the code; upstream TinyWall updates must never replace this fork. The MSI has a distinct product name, installation/data paths, service, named pipe, WFP provider GUID, scheduled task, UpgradeCode, and auto-generated component GUIDs.

User-facing text and links name SecureWall and point at `https://github.com/ryanportfolio/SecureWall`, never at the upstream TinyWall repository (a core test scans the product for `github.com/pylorak`). TinyWall is named only where SecureWall detects or cleans up an installed TinyWall, in the GPL attribution and copyright notices, and in internal code names such as the `TinyWall/` source directory, namespaces and resource keys.

No signing, publishing, deployment, or real firewall activation is authorized by ordinary build work. Local unsigned test MSIs may be built without installing them. An explicitly requested public prerelease may carry unsigned test MSIs only when the release and README label them as unsigned, alpha, and not end-to-end verified. Publishing one: after the version bump merges, create the GitHub release as a prerelease on the merge commit (`gh release create v<Version> --prerelease --target <sha> --notes-file docs/releases/v<Version>.md`); `release.yml` runs on the published release, builds the three MSIs, re-checks the tag and prerelease status, and attaches them with `SHA256SUMS.txt`. It refuses a non-prerelease, so a full release is a later manual flip of the prerelease flag on GitHub. Production shipping requires a successful release build, signed artifacts, GPL source availability, and the complete local-console matrix in `docs/TESTING.md`.

## secwall.org

https://secwall.org is a static site on the Vercel project `securewall` (team `sardonicasts-projects`). It is deployed from the CLI and not linked to Git, so publishing a release changes nothing on it. Its files live in `site/` (added by ryanportfolio/SecureWall#15; `site/README.md` covers preview, checks and deploy). Until that PR merges, the only copy is the Vercel production deployment.

Every release updates the site in the same task: a new tag, and a prerelease flipped to a full release. After `release.yml` has attached the MSIs:

1. Replace `v<old>` with `v<new>` in `site/index.html` and `site/build/index-*.js`. The version appears in release-tag links, `releases/download/v<old>/SecureWall_<arch>.msi` links, the `tree/v<old>/tools/diagnostics` link, the "v<old> alpha" labels and the JSON-LD description. Grep both files for the old version afterwards; expect zero hits. A flip to a full release also changes the "alpha" and "unsigned" wording, which needs the user's copy decision.
2. The bundle's React source is not in the repo, so a literal string replacement is the only allowed edit to `site/build/`. `/build/*` is served `immutable` for a year, so give the edited bundle a new hashed file name and update every reference to it (`index.html`, `tools/site/prune-css.mjs`). An edit under the old name stays cached in browsers.
3. `curl -sIL` each new download URL and confirm it resolves to the MSI. Run `node tools/site/seo.mjs --check`, then `vercel deploy --prod` from `site/`.
4. Confirm the live page: `curl -sL https://secwall.org/` contains `v<new>` and no `v<old>`.
