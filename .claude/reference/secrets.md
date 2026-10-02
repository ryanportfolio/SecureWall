# Secrets & environment variables

> Env var names, what they key, and where they're consumed. Never store actual secret VALUES here: names and purposes only.

| Env var | Keys what | Consumed in |
|---|---|---|
| `VERCEL_TOKEN` | Vercel API and CLI access to team `sardonicasts-projects` (repository secret) | `.github/workflows/secwall-site.yml`, `tools/release/update-secwall-site.mjs` |
| `VERCEL_ORG_ID` | Vercel team id, not secret; set in the workflow | same as above; also links the Vercel CLI deploy |
| `VERCEL_PROJECT_ID` | Vercel project `securewall` id, not secret; set in the workflow | Vercel CLI deploy in `secwall-site.yml` |
