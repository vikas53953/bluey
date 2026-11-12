<#
.SYNOPSIS
  verify-boo: run the REAL published Boo.exe like a user (FlaUI) and keep the evidence.

.DESCRIPTION
  Reuses the existing harness only; it adds no test of its own:
    ui    tests/Boo.Ui.Tests    hotkey + hover wake, focus start/pause/resume/end, Ctrl+Alt+S / F12 pause, appearance,
                                start-with-Windows, accessibility, corrupt-data startup. No AI, no key.
    live  tools/Boo.LiveSmoke   one real AI round trip, then
          tools/Boo.VerifyLive  the live journeys (hotkey, hover, chat, explain ring, research, Apply/Undo,
                                25-minute Developer Mode focus). Needs BOO_LIVE_<PROVIDER>_KEY in the environment.
  Every suite is gated by scripts/ci/assert-tests-ran.ps1 (every declared test ran and passed: no skip, no empty run).
  The result per suite is PASS, FAIL or BLOCKED (could not run, for example no key). BLOCKED is never a pass.

  Evidence goes to artifacts/verify/<UTC time>/<suite>/ (screenshots *.png, observations *.txt, trx) plus summary.md.
  Keys: read only from the environment, never printed (length only), and the evidence is scanned for them at the end.

  WARNING: it drives this PC's real mouse and keyboard for several minutes (live: about 15). Do not touch the PC while
  it runs, and quit any running Boo first (Boo is single-instance).

.EXAMPLE
  pwsh .claude/skills/verify-boo/scripts/Invoke-VerifyBoo.ps1 -Suite ui
.EXAMPLE
  $env:BOO_LIVE_OPENAI_KEY = '<from your password manager>'; pwsh .claude/skills/verify-boo/scripts/Invoke-VerifyBoo.ps1 -Suite live
