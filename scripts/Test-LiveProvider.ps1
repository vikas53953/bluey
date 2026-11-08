param([string]$Model = 'gpt-6.1-sol')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$credential = $null
foreach ($line in [IO.File]::ReadAllLines((Join-Path $workspace '.env.local'))) {
    if ($line -match '^OPENAI_API_KEY=(.+)$') { $credential = $Matches[1].Trim().Trim('"').Trim("'") }
}
if ([string]::IsNullOrWhiteSpace($credential)) { throw 'Provider is not configured.' }
$body = @{ model=$Model; input='Reply with exactly BOO_LIVE_OK. No other text.'; reasoning=@{effort='low'}; max_output_tokens=512; store=$false } | ConvertTo-Json -Depth 5
$timer = [Diagnostics.Stopwatch]::StartNew()
try {
    $reply = Invoke-RestMethod -Method Post -Uri 'https://api.openai.com/v1/responses' -Headers @{Authorization=('Bearer '+$credential)} -ContentType 'application/json' -Body $body -TimeoutSec 60
    $text = @($reply.output | Where-Object type -eq 'message' | ForEach-Object { $_.content } | Where-Object type -eq 'output_text' | ForEach-Object { $_.text }) -join ''
    $result = [ordered]@{ success=($text.Trim() -eq 'BOO_LIVE_OK'); model=$reply.model; status=$reply.status; elapsedMilliseconds=$timer.ElapsedMilliseconds; outputMatches=($text.Trim() -eq 'BOO_LIVE_OK'); inputTokens=$reply.usage.input_tokens; outputTokens=$reply.usage.output_tokens; utc=[DateTime]::UtcNow.ToString('O') }
} catch {
    $httpStatus = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
    $code = 'network_or_unknown'
    try { $parsed = $_.ErrorDetails.Message | ConvertFrom-Json; if ($parsed.error.code -match '^[a-z_]+$') { $code = $parsed.error.code } } catch { }
    $result = [ordered]@{ success=$false; model=$Model; httpStatus=$httpStatus; errorCode=$code; elapsedMilliseconds=$timer.ElapsedMilliseconds; utc=[DateTime]::UtcNow.ToString('O') }
} finally { $credential=$null }
$json = $result | ConvertTo-Json
$json | Set-Content (Join-Path $workspace 'artifacts/live-provider-probe.json')
$json
if (!$result.success) { exit 1 }
