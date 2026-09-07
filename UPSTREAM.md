# Upstream provenance

SecureWall is a modified version of [TinyWall](https://github.com/pylorak/TinyWall):

- Upstream release: 3.5.1 (tag `rel-3.5.1`, 2026-06-07)
- Upstream commit (pin): `1df71b146d01d734d5b5a45a814b29e6a073f4d0`
- Upstream head last reviewed: `62b088f` (2026-08-15)
- Upstream author and copyright holder: Károly Pados and other contributors recorded by the TinyWall repository
- Upstream license: GNU General Public License version 3
- Import date: 2026-07-14

The unmodified upstream README, changelog, future-ideas file, and GPL text are retained as `UPSTREAM-README.md`, `UPSTREAM-CHANGELOG.txt`, `UPSTREAM-FUTURE-IDEAS.txt`, and `LICENSE`.

## How the fork relates to upstream

The fork is a source snapshot of the pinned commit, not a git branch of it. This repository shares no commit history with `pylorak/TinyWall`, so `git merge` and `git rebase` against upstream are impossible. Upstream fixes are carried over by hand: read the upstream diff, apply the equivalent change to the fork's files, add tests, and record the upstream commit id in the commit message and in the list below.

## Upstream fixes backported

Each entry names the upstream commit or issue and where the change landed in the fork.

- `62b088f`: bounded read of the WFP net-event `appId` blob. `NetEventSubscription.ReadAppIdPath` reads at most `size / 2` UTF-16 units and stops at the first null (`pylorak.Windows.WFP/NetEventSubscription.cs`).
- Issue #128 items 2 and 3: native net-event callbacks (`NativeCallbackHandler0`, `NativeCallbackHandler1`, `WfpNetEventCallback`) wrap their body in `try/catch` so no managed exception propagates into FWPUClnt's RPC thread; appId length is bounded as above.
- Issue #128 item 5: `Utils.RandomString` draws from `RandomNumberGenerator` with rejection sampling over the 62-character alphabet. Used as the PBKDF2 salt source for the password file. Upstream had not landed this fix as of `62b088f`; the fork applied it from the issue text.
- `3448b6b`: update downloads (application database, hosts file) are decompressed and hashed in memory instead of through shared temp files (`TinyWall/TinyWallService.cs`, `TinyWall/Utils.cs`, `TinyWall/HostsFileManager.cs`).
- `ac3b725` (atomic-write substance only): `TinyWall/AtomicFileWriter.cs` writes a `CreateNew` temp file beside the target, flushes it to disk, and swaps it in with `File.Replace`; the hosts backup lock is restored in `finally`.

## Upstream fixes not backported

Still open in the fork as of the head above:

- `e0e7e22`: ACL on the update installer download. Not applicable while the fork's binary update feed is compiled off, but the code path remains.
- `b741831`: PBKDF2 stored-hash format change. The fork keeps the 3.5.1 format.
- `3146d95`: ACL on the password file (`pwd` under the application-data directory).
- `c2ae8df`, `a56488f`, `2329049`: fixes in the WFP wrapper (`pylorak.Windows.WFP/`).
- `487a0eb`: net-event subscription optimisation.

## SecureWall modifications

SecureWall uses a distinct product name, executable identity, Windows service name, named pipe, scheduled task, application-data directory, and WFP provider GUID. Its principal functional change is a fail-closed, rate-limited outbound-drop prompt pipeline with service-owned attribution and authorization tokens.

Source-file namespaces and some internal class names remain `pylorak.TinyWall` to keep the fork reviewable and reduce unnecessary divergence. They do not identify the installed product.

SecureWall has no configured binary update feed. It must not install binaries advertised by TinyWall's upstream update service.

SecureWall is not TinyWall and is not endorsed by TinyWall's author.
