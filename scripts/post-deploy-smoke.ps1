<#
.SYNOPSIS
    Post-deploy smoke hook for the shared deploy.yml: runs after its health gate has proved this
    commit is live and /health answers. A failure here rolls the deploy back.

.DESCRIPTION
    Checks the one route the shared gate does not: /api/diag/summary, the /azure page's only
    first-paint fetch. 200 once a scan has stored a report; 404 before the first scan is fine.

.EXAMPLE
    ./scripts/post-deploy-smoke.ps1 -BaseUrl https://app-popunkoutersoftware2.azurewebsites.net
#>
param([Parameter(Mandatory)][string] $BaseUrl)

$ErrorActionPreference = 'Stop'
$url = "$($BaseUrl.TrimEnd('/'))/api/diag/summary"
foreach ($attempt in 1..6) {
    try {
        $code = (Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 60 -SkipHttpErrorCheck).StatusCode
    } catch { $code = 0 }
    if ($code -in 200, 404) { "/api/diag/summary -> $code (attempt $attempt)"; exit 0 }
    Start-Sleep 10
}
throw "/api/diag/summary answered $code, expected 200 or 404."
