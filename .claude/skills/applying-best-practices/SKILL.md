---
description: Security, correctness, and performance checklist for SecureWall's C#/.NET Framework WinForms service, WFP filters, named-pipe protocol, and event correlation. Use before non-trivial changes and reviews.
---

# Applying best practices

Investigate surrounding code and callers before changing upstream behavior. Classify timing, filter precedence, service lifecycle, IPC authorization, and persistence changes as behavioral—not cleanup.

## Enforcement

- LocalSystem service owns policy; controller requests actions only.
- WFP edits stay transactional. Publish derived runtime state only after commit.
- Prompt only outbound ALE drops from known default-block runtime IDs in Normal mode.
- Explicit blocks, blocklists, raw sockets, inbound drops, BlockAll, Learning, Disabled, and AllowOutgoing modes never prompt.
- Loopback remains excluded. Prompt allow sets remote TCP/UDP connect ports only; listener fields remain null.

## Identity and IPC

- Package SID wins over executable/service attribution.
- Exact service identity is path plus service name. Shared/unknown service hosts fail closed.
- Correlation requires exact filter ID, normalized path, protocol, tuple, direction, and bounded timestamp skew.
- Controller sends only an opaque token for Allow/Ignore. Validate expiry, single use, lock state, and service-owned subject.
- Save/reload failure leaves policy blocked and token pending.

## Lifecycle and resources

- Bound queues, candidate buffers, dedup windows, cooldowns, and token lifetime.
- Restore exact prior audit flags; dispose nested leases in reverse order.
- Avoid blocking WinForms polling; keep one popup visible and dispose timers/event handlers.
- Close and timeout equal Ignore. Controller shutdown grants nothing.

## Verification

- Add a failing pure test before production behavior.
- Run pure tests, native .NET Framework build, protocol self-test, source checks, and synthetic popup preview.
- Never equate those checks with real WFP verification; use `docs/TESTING.md` on an expendable local-console VM.

Project pitfalls live in `.claude/reference/pitfalls.md`.
