param([string]$Workspace = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$keyFile = Join-Path $Workspace '.env.local'
$credential = $null
if (Test-Path -LiteralPath $keyFile) {
    foreach ($line in [IO.File]::ReadAllLines($keyFile)) {
        if ($line -match '^OPENAI_API_KEY=(.+)$') { $credential = $Matches[1].Trim().Trim('"').Trim("'") }
    }
}
if ([string]::IsNullOrWhiteSpace($credential)) { Write-Output 'PROVIDER_NOT_CONFIGURED'; exit 2 }
try {
    $reply = Invoke-RestMethod -Method Get -Uri 'https://api.openai.com/v1/models' -Headers @{ Authorization = ('Bearer ' + $credential) } -TimeoutSec 30
    $modelNames = @($reply.data | ForEach-Object { $_.id } | Where-Object { $_ -match '^(gpt-realtime|gpt-6|gpt-4.1|gpt-4o)' } | Sort-Object)
    [pscustomobject]@{ authenticated = $true; modelNames = $modelNames } | ConvertTo-Json -Depth 3
} catch {
    $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
    [pscustomobject]@{ authenticated = $false; httpStatus = $status; message = 'Provider validation failed; no credentials or response body logged.' } | ConvertTo-Json
    exit 1
} finally {
    $credential = $null
}
