<#
.SYNOPSIS
  Records the CI runner's desktop to an MP4 while the demo tests run (demo-ci only).

.DESCRIPTION
  -Start     downloads a pinned ffmpeg build, checks its SHA-256, minimises every open window (so the windows the
             demos open come up on a clean desktop) and starts recording the whole desktop in the background
             (10 frames a second, 1280 px wide, H.264 CRF 35). A small helper copy of this script stays alive next
             to ffmpeg so the recording can be stopped cleanly from a later step.
  -Stop      asks ffmpeg to finish (it presses 'q' for it, so the MP4 is closed properly), waits, then checks the
             file with Test-DemoVideo (ffprobe and ffmpeg from the same pinned build). A good video gets about six
             still frames next to it (frames/*.png) for reviewers without a player. A bad one fails the step but is
             KEPT as demo.rejected.mp4 with a demo.rejected.txt that says why (only a file above the 25 MB cap is
             deleted, so it is never uploaded).
  -SelfTest  proves the checker on known files in RUNNER_TEMP (no screen capture): an empty file, random bytes, a
             good test-pattern MP4 cut in half and a missing file must be rejected; the good test-pattern MP4 must
             be accepted; and a rejection must keep the raw file with its reason.

  BOO-SAFE-001: it refuses to do anything unless it runs on a GitHub-hosted runner (RUNNER_ENVIRONMENT is
  'github-hosted'). On anyone's own PC it stops before touching ffmpeg, so it can never record a real person's screen.

.EXAMPLE
  pwsh scripts/ci/Record-Desktop.ps1 -SelfTest
  pwsh scripts/ci/Record-Desktop.ps1 -Start -OutFile artifacts/demo-video/demo.mp4
  pwsh scripts/ci/Record-Desktop.ps1 -Stop
#>
param(
  [switch] $Start,
  [switch] $Stop,
  [switch] $SelfTest,
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
# ffprobe.exe comes out of the SAME verified archive, so the checker is as pinned as the recorder.
$FfmpegUrl    = 'https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip'
$FfmpegSha256 = '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'
$ZipEntries   = [ordered]@{
  'ffmpeg.exe'  = 'ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe'
  'ffprobe.exe' = 'ffmpeg-9.0.2-essentials_build/bin/ffprobe.exe'
}
$MaxBytes     = 25MB                       # hard cap from the agent-trust plan, section 3.2 F
$MaxSeconds   = 1800                       # ffmpeg stops by itself after 30 min even if nobody stops it
$FrameCount   = 6                          # still frames pulled from a good video for reviewers

if (-not $env:RUNNER_TEMP) { throw 'RUNNER_TEMP is not set; this script only runs inside GitHub Actions.' }
$State   = Join-Path $env:RUNNER_TEMP 'boo-recorder'     # helper files; never inside the uploaded folder
$Log     = Join-Path $State 'ffmpeg.log'
$StopReq = Join-Path $State 'stop.request'
$ExitTxt = Join-Path $State 'ffmpeg.exit'
$PidTxt  = Join-Path $State 'ffmpeg.pid'
$OutTxt  = Join-Path $State 'outfile.txt'

# Downloads the pinned archive into $Dir, refuses it unless the SHA-256 matches exactly (fail closed), then takes out
# only ffmpeg.exe and ffprobe.exe, each by its exact name. Returns @{ ffmpeg = path; ffprobe = path }.
function Install-PinnedFfmpeg([string] $Dir) {
  New-Item -ItemType Directory -Force -Path $Dir | Out-Null
  $zip = Join-Path $Dir 'ffmpeg.zip'
  Invoke-WebRequest -Uri $FfmpegUrl -OutFile $zip -UseBasicParsing
  $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($hash -ne $FfmpegSha256) {
    Remove-Item -LiteralPath $zip -Force
    throw "ffmpeg download does not match the pinned SHA-256 (got $hash). Not using it."
  }
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $tools = @{}
  $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
  try {
    foreach ($name in $ZipEntries.Keys) {
      $entry = $archive.GetEntry($ZipEntries[$name])
      if (-not $entry) { throw "$name not found in the pinned archive at $($ZipEntries[$name])." }
      $dest = Join-Path $Dir $name
      [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
      $tools[[IO.Path]::GetFileNameWithoutExtension($name)] = $dest
    }
  } finally { $archive.Dispose() }
  Remove-Item -LiteralPath $zip -Force
  return $tools
}

# Runs a tool, waits up to $TimeoutSeconds, returns exit code + stdout + stderr (both read without deadlock).
function Invoke-Tool([string] $Exe, [string[]] $ToolArgs, [int] $TimeoutSeconds = 300) {
  $psi = [System.Diagnostics.ProcessStartInfo]::new($Exe)
  foreach ($a in $ToolArgs) { $psi.ArgumentList.Add($a) }
  $psi.UseShellExecute = $false
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.CreateNoWindow = $true
  $p = [System.Diagnostics.Process]::Start($psi)
  $outTask = $p.StandardOutput.ReadToEndAsync()
  $errTask = $p.StandardError.ReadToEndAsync()
  if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
    try { $p.Kill() } catch { }
    $p.WaitForExit()
    return [pscustomobject]@{ ExitCode = -1; StdOut = $outTask.Result; StdErr = "timed out after $TimeoutSeconds s`n" + $errTask.Result }
  }
  $p.WaitForExit()
  return [pscustomobject]@{ ExitCode = $p.ExitCode; StdOut = $outTask.Result; StdErr = $errTask.Result }
}

# THE video check (fail closed), used by -Stop and proven by -SelfTest. A video passes only if ALL hold:
#   1. the checker tools exist;  2. the file exists;  3. it has more than 0 bytes;  4. it is at most 25 MB;
#   5. ffprobe exits 0 and finds at least one video stream;  6. the duration is above 0 seconds;
#   7. ffprobe decodes more than 0 frames (-count_frames, nb_read_frames);
#   8. a full decode pass (ffmpeg -v error -i FILE -f null -) exits 0 and prints nothing.
# Returns Ok, Reason, Details (all tool output, for the rejection note), Duration, Frames, Bytes.
function Test-DemoVideo([string] $Path, [string] $Ffmpeg, [string] $Ffprobe) {
  $r = [pscustomobject]@{ Ok = $false; Reason = ''; Details = ''; Duration = 0.0; Frames = 0; Bytes = 0 }
  if (-not $Ffmpeg -or -not $Ffprobe -or -not (Test-Path -LiteralPath $Ffmpeg) -or -not (Test-Path -LiteralPath $Ffprobe)) {
    $r.Reason = 'the checker tools (ffmpeg.exe, ffprobe.exe from the pinned build) are missing, so the video cannot be checked'
    return $r
  }
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { $r.Reason = 'no video file was written'; return $r }
  $r.Bytes = (Get-Item -LiteralPath $Path).Length
  if ($r.Bytes -le 0) { $r.Reason = 'the video file is empty (0 bytes)'; return $r }
  if ($r.Bytes -gt $MaxBytes) { $r.Reason = "the video is $([math]::Round($r.Bytes / 1MB, 2)) MB, above the 25 MB cap"; return $r }

  $probe = Invoke-Tool $Ffprobe @('-v', 'error', '-count_frames', '-select_streams', 'v',
                                  '-show_entries', 'stream=index,codec_type,codec_name,width,height,nb_read_frames:format=duration',
                                  '-of', 'json', $Path)
  $r.Details = "ffprobe exit $($probe.ExitCode)`n--- ffprobe stdout ---`n$($probe.StdOut)`n--- ffprobe stderr ---`n$($probe.StdErr)"
  if ($probe.ExitCode -ne 0) { $r.Reason = "ffprobe could not read the file (exit $($probe.ExitCode))"; return $r }
  try { $info = $probe.StdOut | ConvertFrom-Json } catch { $r.Reason = 'ffprobe output was not readable JSON'; return $r }
  $video = @($info.streams | Where-Object { $_.codec_type -eq 'video' })
  if ($video.Count -lt 1) { $r.Reason = 'the file has no video stream'; return $r }
  $dur = 0.0
  $durText = if ($info.format) { [string]$info.format.duration } else { '' }
  if (-not [double]::TryParse($durText, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$dur) -or $dur -le 0) {
    $r.Reason = "the video has no positive duration (ffprobe duration: '$durText')"; return $r
  }
  $r.Duration = $dur
  $frames = 0
  if (-not [int]::TryParse([string]$video[0].nb_read_frames, [ref]$frames) -or $frames -le 0) {
    $r.Reason = "no video frames could be decoded (nb_read_frames: '$($video[0].nb_read_frames)')"; return $r
  }
  $r.Frames = $frames

  $decode = Invoke-Tool $Ffmpeg @('-hide_banner', '-nostdin', '-v', 'error', '-i', $Path, '-f', 'null', '-')
  $r.Details += "`nffmpeg decode pass exit $($decode.ExitCode)`n--- ffmpeg decode stderr ---`n$($decode.StdErr)"
  if ($decode.ExitCode -ne 0) { $r.Reason = "the full decode pass failed (ffmpeg exit $($decode.ExitCode))"; return $r }
  if ($decode.StdErr.Trim()) { $r.Reason = 'the full decode pass reported errors (see the decode output)'; return $r }

  $r.Ok = $true
  $r.Reason = 'ok'
  return $r
}

# A rejected video is kept as evidence: renamed to <name>.rejected.mp4 (same folder, so it uploads after the key scan)
# with <name>.rejected.txt saying why. Only a file above the 25 MB cap is deleted, so nothing oversized is uploaded.
function Save-RejectedVideo([string] $Path, [string] $Reason, [string] $Details) {
  $dir = Split-Path -Parent $Path
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $base = [IO.Path]::GetFileNameWithoutExtension($Path)
  $kept = Join-Path $dir "$base.rejected.mp4"
  $note = Join-Path $dir "$base.rejected.txt"
  $fate = 'no file to keep'
  if (Test-Path -LiteralPath $Path -PathType Leaf) {
    $len = (Get-Item -LiteralPath $Path).Length
    if ($len -gt $MaxBytes) {
      Remove-Item -LiteralPath $Path -Force
      $fate = "deleted: $len bytes is above the 25 MB upload cap"
    } else {
      Move-Item -LiteralPath $Path -Destination $kept -Force
      $fate = "kept as $(Split-Path -Leaf $kept) ($len bytes)"
    }
  }
  $text = @("Demo video REJECTED: $Reason", "Raw file: $fate", '', $Details) -join "`n"
  Set-Content -LiteralPath $note -Value $text -Encoding utf8
  return [pscustomobject]@{ Kept = $kept; Note = $note; Text = $text }
}

# About $FrameCount evenly spaced stills from the recorded FILE (not the screen), for reviewers without a player.
function Save-ReviewFrames([string] $Path, [double] $Duration, [string] $Ffmpeg) {
  $dir = Join-Path (Split-Path -Parent $Path) 'frames'
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $made = 0
  for ($i = 1; $i -le $FrameCount; $i++) {
    $t = [math]::Round($Duration * ($i - 0.5) / $FrameCount, 2)
    $ts = $t.ToString('0.00', [Globalization.CultureInfo]::InvariantCulture)
    $png = Join-Path $dir ('frame-{0:D2}-at-{1}s.png' -f $i, $ts)
    $x = Invoke-Tool $Ffmpeg @('-hide_banner', '-nostdin', '-v', 'error', '-y', '-ss', $ts, '-i', $Path,
                               '-frames:v', '1', '-vf', 'scale=960:-2', $png)
    if ($x.ExitCode -eq 0 -and (Test-Path -LiteralPath $png) -and (Get-Item -LiteralPath $png).Length -gt 0) { $made++ }
    else { Write-Host "::warning::Could not save review frame $i at $ts s (ffmpeg exit $($x.ExitCode)): $($x.StdErr.Trim())" }
  }
  return $made
}

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

if (@(@($Start, $Stop, $SelfTest) | Where-Object { $_ }).Count -ne 1) { throw 'Use exactly one of -Start, -Stop or -SelfTest.' }

if ($SelfTest) {
  # Red/green proof of Test-DemoVideo on files with a known answer. Nothing here captures the screen: the good sample
  # is ffmpeg's built-in test pattern (lavfi testsrc), encoded the same way as the real recording.
  $dir = Join-Path $env:RUNNER_TEMP 'boo-recorder-selftest'
  if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
  $tools = Install-PinnedFfmpeg (Join-Path $dir 'tools')
  $samples = Join-Path $dir 'samples'
  New-Item -ItemType Directory -Force -Path $samples | Out-Null

  $good = Join-Path $samples 'good.mp4'
  $gen = Invoke-Tool $tools.ffmpeg @('-hide_banner', '-nostdin', '-v', 'error', '-y', '-f', 'lavfi', '-i', 'testsrc=duration=2:size=320x240:rate=10',
                                     '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '35', '-pix_fmt', 'yuv420p', '-movflags', '+faststart', $good)
  if ($gen.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $good)) { throw "Self-test could not make its good sample (ffmpeg exit $($gen.ExitCode)): $($gen.StdErr)" }
  $goodBytes = [IO.File]::ReadAllBytes($good)
  $halfBytes = [byte[]]::new([math]::Floor($goodBytes.Length / 2))
  [Array]::Copy($goodBytes, $halfBytes, $halfBytes.Length)
  $noiseBytes = [byte[]]::new(65536)
  [Random]::new(39).NextBytes($noiseBytes)

  $empty = Join-Path $samples 'empty.mp4';     [IO.File]::WriteAllBytes($empty, [byte[]]::new(0))
  $noise = Join-Path $samples 'random.mp4';    [IO.File]::WriteAllBytes($noise, $noiseBytes)
  $half  = Join-Path $samples 'truncated.mp4'; [IO.File]::WriteAllBytes($half, $halfBytes)
  $none  = Join-Path $samples 'missing.mp4'

  $cases = @(
    @{ Name = 'empty file (0 bytes)';          Path = $empty; Expect = $false }
    @{ Name = 'random bytes named .mp4';       Path = $noise; Expect = $false }
    @{ Name = 'good MP4 cut to half its size'; Path = $half;  Expect = $false }
    @{ Name = 'missing file';                  Path = $none;  Expect = $false }
    @{ Name = 'good 2 s test-pattern MP4';     Path = $good;  Expect = $true  }
  )
  $failed = 0
  foreach ($c in $cases) {
    $v = Test-DemoVideo $c.Path $tools.ffmpeg $tools.ffprobe
    $pass = ($v.Ok -eq $c.Expect)
    if (-not $pass) { $failed++ }
    Write-Host ('{0}  {1}: expected {2}, got {3} ({4}; {5} frames, {6:N2} s)' -f $(if ($pass) { 'PASS' } else { 'FAIL' }), $c.Name,
                $(if ($c.Expect) { 'accept' } else { 'reject' }), $(if ($v.Ok) { 'accept' } else { 'reject' }), $v.Reason, $v.Frames, $v.Duration)
    if (-not $pass) { Write-Host $v.Details }
  }

  # The rejection path must keep the raw file (renamed, same size) and write the reason next to it.
  $up = Join-Path $dir 'upload'
  New-Item -ItemType Directory -Force -Path $up | Out-Null
  $raw = Join-Path $up 'demo.mp4'
  Copy-Item -LiteralPath $half -Destination $raw
  $v = Test-DemoVideo $raw $tools.ffmpeg $tools.ffprobe
  $saved = Save-RejectedVideo $raw $v.Reason $v.Details
  $keptOk = (-not $v.Ok) -and (-not (Test-Path -LiteralPath $raw)) -and (Test-Path -LiteralPath $saved.Note) -and
            (Test-Path -LiteralPath $saved.Kept) -and ((Get-Item -LiteralPath $saved.Kept).Length -eq $halfBytes.Length)
  if (-not $keptOk) { $failed++ }
  Write-Host ('{0}  a rejected video is kept as demo.rejected.mp4 with demo.rejected.txt' -f $(if ($keptOk) { 'PASS' } else { 'FAIL' }))

  $total = $cases.Count + 1
  $line = "Recorder self-test: $($total - $failed)/$total checks passed (validator rejects empty/corrupt video, accepts a good one, keeps rejected files)."
  Write-Host $line
  if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $line }
  if ($failed -gt 0) { Write-Host "::error::Recorder self-test failed $failed check(s); the video checker cannot be trusted."; exit 1 }
  exit 0
}

if ($Start) {
  if (-not $OutFile) { throw '-Start needs -OutFile (the MP4 to write).' }
  if (Test-Path -LiteralPath $State) { Remove-Item -LiteralPath $State -Recurse -Force }
  New-Item -ItemType Directory -Force -Path $State | Out-Null
  $out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutFile)
  New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
  Set-Content -LiteralPath $OutTxt -Value $out

  # Pinned build, SHA-256 checked before anything is taken out of it; ffprobe.exe stays here for the -Stop check.
  $tools = Install-PinnedFfmpeg $State
  $exe = $tools.ffmpeg

  # Clean desktop: minimise every open window (runner consoles, Explorer) so what the demos open is in view.
  try {
    (New-Object -ComObject Shell.Application).MinimizeAll()
    Start-Sleep -Seconds 1
    Write-Host 'Minimised all open windows before recording.'
  } catch { Write-Host "::warning::Could not minimise open windows before recording: $($_.Exception.Message)" }

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
  # The helper is gone or stuck: make sure ffmpeg does not keep recording before the file is checked.
  Stop-Process -Id $ffPid -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 2
}
'----- ffmpeg log (last 20 lines) -----'
if (Test-Path -LiteralPath $Log) { Get-Content -LiteralPath $Log -Tail 20 }

# ffmpeg has finished; now check the file itself (fail closed). A non-zero recorder exit also rejects it.
# THROWAWAY RED PROOF (never merge): replace the good recording with an EMPTY file; ffmpeg's exit stays 0.
[System.IO.File]::WriteAllBytes($out, [byte[]]@())
$check = Test-DemoVideo $out (Join-Path $State 'ffmpeg.exe') (Join-Path $State 'ffprobe.exe')
if ($check.Ok -and $exit -ne '0') {
  $check.Ok = $false
  $check.Reason = "ffmpeg did not finish cleanly (exit: $exit); the MP4 may not play"
}
if (-not $check.Ok) {
  $saved = Save-RejectedVideo $out $check.Reason ("ffmpeg recorder exit: $exit`n" + $check.Details)
  Write-Host $saved.Text
  if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "Demo video REJECTED: $($check.Reason) (raw file and reason kept in the demo-video artifact)" }
  Write-Host "::error::Demo video rejected: $($check.Reason). Raw file and reason kept in the demo-video artifact."
  exit 1
}

$mb = [math]::Round($check.Bytes / 1MB, 2)
$made = Save-ReviewFrames $out $check.Duration (Join-Path $State 'ffmpeg.exe')
$line = "Demo video: $mb MB ($($check.Bytes) bytes, cap 25 MB), $($check.Frames) frames, $([math]::Round($check.Duration, 1)) s, ffmpeg exit: $exit; $made/$FrameCount review frames saved"
Write-Host $line
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $line }
exit 0
