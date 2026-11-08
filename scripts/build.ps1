param(
    [ValidateSet('restore','build','test','publish','format')][string]$Action = 'test',
    [string]$Project = 'Boo.sln',
    [string]$Filter,
    [switch]$NoRestore,
    [string]$Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'
$checkout = Split-Path -Parent $PSScriptRoot
$commonGit = & git -C $checkout rev-parse --path-format=absolute --git-common-dir
if ($LASTEXITCODE -ne 0) { throw 'Run this wrapper from a Boo git checkout.' }
$canonical = Split-Path -Parent $commonGit.Trim()
$sdkRoot = Join-Path $canonical '.dotnet'
$dotnetExe = Join-Path $sdkRoot 'dotnet.exe'
if (!(Test-Path -LiteralPath $dotnetExe)) { throw 'The pinned .NET SDK is missing from the canonical workspace.' }
$env:DOTNET_ROOT = $sdkRoot
$env:DOTNET_CLI_HOME = Join-Path $canonical '.tools/cli'
$env:NUGET_PACKAGES = Join-Path $canonical '.nuget/packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$arguments = @($Action, (Join-Path $checkout $Project))
if ($Action -in @('build','test','publish')) { $arguments += @('-c', $Configuration) }
if ($NoRestore -and $Action -ne 'restore') { $arguments += '--no-restore' }
if ($Action -eq 'test') {
    $arguments += @('--logger','trx','--results-directory',(Join-Path $checkout 'TestResults'))
    if ($Filter) { $arguments += @('--filter', $Filter) }
}
if ($Action -eq 'publish') { $arguments += @('-r','win-x64','--self-contained','true','-o',(Join-Path $checkout 'artifacts/candidate')) }
if ($Action -eq 'format') { $arguments += @('--verify-no-changes','--no-restore') }
& $dotnetExe @arguments
exit $LASTEXITCODE
