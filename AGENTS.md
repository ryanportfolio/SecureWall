# Codex Instructions

This is the Codex boundary for the AI Operating System starter. Claude Code keeps using `CLAUDE.md` and `.claude/` unchanged.

## Defaults

- Use Caveman Ultra for prose from the first reply, without asking or requiring `$caveman`. Keep code, commands, identifiers, errors, commits, PR text, and files normal.
- Use plain prose for security warnings, irreversible confirmations, and ambiguous multi-step decisions, then resume Ultra. A new session restores Ultra after the user temporarily disables it.
- Read `CLAUDE.md` for project facts, architecture, verification, references, and engineering rules. Do not inherit Claude popup rules, hook implementation, model names, slash syntax, or automatic Git behavior.
- Never execute `.claude/hooks/session-start.sh` in Codex.
- No unit tests or type tests unless the user asks.

## Capabilities

- Inspect tools exposed in the current session before using subagents, browser control, connectors, or interactive input. Config flags alone are not proof.
- Browser per session, never shared. The official playwright plugin holds one persistent profile; a second connection fails with "Browser is already in use ... use --isolated" and deadlocks. Parallel or subagent browser work uses `@playwright/mcp --isolated` (in-memory profile; copy `.mcp.json` from claude-starter).
- Serial fallback is valid only when independence is not part of the deliverable. `impartial-review`, `advocate`, and `why` require fresh independent context; if unavailable, report the gap.
- Claude `Workflow` programs are not Codex programs. Recreate their intent with exposed Codex agents or flag them blocked.
- Codex hooks may reinforce this file, but critical rules stay here because hooks depend on trust and feature availability.

## RTK

- When installed, prefer `rtk` for noisy supported reads: `rtk git status`, `rtk git diff`, `rtk git log`, `rtk git show`, `rtk rg`, and `rtk read`.
- Use `rtk test <command>` for failure-focused output; preserve its exit code and rerun natively when full success output is required as evidence.
- Use native commands for mutations, interactivity, unsupported syntax, exact-output parsing, and diagnosis when filtering hides detail.
- Codex has no Claude RTK rewrite hook here. Invoke `rtk` explicitly.

## Safety

- Caveman Ultra is a communication default, not side-effect authorization. Auto-merge and other persistent side-effect modes require explicit current-session intent.
- Do not inherit Claude auto-commit, push, PR, or merge. Ship only when the request includes shipping.
- Never push to `main`, force-push, merge, delete branches/worktrees, migrate, deploy, install runtime dependencies, or modify external checkouts without explicit approval.
- Stage explicit paths, preserve unrelated changes, and verify before claiming completion.
- Every GitHub release also updates https://secwall.org; `release.yml` deploys it through `secwall-site.yml`, and a release task is not done until that job passes. A request to publish a release is approval to update the site and deploy it to production, including a manual `secwall-site` run or the hand edit a full-release flip needs; follow the "secwall.org" section of `.claude/reference/deployment.md`.

## Shared Assets

- `.claude/skills/` remains Claude's library. Codex uses standalone native skills under `.agents/skills/`, registered in `.agents/skill-modes.json` as `native` or `disabled` (Claude-only). Read them directly and resolve resources from their Codex skill directory.
- Read relevant `.claude/reference/` material before unfamiliar work and `.agents/CODEX-SKILL-COMPATIBILITY.md` before adapted, gated, or dangerous skills.
- Adding or editing a skill updates its standalone Codex version in the same change and registers it `native` (or `disabled`) in `.agents/skill-modes.json`; never ship a generated adapter. For a `native` skill, once its port matches, run `node .claude/scripts/sync-codex-skills.mjs --baseline <name>` (`disabled` skills skip it), then `--check`; CI fails on drift or a missing registration. Use `addskill` for create/import/update/install; it uses built-in `skill-creator` for Codex authoring.
- Tool mapping: `.agents/codex-tools.md`.

## Starter Maintenance

Keep `bootstrap/` PowerShell 5.1 files ASCII-only and shell scripts LF. Claude gets its hooks and slash skills; Codex gets `AGENTS.md`, `.codex/`, and `.agents/skills/`. Keep private paths, tokens, and maintainer-only workflow out of defaults.
