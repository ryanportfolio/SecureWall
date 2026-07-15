# Upstream provenance

SecureWall is a modified version of [TinyWall](https://github.com/pylorak/TinyWall):

- Upstream release: 3.5.1
- Upstream commit: `1df71b146d01d734d5b5a45a814b29e6a073f4d0`
- Upstream author and copyright holder: Károly Pados and other contributors recorded by the TinyWall repository
- Upstream license: GNU General Public License version 3
- Import date: 2026-07-14

The unmodified upstream README, changelog, future-ideas file, and GPL text are retained as `UPSTREAM-README.md`, `UPSTREAM-CHANGELOG.txt`, `UPSTREAM-FUTURE-IDEAS.txt`, and `LICENSE`.

## SecureWall modifications

SecureWall uses a distinct product name, executable identity, Windows service name, named pipe, scheduled task, application-data directory, and WFP provider GUID. Its principal functional change is a fail-closed, rate-limited outbound-drop prompt pipeline with service-owned attribution and authorization tokens.

Source-file namespaces and some internal class names remain `pylorak.TinyWall` to keep the fork reviewable and reduce unnecessary divergence. They do not identify the installed product.

SecureWall has no configured binary update feed. It must not install binaries advertised by TinyWall's upstream update service.

SecureWall is not TinyWall and is not endorsed by TinyWall's author.
