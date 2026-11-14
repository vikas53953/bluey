<#
.SYNOPSIS
  Fails unless a dotnet test run really ran every test its source declares, and all of them passed.

.DESCRIPTION
  Guards a CI gate against a "vacuous green": zero tests discovered, a filter that matched nothing, a
  test skipped or not executed, or fewer tests run than the source declares. Reads the .trx files that
  `dotnet test --logger trx --results-directory <ResultsDirectory>` wrote, and counts [Fact]/[Theory]
  attributes in the test project's own .cs files (comments stripped; [Theory] counts once, so it is a floor).

  Never reads secrets. Exit 0 = gate holds; exit 1 = gate broken (the reason is printed).

.EXAMPLE
  pwsh scripts/ci/assert-tests-ran.ps1 -ResultsDirectory artifacts/demo-evidence/trx -SourceDirectory tests/Boo.Demo.Tests
#>
param(
  [Parameter(Mandatory)] [string] $ResultsDirectory,
  [Parameter(Mandatory)] [string] $SourceDirectory
)
$ErrorActionPreference = 'Stop'

# Full-app CI pins the independent source floor to the integration merge-base.
$sourceTexts = @()
if ($env:QA_BASE_REF) {
  $paths = @(git ls-tree -r --name-only $env:QA_BASE_REF -- $SourceDirectory)
  if ($LASTEXITCODE -ne 0) { throw 'cannot read merge-base sources' }
  foreach ($path in $paths) {
    if ($path -like '*.cs') {
      $text = git show "$($env:QA_BASE_REF):$path"
      if ($LASTEXITCODE -ne 0) { throw 'cannot read anchored source' }
      $sourceTexts += ($text -join "`n")
    }
  }
}
# A project introduced by this PR has no base source; the exact-ID contract independently checks it.
if (-not $env:QA_BASE_REF) {
  $sourceTexts = @(Get-ChildItem $SourceDirectory -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '[\/](bin|obj)[\/]' } |
    ForEach-Object { Get-Content $_.FullName -Raw })
}
$declared = 0
foreach ($code in $sourceTexts) {
  $code = [regex]::Replace($code, '/\*.*?\*/', '', 'Singleline')
  $code = [regex]::Replace($code, '(?m)//.*$', '')
  $declared += [regex]::Matches($code, '\[\s*(Xunit\.)?(Fact|Theory)\b').Count
}

$failures = @()
if ($declared -eq 0 -and -not $env:QA_BASE_REF) { $failures += "no [Fact] or [Theory] found under $SourceDirectory" }

$trx = @(Get-ChildItem $ResultsDirectory -Recurse -Filter *.trx -ErrorAction SilentlyContinue)
if ($trx.Count -eq 0) {
  $failures += "no .trx results under $ResultsDirectory (did dotnet test run at all?)"
}

$total = 0; $executed = 0; $passed = 0; $failed = 0; $notRun = 0
foreach ($file in $trx) {
  [xml] $doc = Get-Content $file.FullName -Raw
  $c = $doc.TestRun.ResultSummary.Counters
  $total    += [int] $c.total
  $executed += [int] $c.executed
  $passed   += [int] $c.passed
  $failed   += [int] $c.failed + [int] $c.error + [int] $c.timeout + [int] $c.aborted
  $notRun   += [int] $c.notExecuted + [int] $c.inconclusive + [int] $c.notRunnable + [int] $c.disconnected
}

"declared in source: $declared   trx files: $($trx.Count)   total: $total   executed: $executed   passed: $passed   failed: $failed   not run/skipped: $notRun"

if ($trx.Count -gt 0) {
  if ($executed -eq 0)          { $failures += 'zero tests executed' }
  if ($failed -gt 0)            { $failures += "$failed test(s) failed" }
  if ($notRun -gt 0)            { $failures += "$notRun test(s) skipped or not executed" }
  if ($passed -lt $declared)    { $failures += "only $passed passed but the source declares $declared" }
  if ($executed -lt $total)     { $failures += "$($total - $executed) discovered test(s) never executed" }
}

if ($failures.Count -gt 0) {
  $failures | ForEach-Object { "::error::test gate: $_" }
  exit 1
}
'test gate: every declared test ran and passed'
