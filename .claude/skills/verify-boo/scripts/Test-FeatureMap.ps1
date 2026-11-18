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
    - a [Fact]/[Theory] in a live or native project ($LiveDirs and tests/Boo.Demo.Tests below) is not cited by any row
      (a new live check nobody mapped);
    - two rows share an ID.
  Rows come from the frozen table in docs/FEATURE_MAP.md PLUS one fragment file per feature in docs/feature-map/
  (<named-id>.md, BOO-DOCS-001). A fragment row counts exactly like a table row. A fragment's `Replaces: <old id>` line
  drops that frozen row (its cites no longer count); replacing an id that is not in the table, or one id twice, is
  drift, as is a fragment whose name is not a named id or whose row is not exactly one row starting with that id.
  Read-only. Works in Windows PowerShell 5.1 and PowerShell 7.

.EXAMPLE
  powershell -NoProfile -File .claude/skills/verify-boo/scripts/Test-FeatureMap.ps1
#>
param([string] $MapPath, [string] $FragmentDir)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
if (-not $MapPath) { $MapPath = Join-Path $repo 'docs\FEATURE_MAP.md' }
if (-not $FragmentDir) { $FragmentDir = Join-Path $repo 'docs\feature-map' }
$labels = 'automated live', 'automated mock', 'manual', 'UNVERIFIED'
$problems = [System.Collections.Generic.List[string]]::new()
$ids = @{}
$rows = 0
$cited = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

# The ONE list of projects whose tests drive the real, published Boo.exe. A row is "automated live" only when it cites
# a [Fact]/[Theory] from one of them, and every test in them must be on the map. A new native project is added here once.
$LiveDirs = @('tools/Boo.VerifyLive', 'tools/Boo.LiveSmoke', 'tools/Boo.CrashReopen', 'tools/Boo.NativeVoice', 'tests/Boo.Ui.Tests', 'tests/Boo.Perf.Tests')
# Native but mock-AI (demo-ci): must be mapped, never makes a row live.
$NativeDirs = $LiveDirs + @('tests/Boo.Demo.Tests')
$LiveDirPattern = '^(' + (($LiveDirs | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')/'

# Every row to check, from the frozen table and from the fragment files: @{ Id; Line }.
$rowList = [System.Collections.Generic.List[object]]::new()
$inMap = $false
foreach ($line in Get-Content $MapPath -Encoding UTF8) {
  # Everything under "## The map" that looks like a table row must be a header, a separator or a proper map row:
  # a mistyped ID ("NTR8") or a stray space must be reported, never silently skipped.
  if ($line -match '^##\s') { $inMap = ($line -match '^##\s+The map') }
  if ($inMap -and $line -match '^\s*\|' -and $line -notmatch '^\|\s*#\s*\|' -and $line -notmatch '^\|\s*-' -and
      $line -notmatch '^\|\s*([A-Z]{1,2}\d+)\s*\|') {
    $problems.Add("malformed map row (ID must be 1-2 capital letters and a number, at the start of the line): $($line.Substring(0, [Math]::Min(60, $line.Length)))")
    continue
  }
  # Map rows look like: | N1 | feature | how verified | proof |
  if ($line -notmatch '^\|\s*([A-Z]{1,2}\d+)\s*\|') { continue }
  $rowList.Add(@{ Id = $Matches[1]; Line = $line })
}

# Fragment files: docs/feature-map/<named-id>.md, one row each (BOO-DOCS-001; scripts/quality/doc_fragments.py checks
# their shape on every PR, this checks what they claim).
$replaced = @{}
$fragmentRows = [System.Collections.Generic.List[object]]::new()
if (Test-Path $FragmentDir) {
  foreach ($f in Get-ChildItem $FragmentDir -File | Sort-Object Name) {
    $fid = [IO.Path]::GetFileNameWithoutExtension($f.Name)
    if ($f.Extension -cne '.md' -or $fid -cnotmatch '^[a-z][a-z0-9]*(-[a-z0-9]+)+$') {
      $problems.Add("feature-map fragment $($f.Name) is not named <named-id>.md (lowercase words joined by -)"); continue
    }
    $fragLines = @(Get-Content $f.FullName -Encoding UTF8 | Where-Object { $_ -match '^\s*\|' -and $_ -notmatch '^\|\s*#\s*\|' -and $_ -notmatch '^\|\s*-' })
    if ($fragLines.Count -ne 1) { $problems.Add("feature-map fragment $($f.Name) must hold exactly one map row (it has $($fragLines.Count))"); continue }
    $firstCell = ($fragLines[0].Trim().Trim('|').Split('|'))[0].Trim()
    if ($firstCell -cne $fid) { $problems.Add("feature-map fragment $($f.Name): the row's id cell must be '$fid' (it is '$firstCell')"); continue }
    foreach ($meta in Get-Content $f.FullName -Encoding UTF8) {
      if ($meta -match '^Replaces:\s*(\S+)\s*$') {
        $old = $Matches[1]
        if ($replaced.ContainsKey($old)) { $problems.Add("$old is replaced by two fragments ($($replaced[$old]) and $($f.Name))") }
        else { $replaced[$old] = $f.Name }
      }
    }
    $fragmentRows.Add(@{ Id = $fid; Line = $fragLines[0] })
  }
}
foreach ($old in @($replaced.Keys)) {
  if (-not ($rowList | Where-Object { $_.Id -ceq $old })) { $problems.Add("fragment $($replaced[$old]) replaces $old, which is not a row of the frozen map table") }
}
$rowList = @($rowList | Where-Object { -not $replaced.ContainsKey($_.Id) }) + @($fragmentRows)

foreach ($r in $rowList) {
  $line = $r.Line
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
        # The cited method must be a TEST ([Fact]/[Theory]), not a helper that merely exists in the file.
        $isTest = $code -match "\[\s*(Xunit\.)?(Fact|Theory)\b[^\]]*\]\s*(\[[^\]]*\]\s*)*public\s+(async\s+)?\w+\s+$([regex]::Escape($method))\s*\("
        if ($code -notmatch "\b$([regex]::Escape($method))\s*\(") { $problems.Add("$id cites ${relFile}::$method, but no such method is in that file") }
        elseif (-not $isTest) { $problems.Add("$id cites ${relFile}::$method, which is not a [Fact]/[Theory] test") }
        [void] $cited.Add("${relFile}::$method")
        if ($isTest -and $relFile -match $LiveDirPattern) { $liveCited = $true }
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
    $problems.Add("$id claims 'automated live' but cites no file.cs::Method from $($LiveDirs -join ', ')")
  }
}

if ($rows -eq 0) { $problems.Add("no map rows found in $MapPath") }

# Every live or native UI test must be on the map, so a new live check cannot hide from it.
foreach ($dir in $NativeDirs) {
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
