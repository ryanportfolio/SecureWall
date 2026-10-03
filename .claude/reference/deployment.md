# Deployment

SecureWall is a Windows desktop/controller plus LocalSystem service packaged as a per-machine WiX MSI. Debug output is `TinyWall/bin/Debug/SecureWall.exe`; the source directory retains its upstream name to minimize fork churn.

The product version has one source: `<Version>` in `TinyWall/TinyWall.csproj`. The SDK derives `FileVersion` from it (no `<FileVersion>` or `<AssemblyVersion>` override), `MsiSetup/Product.wxs` binds `ProductVersion` to `!(bind.fileVersion.SecureWallEXE)`, `.github/workflows/release.yml` refuses a tag that does not equal `v<Version>`, and `tests/installer/Test-SecureWallInstaller.ps1` asserts both the csproj value and the four-part MSI value. Bump all of them together, plus the `workflow_dispatch` default tag in `release.yml`, and add `docs/releases/v<Version>.md`.

There is no SecureWall binary update feed. The inherited TinyWall updater and its feed URL were removed from the code; upstream TinyWall updates must never replace this fork. The MSI has a distinct product name, installation/data paths, service, named pipe, WFP provider GUID, scheduled task, UpgradeCode, and auto-generated component GUIDs.

User-facing text and links name SecureWall and point at `https://github.com/ryanportfolio/SecureWall`, never at the upstream TinyWall repository (a core test scans the product for `github.com/pylorak`). TinyWall is named only where SecureWall detects or cleans up an installed TinyWall, in the GPL attribution and copyright notices, and in internal code names such as the `TinyWall/` source directory, namespaces and resource keys.

No signing, publishing, deployment, or real firewall activation is authorized by ordinary build work. Local unsigned test MSIs may be built without installing them. An explicitly requested public prerelease may carry unsigned test MSIs only when the release and README label them as unsigned, alpha, and not end-to-end verified. Publishing one: after the version bump merges, create the GitHub release as a prerelease on the merge commit (`gh release create v<Version> --prerelease --target <sha> --notes-file docs/releases/v<Version>.md`); `release.yml` runs on the published release, builds the three MSIs, re-checks the tag and prerelease status, and attaches them with `SHA256SUMS.txt`. It refuses a non-prerelease, so a full release is a later manual flip of the prerelease flag on GitHub. Production shipping requires a successful release build, signed artifacts, GPL source availability, and the complete local-console matrix in `docs/TESTING.md`.

## secwall.org

https://secwall.org is a static site on the Vercel project `securewall` (team `sardonicasts-projects`). It is deployed from the CLI and not linked to Git. The live production deployment is the source of truth for its files; no repository holds them. Its JS bundle is prebuilt and its React source is not available, so version strings are the only edits it takes. The live site is the SEO version from ryanportfolio/SecureWall#15 (closed unmerged), deployed 2026-10-02 at v0.4.0: metadata, JSON-LD, a hidden crawler copy of the page text in `index.html`, `404.html`, `robots.txt`, `sitemap.xml` and a pruned stylesheet. That PR's branch keeps its preview server, SEO check and brand-image tools.

Every release updates the site. `release.yml` does this in its `site` job, after the MSIs are attached, by calling `.github/workflows/secwall-site.yml`. That workflow runs `tools/release/update-secwall-site.mjs`, which:

1. Downloads the deployment `secwall.org` points at, checking each file against the SHA-1 Vercel reports.
2. Reads the current release from the site's GitHub release links. It stops without deploying when the site already shows the tag, and refuses to move to an older version.
3. Replaces only `v<old>`, `Version <old>` and the JSON-LD `"softwareVersion":"<old>"` in `index.html` and `build/*.js`. The bundle also contains Theatre.js's own `"0.4.0"` state version, so a bare version number is never replaced.
4. Gives each edited bundle a new hashed name and updates every reference to it. `/build/*` may be served `immutable`, and an edit under the old name would stay cached in browsers.
5. Fails if any old version form remains, if `index.html` references a missing bundle, or if any new GitHub link or `SecureWall_<arch>.msi` asset does not return 200.

The workflow then deploys with `vercel deploy --prod` and checks that the live page links the new tag. It needs the `VERCEL_TOKEN` repository secret. After publishing a release, confirm the `site` job passed. To retry, or to update the site without rebuilding the MSIs, run `secwall-site` by hand with the tag (`gh workflow run secwall-site.yml -f tag=v<Version>`).

Flipping a prerelease to a full release is not automated. The site calls each release an unsigned alpha, so that change needs the user's copy decision and a hand edit following the same rules (version forms only, rename the bundle, deploy, check live).

To test locally, set `VERCEL_ORG_ID=team_kjAf51SrpxZhvAptYZizUMmB` and a `VERCEL_TOKEN`, then run `node tools/release/update-secwall-site.mjs --version <x.y.z> --out .tmp/site`. It writes the result to `.tmp/site` and deploys nothing.
