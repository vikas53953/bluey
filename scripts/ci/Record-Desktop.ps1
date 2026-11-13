<#
.SYNOPSIS
  Records the CI runner's desktop to an MP4 while the demo tests run (demo-ci only).

.DESCRIPTION
  -Start  downloads a pinned ffmpeg build, checks its SHA-256, and starts recording the whole desktop in the
          background (10 frames a second, 1280 px wide, H.264 CRF 35). A small helper copy of this script stays
          alive next to ffmpeg so the recording can be stopped cleanly from a later step.
  -Stop   asks ffmpeg to finish (it presses 'q' for it, so the MP4 is closed properly), waits, prints the size and
          fails if the file is missing, broken or above the 25 MB cap (an oversized file is deleted).

  BOO-SAFE-001: it refuses to do anything unless it runs on a GitHub-hosted runner (RUNNER_ENVIRONMENT is
  'github-hosted'). On anyone's own PC it stops before touching ffmpeg, so it can never record a real person's screen.

.EXAMPLE
  pwsh scripts/ci/Record-Desktop.ps1 -Start -OutFile artifacts/demo-video/demo.mp4
  pwsh scripts/ci/Record-Desktop.ps1 -Stop
#>
param(
  [switch] $Start,
  [switch] $Stop,
  [string] $OutFile,
  [int] $StopTimeoutSeconds = 60,
  # Internal: the background helper that owns ffmpeg. Not for direct use.
  [switch] $Supervise,
  [string] $FfmpegPath
)
$ErrorActionPreference = 'Stop'

# ---- Safety first: nothing below this line runs off a GitHub-hosted runner (BOO-SAFE-001). ----
if ($env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:GITHUB_ACTIONS -ne 'true') {
  Write-Host 'Record-Desktop refused: desktop video is only allowed inside GitHub Actions on a GitHub-hosted runner (GITHUB_ACTIONS=true, RUNNER_ENVIRONMENT=github-hosted).'
  Write-Host 'It never records a personal PC (rule BOO-SAFE-001). Nothing was started.'
  exit 2
}

# Pinned ffmpeg: Gyan's "essentials" Windows build 9.0.2 (includes gdigrab and libx264), from its GitHub release.
# The SHA-256 matches both GitHub's asset digest and gyan.dev's published .sha256 file (checked 13 Nov 2026).
$FfmpegUrl    = 'https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip'
$FfmpegSha256 = '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'
$FfmpegEntry  = 'ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe'
$MaxBytes     = 25MB                       # hard cap from the agent-trust plan, section 3.2 F
$MaxSeconds   = 1800                       # ffmpeg stops by itself after 30 min even if nobody stops it

if (-not $env:RUNNER_TEMP) { throw 'RUNNER_TEMP is not set; this script only runs inside GitHub Actions.' }
$State   = Join-Path $env:RUNNER_TEMP 'boo-recorder'     # helper files; never inside the uploaded folder
$Log     = Join-Path $State 'ffmpeg.log'
$StopReq = Join-Path $State 'stop.request'
$ExitTxt = Join-Path $State 'ffmpeg.exit'
$PidTxt  = Join-Path $State 'ffmpeg.pid'
$OutTxt  = Join-Path $State 'outfile.txt'

if ($Supervise) {
  # Background helper: start ffmpeg with a pipe on its keyboard input, wait for the stop request, then send 'q'.
  $psi = [System.Diagnostics.ProcessStartInfo]::new($FfmpegPath)
  foreach ($a in @('-hide_banner', '-nostats', '-loglevel', 'warning', '-y',
                   '-f', 'gdigrab', '-framerate', '10', '-draw_mouse', '1', '-i', 'desktop',
                   '-t', "$MaxSeconds", '-vf', 'scale=1280:-2',
                   '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '35', '-pix_fmt', 'yuv420p',
                   '-movflags', '+faststart', $OutFile)) { $psi.ArgumentList.Add($a) }
  $psi.UseShellExecute = $false
  $psi.RedirectStandardInput = $true
  $psi.CreateNoWindow = $true
  $psi.WorkingDirectory = $State
  $psi.Environment['FFREPORT'] = 'file=ffmpeg.log:level=32'  # full log next to the helper (a relative name: no ':')
  $p = [System.Diagnostics.Process]::Start($psi)
  Set-Content -LiteralPath $PidTxt -Value $p.Id
  while (-not $p.HasExited -and -not (Test-Path -LiteralPath $StopReq)) { Start-Sleep -Milliseconds 500 }
  if (-not $p.HasExited) {
    $p.StandardInput.Write('q')                              # ffmpeg's own "quit": it finishes and closes the MP4
    $p.StandardInput.Flush()
    $p.StandardInput.Close()
  }
  if (-not $p.WaitForExit($StopTimeoutSeconds * 1000)) { $p.Kill(); $p.WaitForExit() ; Set-Content -LiteralPath $ExitTxt -Value 'killed'; exit 1 }
  Set-Content -LiteralPath $ExitTxt -Value $p.ExitCode
  exit 0
}

