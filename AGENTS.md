# AGENTS.md

All agent instructions for this repository live in [CLAUDE.md](CLAUDE.md): the working rules
(master only, git sync, restart and verify, targeted tests, zero warnings, TLDRs, UI before/after
pages) and the full architecture map. Read it before changing anything. This file is only a
pointer, so the rules exist in one place and cannot drift.

## CI/CD and Azure (same section in every Po repo)

- A push to `master` builds, tests, deploys and health-gates (`.github/workflows/deploy.yml`). Nothing is deployed by hand.
- Azure sign-in is GitHub OIDC (the `gh-<repo>-deploy` app). Its IDs are repo **variables** `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID`, and the App Service name is `AZURE_WEBAPP_NAME`. Never move them to secrets or write resource names into workflows.
- Every permission the app and CI need lives in `SCRIPTS/ensure-azure-access.ps1`. A role granted by hand and not added there is a bug: the next re-provision loses it.
- The app answers `/health/live` (process up, no dependencies) and `/health` (dependencies reachable). The deploy's health gate checks both.
- Tests pass on any machine: no drive-letter, temp-folder or working-directory assumptions, and no live config value hard-coded as an expected result (read it from the same config the app reads).
- A red run on the default branch gets an ntfy push from the CI watcher in PoPunkouterSoftware. Fix it, or say why it is red.
