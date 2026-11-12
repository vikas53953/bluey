<#
.SYNOPSIS
  verify-boo maintenance check: docs/FEATURE_MAP.md still tells the truth about the code.

.DESCRIPTION
  Fails (exit 1) when the map has drifted:
    - a row has no ID, no feature, or a "How verified" value that is not made only of the allowed labels
      (automated live, automated mock, manual, UNVERIFIED; exact case; parts joined by "+"; notes in brackets);
    - a row says "automated live" but cites no test method that drives the real Boo.exe;
    - a row cites a file that does not exist, or `file.cs::Method` where the method is not in that file;
    - a row claims "automated live" or "automated mock" but cites no test, workflow or script as proof;
    - a [Fact]/[Theory] in a live user-journey project (tools/Boo.VerifyLive, tools/Boo.LiveSmoke) or a native UI suite
      (tests/Boo.Ui.Tests, tests/Boo.Demo.Tests, tests/Boo.Perf.Tests) is not cited by any row
      (a new live check nobody mapped);
    - two rows share an ID.
  Read-only. Works in Windows PowerShell 5.1 and PowerShell 7.

.EXAMPLE
  powershell -NoProfile -File .claude/skills/verify-boo/scripts/Test-FeatureMap.ps1
#>
param([string] $MapPath)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
if (-not $MapPath) { $MapPath = Join-Path $repo 'docs\FEATURE_MAP.md' }
$labels = 'automated live', 'automated mock', 'manual', 'UNVERIFIED'
$problems = [System.Collections.Generic.List[string]]::new()
$ids = @{}
$rows = 0
$cited = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

foreach ($line in Get-Content $MapPath -Encoding UTF8) {
  # Map rows look like: | N1 | feature | how verified | proof |
  if ($line -notmatch '^\|\s*([A-Z]{1,2}\d+)\s*\|') { continue }
  $cells = $line.Trim().Trim('|').Split('|') | ForEach-Object { $_.Trim() }
  $id = $cells[0]
  $rows++
  if ($cells.Count -lt 4) { $problems.Add("$id has $($cells.Count) cells; expected ID | Feature | How verified | Proof"); continue }
  $feature = $cells[1]; $how = $cells[2]; $proof = ($cells[3..($cells.Count - 1)] -join '|')
  if ($ids.ContainsKey($id)) { $problems.Add("$id is used twice") } else { $ids[$id] = $true }
  if (-not $feature) { $problems.Add("$id has no feature text") }
  # Strict: drop "(notes)", split on "+", and every part must be exactly one label (case-sensitive). "probably
  # automated live" or "not manual, unverified-ish" are rejected, not matched loosely.
  $parts = @((($how -replace '\([^)]*\)', '') -split '\+') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
  $found = @($parts | Where-Object { $labels -ccontains $_ })
  if ($parts.Count -eq 0 -or $found.Count -ne $parts.Count) { $problems.Add("$id 'How verified' is '$how'; each '+'-separated part must be exactly one of: $($labels -join ', ')") }
  $liveCited = $false

  $refs = [regex]::Matches($proof, '`([^`]+)`') | ForEach-Object { $_.Groups[1].Value }
  $hasProof = $false
  foreach ($ref in $refs) {
    if ($ref -match '^(?<file>[\w./\\-]+\.(cs|ps1|yml|md|json))(::(?<method>\w+))?$') {
      # Copy the captures first: the -notmatch below overwrites $Matches.
      $relFile = $Matches['file'] -replace '\\', '/'
      $method = $Matches['method']
      $file = Join-Path $repo $relFile
      if (-not (Test-Path $file)) { $problems.Add("$id cites $relFile, which does not exist"); continue }
      $hasProof = $true
      if ($method) {
        $code = Get-Content $file -Raw
        if ($code -notmatch "\b$([regex]::Escape($method))\s*\(") { $problems.Add("$id cites ${relFile}::$method, but no such method is in that file") }
        [void] $cited.Add("${relFile}::$method")
        if ($relFile -match '^(tools/Boo\.VerifyLive|tools/Boo\.LiveSmoke|tests/Boo\.Ui\.Tests|tests/Boo\.Perf\.Tests)/') { $liveCited = $true }
      }
    } elseif ($ref -match 'gh workflow run|Boo\.exe --notch-gallery|text_provider=|SpeechEnabled|BOO_LIVE_') {
      $hasProof = $true
    }
  }
  if ($proof -match 'windows-ci|demo-ci|live-ai-smoke|release\.yml|live smoke|live journeys') { $hasProof = $true }
  if (($how -cmatch 'automated (live|mock)') -and -not $hasProof) { $problems.Add("$id claims '$how' but cites no test, workflow or script") }
  # "automated live" must name a test method that drives the real Boo.exe (live journeys, live smoke, ui smoke, perf).
  # demo-ci (mock AI) and unit tests never make a row live.
  if (($parts -ccontains 'automated live') -and -not $liveCited) {
    $problems.Add("$id claims 'automated live' but cites no file.cs::Method from tools/Boo.VerifyLive, tools/Boo.LiveSmoke, tests/Boo.Ui.Tests or tests/Boo.Perf.Tests")
  }
}

if ($rows -eq 0) { $problems.Add("no map rows found in $MapPath") }

# Every live or native UI test must be on the map, so a new live check cannot hide from it.
foreach ($dir in 'tools/Boo.VerifyLive', 'tools/Boo.LiveSmoke', 'tests/Boo.Ui.Tests', 'tests/Boo.Demo.Tests', 'tests/Boo.Perf.Tests') {
  foreach ($f in Get-ChildItem (Join-Path $repo $dir) -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }) {
    $code = Get-Content $f.FullName -Raw
    $rel = $f.FullName.Substring($repo.Length + 1) -replace '\\', '/'
    foreach ($m in [regex]::Matches($code, '\[\s*(Fact|Theory)\b[^\]]*\]\s*(\[[^\]]*\]\s*)*public\s+(async\s+)?\w+\s+(?<name>\w+)\s*\(')) {
      $key = "$rel::$($m.Groups['name'].Value)"
      if (-not $cited.Contains($key)) { $problems.Add("$key is a live/native test that no map row cites") }
    }
  }
}

"feature map: $rows rows, $($cited.Count) test methods cited"
if ($problems.Count -gt 0) {
  $problems | ForEach-Object { "DRIFT: $_" }
  exit 1
}
'feature map: OK (every row labelled, every cited file and method exists, every live/native test is mapped)'
exit 0