#>
param(
  [ValidateSet('ui', 'live', 'all')] [string] $Suite = 'ui',
  [ValidateSet('openai', 'anthropic', 'gemini')] [string] $Provider = 'openai',
  # Use an already published Boo.exe instead of publishing one from this checkout.
  [string] $Exe,
  [string] $OutDir
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
Set-Location $repo
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
if (-not $OutDir) { $OutDir = Join-Path $repo "artifacts\verify\$stamp" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$results = [System.Collections.Generic.List[object]]::new()
function Say($text) { Write-Host "[verify-boo] $text" }

# --- preconditions ---------------------------------------------------------------------------------------------
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet is not on PATH. Install the SDK named in global.json.' }
$sdk = (Get-Content global.json -Raw | ConvertFrom-Json).sdk.version
if (-not ((& dotnet --list-sdks) -match [regex]::Escape($sdk))) { throw "The .NET SDK $sdk from global.json is not installed." }
$running = @(Get-Process Boo -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) { throw "Boo is already running (pid $($running.Id -join ', ')). Quit it from the tray first: Boo is single-instance." }
$sha = (git rev-parse HEAD).Trim()
$dirty = [bool](git status --porcelain)
Say "commit $sha$(if ($dirty) { ' (with uncommitted changes)' })"

# --- the app under test: the published Boo.exe -------------------------------------------------------------------
if ($Exe) {
  $Exe = (Resolve-Path $Exe).Path
} else {
  $publish = Join-Path $repo 'artifacts\verify\app'
  Say "publishing Boo to $publish"
  & dotnet publish src/Boo.App/Boo.App.csproj -c Release -r win-x64 --self-contained true -o $publish --nologo -v q
  if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
  $Exe = Join-Path $publish 'Boo.exe'
}
Say "app under test: $Exe"

function Invoke-Suite([string] $name, [string] $project, [hashtable] $extraEnv) {
  $evidence = Join-Path $OutDir $name
  $trx = Join-Path $evidence 'trx'
  New-Item -ItemType Directory -Force -Path $trx | Out-Null
  Say "building $project"
  & dotnet build $project -c Release --nologo -v q
  if ($LASTEXITCODE -ne 0) { $results.Add([pscustomobject]@{ Suite = $name; Project = $project; Result = 'FAIL'; Why = 'build failed' }); return }
  $saved = @{}
  $env_ = @{ BOO_UI_EXE = $Exe; BOO_UI_ARTIFACTS = $evidence } + $extraEnv
  foreach ($k in $env_.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $env_[$k]) }
  try {
    Say "running $project (hands off the mouse and keyboard)"
    & dotnet test $project -c Release --no-build --logger trx --logger 'console;verbosity=normal' --results-directory $trx |
      Tee-Object -FilePath (Join-Path $evidence 'console.log') | Out-Host
    $testExit = $LASTEXITCODE
  } finally {
    foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
  }
  & (Join-Path $repo 'scripts\ci\assert-tests-ran.ps1') -ResultsDirectory $trx -SourceDirectory $project 2>&1 |
    Tee-Object -FilePath (Join-Path $evidence 'gate.log') | Out-Host
  $gateExit = $LASTEXITCODE
  $result = if ($testExit -eq 0 -and $gateExit -eq 0) { 'PASS' } else { 'FAIL' }
  $why = if ($result -eq 'PASS') { 'every declared test ran and passed' } else { "dotnet test exit $testExit, gate exit $gateExit (see console.log, gate.log, *-FAILED.png)" }
  $results.Add([pscustomobject]@{ Suite = $name; Project = $project; Result = $result; Why = $why })
}

if ($Suite -in 'ui', 'all') {
  Invoke-Suite 'ui-smoke' 'tests/Boo.Ui.Tests' @{}
}

if ($Suite -in 'live', 'all') {
  $keyVar = "BOO_LIVE_$($Provider.ToUpperInvariant())_KEY"
  $key = [Environment]::GetEnvironmentVariable($keyVar)
  $hostName = @{ openai = 'api.openai.com'; anthropic = 'api.anthropic.com'; gemini = 'generativelanguage.googleapis.com' }[$Provider]
  $ips = @(try { [System.Net.Dns]::GetHostAddresses($hostName) | ForEach-Object { $_.ToString() } } catch { })
  if ([string]::IsNullOrWhiteSpace($key)) {
    foreach ($n in 'live-smoke', 'live-journeys') { $results.Add([pscustomobject]@{ Suite = $n; Project = '-'; Result = 'BLOCKED'; Why = "$keyVar is not set, so no real AI round trip can run (use the live-ai-smoke workflow, or set the variable in this shell only)" }) }
  } elseif ($ips.Count -eq 0 -or ($ips | Where-Object { $_ -match '^(127\.|0\.|::1$)' })) {
    foreach ($n in 'live-smoke', 'live-journeys') { $results.Add([pscustomobject]@{ Suite = $n; Project = '-'; Result = 'BLOCKED'; Why = "$hostName resolves to '$($ips -join ', ')': not a real, reachable provider (a mock redirect would be a false green)" }) }
  } else {
    Say "$keyVar is present ($($key.Trim().Length) characters); $hostName -> $($ips -join ', ')"
    $live = @{ BOO_LIVE_TEXT_PROVIDER = $Provider }
    Invoke-Suite 'live-smoke' 'tools/Boo.LiveSmoke' $live
    Invoke-Suite 'live-journeys' 'tools/Boo.VerifyLive' $live
  }
}

# --- key leak scan (any BOO_LIVE_*_KEY in this environment) -------------------------------------------------------
$keys = @('BOO_LIVE_OPENAI_KEY', 'BOO_LIVE_ANTHROPIC_KEY', 'BOO_LIVE_GEMINI_KEY') |
  ForEach-Object { [Environment]::GetEnvironmentVariable($_) } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim() }
foreach ($f in Get-ChildItem $OutDir -Recurse -File) {
  $text = [System.IO.File]::ReadAllText($f.FullName)
  foreach ($k in $keys) {
    if ($text.Contains($k)) { Remove-Item $f.FullName -Force; $results.Add([pscustomobject]@{ Suite = 'key-leak-scan'; Project = '-'; Result = 'FAIL'; Why = "a key was found in $($f.Name); the file was deleted" }); break }
  }
}

# --- summary --------------------------------------------------------------------------------------------------------
$shots = Get-ChildItem $OutDir -Recurse -Filter *.png | Sort-Object FullName
$lines = @(
  "# verify-boo run $stamp (UTC)", '',
  "- Commit: ``$sha``$(if ($dirty) { ' (uncommitted changes present: not a clean proof)' })",
  "- App under test: ``$Exe``",
  "- Machine: $env:COMPUTERNAME, $([Environment]::OSVersion.VersionString)", '',
  '| Suite | Project | Result | Why |', '| --- | --- | --- | --- |'
) + ($results | ForEach-Object { "| $($_.Suite) | ``$($_.Project)`` | **$($_.Result)** | $($_.Why) |" }) + @(
  '', "Screenshots ($($shots.Count)):", ''
) + ($shots | ForEach-Object { "- ``$($_.FullName.Substring($OutDir.Length + 1))``" })
$summary = Join-Path $OutDir 'summary.md'
$lines | Set-Content -Path $summary -Encoding utf8
$results | Format-Table -AutoSize | Out-Host
Say "evidence: $OutDir"
Say "summary:  $summary"
if ($results.Count -eq 0 -or ($results | Where-Object { $_.Result -ne 'PASS' })) { exit 1 }
exit 0
