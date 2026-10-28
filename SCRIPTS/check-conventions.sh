#!/usr/bin/env bash
# Checks every punkouter27/Po* repo against the conventions in the shared "CI/CD and Azure" section of
# AGENTS.md. Reads files straight from GitHub (every Po repo is public), so it needs no clone.
#   bash SCRIPTS/check-conventions.sh        (needs gh, signed in or GH_TOKEN set)
# Exits 1 and lists each gap when any repo drifts. Run weekly by .github/workflows/conventions.yml.
# No pipefail: `raw | grep -q` stops reading early on a match, and curl's broken pipe must not count as a gap.
set -u

owner=punkouter27
fails=0
raw() { curl -fsS --max-time 30 "https://raw.githubusercontent.com/$owner/$1/master/$2" 2>/dev/null; }
gap() { echo "  ✗ $1"; fails=$((fails + 1)); }

for repo in $(gh repo list "$owner" --limit 200 --no-archived --json name -q '.[] | select(.name | startswith("Po")) | .name' | sort); do
  echo "$repo"
  [ "$(gh api "repos/$owner/$repo" -q .default_branch)" = master ] || gap "default branch is not master"
  raw "$repo" .github/dependabot.yml > /dev/null || gap "no .github/dependabot.yml"
  raw "$repo" .gitattributes | grep -qE '^\* +text=auto' || gap ".gitattributes lacks '* text=auto'"
  raw "$repo" .editorconfig > /dev/null || gap "no .editorconfig"
  raw "$repo" AGENTS.md | grep -q '^## CI/CD and Azure' || gap "AGENTS.md lacks the shared 'CI/CD and Azure' section"
  raw "$repo" CLAUDE.md | grep -q '@AGENTS.md' || gap "CLAUDE.md does not import @AGENTS.md"

  if raw "$repo" SCRIPTS/ensure-azure-access.ps1 > /dev/null || raw "$repo" scripts/ensure-azure-access.ps1 > /dev/null; then
    deploy=$(raw "$repo" .github/workflows/deploy.yml)
    grep -q 'Check the F1 daily CPU quota' <<< "$deploy" || gap "deploy.yml lacks the F1 quota check"
    grep -q 'Back up the live site' <<< "$deploy" || gap "deploy.yml lacks the backup step"
    grep -qE 'name: Roll back( to the previous build)?$' <<< "$deploy" || gap "deploy.yml lacks the rollback step"
    grep -q 'Health gate (this commit on /health/live' <<< "$deploy" || gap "deploy.yml lacks the X-Commit health gate"
    grep -qE 'secrets\.AZURE_(CLIENT|TENANT|SUBSCRIPTION)_ID' <<< "$deploy" && gap "deploy.yml reads Azure IDs from secrets, not vars"
  elif raw "$repo" ProjectSettings/ProjectVersion.txt > /dev/null; then
    webgl=$(raw "$repo" .github/workflows/webgl.yml) || { gap "Unity game without .github/workflows/webgl.yml"; continue; }
    grep -q 'self-hosted, unity' <<< "$webgl" || gap "webgl.yml does not run on the self-hosted unity runner"
    grep -qE '^[[:space:]]+pull_request:' <<< "$webgl" && gap "webgl.yml has a pull_request trigger (public repo + self-hosted runner)"
  fi
done

echo
if [ "$fails" -gt 0 ]; then echo "$fails gap(s) found."; exit 1; fi
echo "Every Po repo follows the conventions."
