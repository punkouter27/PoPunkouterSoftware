<#
.SYNOPSIS
    Copies the shared CI/CD files from each app type's reference repo to every other repo of that type.

.DESCRIPTION
    Every Po repo keeps its own workflow files, but all repos of one app type hold IDENTICAL copies
    (scripts/ci-repos.json lists the types, their reference repo and the shared files). Change the
    reference repo's file, then run this to copy it everywhere. Repo-specific values never go in
    these files: they are repo variables, Tools/webgl.json, or hook scripts.

    Expects the repos side by side as sibling folders of this one (…\_CODEMATT\<Repo>).
    It never pushes: a push to master deploys. With -Commit it commits each changed repo locally.

.EXAMPLE
    ./scripts/sync-ci.ps1                 # copy, show what changed
    ./scripts/sync-ci.ps1 -Check          # change nothing; exit 1 if any copy differs
    ./scripts/sync-ci.ps1 -Type unity-webgl -Commit
#>
[CmdletBinding()]
param(
    [string] $Type,
    [switch] $Check,
    [switch] $Commit,
    [string] $Message = 'synced up the shared ci files'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$parent = Split-Path -Parent $repoRoot
$config = Get-Content (Join-Path $PSScriptRoot 'ci-repos.json') -Raw | ConvertFrom-Json
$drift = 0

foreach ($typeName in $config.types.PSObject.Properties.Name) {
    if ($Type -and $typeName -ne $Type) { continue }
    $t = $config.types.$typeName
    $refDir = Join-Path $parent $t.reference
    "== $typeName (reference: $($t.reference))"

    foreach ($repo in $t.repos) {
        $dir = Join-Path $parent $repo
        if (-not (Test-Path $dir)) { "  ${repo}: not cloned next to this repo, skipped"; continue }
        $changed = @()
        foreach ($f in $t.files) {
            if ($f.when -and -not (Test-Path (Join-Path $dir $f.when))) { continue }
            $src = Join-Path $refDir $f.path
            $dst = Join-Path $dir $f.path
            $srcText = [IO.File]::ReadAllText($src)
            $same = (Test-Path $dst) -and ([IO.File]::ReadAllText($dst) -ceq $srcText)
            if ($same) { continue }
            $changed += $f.path
            if (-not $Check -and $repo -ne $t.reference) {
                New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
                [IO.File]::WriteAllText($dst, $srcText)   # byte-for-byte, no BOM, no newline rewrite
            }
        }
        if ($changed.Count -eq 0) { "  ${repo}: in sync"; continue }
        $drift++
        "  ${repo}: $(if ($Check) { 'DIFFERS' } else { 'updated' }) -> $($changed -join ', ')"
        if ($Commit -and -not $Check) {
            git -C $dir add -- $changed
            git -C $dir commit -m $Message --quiet
            "    committed (not pushed)"
        }
    }
}

if ($Check -and $drift -gt 0) { "$drift repo(s) differ from their reference."; exit 1 }
