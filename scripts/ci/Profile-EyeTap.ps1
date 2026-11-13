# One no-key cold Q10 attempt; collector failure never replaces or hides the test result.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'GitHub hosted VM required.' }
$folder = Join-Path $env:GITHUB_WORKSPACE 'artifacts/eye-profile'
New-Item -ItemType Directory -Force $folder | Out-Null
$wpr = Get-Command wpr.exe -ErrorAction SilentlyContinue
$started = $false
$collectorOk = $false
$testExit = 99
$testError = $null
try {
    try {
    if ($wpr) {
        (Get-Item $wpr.Source).VersionInfo | Format-List | Out-File "$folder/wpr-version.txt"
        & $wpr.Source -profiles 2>&1 | Out-File "$folder/wpr-profiles.txt"
        & $wpr.Source -profiledetails GeneralProfile 2>&1 | Out-File "$folder/general-profile.txt"
        $general = $LASTEXITCODE
        & $wpr.Source -profiledetails DotNET 2>&1 | Out-File "$folder/dotnet-profile.txt"
        $clr = $LASTEXITCODE
        if ($general -eq 0 -and $clr -eq 0) {
            & $wpr.Source -start GeneralProfile -start DotNET -filemode 2>&1 | Out-File "$folder/wpr-start.txt"
            $started = $LASTEXITCODE -eq 0
        }
    } else { 'UNVERIFIED: WPR unavailable; test still runs once.' | Set-Content "$folder/collector.txt" }
    } catch { $_.ToString() | Set-Content "$folder/collector-setup-error.txt" }
    try {
        & dotnet test reviewed-harness/tools/Boo.QaSuite -c Release --no-build --filter 'FullyQualifiedName=Boo.QaSuite.Journeys.EyeTapTests.Q10_PhysicalEyeTapUnder200Ms_OpensPanelWithoutListening' --logger trx --logger 'console;verbosity=normal' --results-directory "$folder/trx"
        $testExit = $LASTEXITCODE
    } catch { $testError = $_.ToString() }
} finally {
    try {
    if ($started) {
        & $wpr.Source -status collectors -details 2>&1 | Out-File "$folder/wpr-status-before-stop.txt"
        & $wpr.Source -stop "$folder/eye-tap.etl" 2>&1 | Out-File "$folder/wpr-stop.txt"
        $collectorOk = $LASTEXITCODE -eq 0 -and (Test-Path "$folder/eye-tap.etl")
    }
    } catch { $_.ToString() | Set-Content "$folder/collector-stop-error.txt" }
    [ordered]@{ test_exit=$testExit; test_error=$testError; collector_started=$started; collector_complete=$collectorOk; scope='diagnostic only; no acceptance measurement'; app=$env:APP_SHA; harness=$env:HARNESS_SHA } | ConvertTo-Json | Set-Content "$folder/verdict.json"
}
# Enforce exactly the intended test ran; missing/skipped/extra results fail closed.
$results = @(Get-ChildItem "$folder/trx" -Filter '*.trx' -ErrorAction SilentlyContinue)
if ($results.Count -ne 1) { throw 'Expected one TRX from the single attempt.' }
[xml]$trx = Get-Content $results[0].FullName
$ns = [Xml.XmlNamespaceManager]::new($trx.NameTable)
$ns.AddNamespace('t','http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
$rows = @($trx.SelectNodes('//t:UnitTestResult',$ns))
if ($rows.Count -ne 1 -or $rows[0].testName -ne 'Boo.QaSuite.Journeys.EyeTapTests.Q10_PhysicalEyeTapUnder200Ms_OpensPanelWithoutListening' -or $rows[0].outcome -notin @('Passed','Failed')) { throw 'Exact single Q10 execution required, no skipped results.' }
if ($testExit -ne 0) { exit $testExit }
if (-not $collectorOk) { throw 'Collector unavailable or failed; profiling UNVERIFIED despite test result.' }