if ($Start -eq $Stop) { throw 'Use exactly one of -Start or -Stop.' }

if ($Start) {
  if (-not $OutFile) { throw '-Start needs -OutFile (the MP4 to write).' }
  if (Test-Path -LiteralPath $State) { Remove-Item -LiteralPath $State -Recurse -Force }
  New-Item -ItemType Directory -Force -Path $State | Out-Null
  $out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutFile)
  New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
  Set-Content -LiteralPath $OutTxt -Value $out

  # Download the pinned build and refuse it unless the SHA-256 matches exactly (fail closed).
  $zip = Join-Path $State 'ffmpeg.zip'
  Invoke-WebRequest -Uri $FfmpegUrl -OutFile $zip -UseBasicParsing
  $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($hash -ne $FfmpegSha256) {
    Remove-Item -LiteralPath $zip -Force
    throw "ffmpeg download does not match the pinned SHA-256 (got $hash). Not using it."
  }
  # Take only ffmpeg.exe out of the archive.
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $exe = Join-Path $State 'ffmpeg.exe'
  $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
  try {
    $entry = $archive.GetEntry($FfmpegEntry)
    if (-not $entry) { throw "ffmpeg.exe not found in the pinned archive at $FfmpegEntry." }
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $exe, $true)
  } finally { $archive.Dispose() }
  Remove-Item -LiteralPath $zip -Force

  # Start the helper in its own hidden window, so this step can end while the recording goes on.
  $q = { param($s) '"' + $s + '"' }
  $helperArgs = @('-NoProfile', '-NonInteractive', '-File', (& $q $PSCommandPath), '-Supervise',
                  '-OutFile', (& $q $out), '-FfmpegPath', (& $q $exe), '-StopTimeoutSeconds', "$StopTimeoutSeconds")
  $helper = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $helperArgs -WindowStyle Hidden -PassThru
  Write-Host "Recorder helper started (pid $($helper.Id)); recording the runner desktop to $out"

  # Wait until ffmpeg is running and the file exists; otherwise show its log and fail.
  $deadline = (Get-Date).AddSeconds(30)
  while ((Get-Date) -lt $deadline -and -not ((Test-Path -LiteralPath $out) -and (Test-Path -LiteralPath $PidTxt))) {
    if (Test-Path -LiteralPath $ExitTxt) { break }
    Start-Sleep -Milliseconds 500
  }
  if (-not (Test-Path -LiteralPath $out) -or (Test-Path -LiteralPath $ExitTxt)) {
    if (Test-Path -LiteralPath $Log) { Get-Content -LiteralPath $Log -Tail 30 }
    throw 'The desktop recording did not start (see the ffmpeg log above).'
  }
  Write-Host "Recording (ffmpeg pid $(Get-Content -LiteralPath $PidTxt))."
  exit 0
}

# ---- Stop ----
if (-not (Test-Path -LiteralPath $OutTxt) -or -not (Test-Path -LiteralPath $PidTxt)) {
  throw 'No recording was started in this job (the start step failed or did not run), so there is no video.'
}
$out = (Get-Content -LiteralPath $OutTxt -Raw).Trim()
New-Item -ItemType File -Force -Path $StopReq | Out-Null
$deadline = (Get-Date).AddSeconds($StopTimeoutSeconds + 15)
while (-not (Test-Path -LiteralPath $ExitTxt) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
$ffPid = [int](Get-Content -LiteralPath $PidTxt -Raw)
$exit = if (Test-Path -LiteralPath $ExitTxt) { (Get-Content -LiteralPath $ExitTxt -Raw).Trim() } else { 'unknown' }
if ($exit -eq 'unknown') {
  # The helper is gone or stuck: make sure ffmpeg does not keep recording.
  Stop-Process -Id $ffPid -Force -ErrorAction SilentlyContinue
}
'----- ffmpeg log (last 20 lines) -----'
if (Test-Path -LiteralPath $Log) { Get-Content -LiteralPath $Log -Tail 20 }

if (-not (Test-Path -LiteralPath $out)) { throw "No video was written (ffmpeg exit: $exit)." }
$bytes = (Get-Item -LiteralPath $out).Length
$mb = [math]::Round($bytes / 1MB, 2)
Write-Host "Demo video: $mb MB ($bytes bytes), ffmpeg exit: $exit"
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "Demo video: $mb MB (cap 25 MB), ffmpeg exit: $exit" }
if ($bytes -gt $MaxBytes) {
  Remove-Item -LiteralPath $out -Force
  throw "The video is $mb MB, above the 25 MB cap. It was deleted and will not be uploaded."
}
if ($exit -ne '0') { throw "ffmpeg did not finish cleanly (exit: $exit); the MP4 may not play." }
exit 0
