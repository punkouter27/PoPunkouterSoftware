#!/usr/bin/env bash
# Checks every punkouter27/Po* repo against the conventions in the shared "CI/CD and Azure" section of
# AGENTS.md. Reads files straight from GitHub (every Po repo is public), so it needs no clone.
#   bash scripts/check-conventions.sh        (needs gh, signed in or GH_TOKEN set)
# Exits 1 and lists each gap when any repo drifts. Run weekly by .github/workflows/conventions.yml.
# No pipefail: `raw | grep -q` stops reading early on a match, and curl's broken pipe must not count as a gap.
set -u

owner=punkouter27
fails=0
config="$(dirname "$0")/ci-repos.json"
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

  # Shared CI/CD files: every repo of an app type holds byte-identical copies of its reference
  # repo's files (scripts/ci-repos.json; copied by scripts/sync-ci.ps1). The step-by-step greps
  # this replaced missed any repo whose scripts folder was spelled SCRIPTS.
  type=$(jq -r --arg r "$repo" '.types | to_entries[] | select(.value.repos | index($r)) | .key' "$config")
  if [ -n "$type" ]; then
    ref=$(jq -r --arg t "$type" '.types[$t].reference' "$config")
    while IFS=$'\t' read -r path when; do
      [ -n "$when" ] && ! raw "$repo" "$when" > /dev/null && continue
      want=$(raw "$ref" "$path") || { gap "reference $ref has no $path"; continue; }
      have=$(raw "$repo" "$path") || { gap "no $path (a $type repo; copy it with scripts/sync-ci.ps1)"; continue; }
      [ "$have" = "$want" ] || gap "$path differs from $ref's copy (run scripts/sync-ci.ps1)"
    done < <(jq -r --arg t "$type" '.types[$t].files[] | [.path, (.when // "")] | @tsv' "$config")
    if [ "$type" = dotnet-web ]; then
      raw "$repo" scripts/ensure-azure-access.ps1 > /dev/null || gap "no scripts/ensure-azure-access.ps1 (lower-case scripts/)"
    fi
  elif raw "$repo" ProjectSettings/ProjectVersion.txt > /dev/null; then
    gap "Unity game not listed under unity-webgl in scripts/ci-repos.json"
  elif raw "$repo" scripts/ensure-azure-access.ps1 > /dev/null || raw "$repo" SCRIPTS/ensure-azure-access.ps1 > /dev/null; then
    gap "App Service app not listed under dotnet-web in scripts/ci-repos.json"
  fi
done

# The reference copies themselves keep the rules that make them safe.
webgl=$(raw PoBox .github/workflows/webgl.yml)
grep -q 'self-hosted, unity' <<< "$webgl" || gap "PoBox webgl.yml does not run on the self-hosted unity runner"
grep -qE '^[[:space:]]+pull_request:' <<< "$webgl" && gap "PoBox webgl.yml has a pull_request trigger (public repo + self-hosted runner)"
grep -qE 'secrets\.AZURE_(CLIENT|TENANT|SUBSCRIPTION)_ID' <<< "$(raw PoTraffic .github/workflows/deploy.yml)" \
  && gap "PoTraffic deploy.yml reads Azure IDs from secrets, not vars"

echo
if [ "$fails" -gt 0 ]; then echo "$fails gap(s) found."; exit 1; fi
echo "Every Po repo follows the conventions."
