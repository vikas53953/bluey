<#
.SYNOPSIS
  Visual acceptance for demo-ci: fails when a full-screen demo screenshot or the demo video shows a blank window.

.DESCRIPTION
  A "blank window" is a big area of one flat colour: a window whose inside was never drawn (Codex review P2 on PR #39:
  a maximised all-white "Boo companion" window for about 20 s of the focus demo). The check needs no window list and
  no knowledge of Boo's layout, so it covers every demo and every window, not just the one that went wrong:

    1. Each image is cut into small tiles (about 80 across). A tile is "flat" when every pixel in it is within a few
       colour steps of the others.
    2. For each flat colour it measures (a) the largest rectangle made only of flat tiles of that colour and (b) how
       much of the screen that colour's flat tiles cover in any shape.
    3. Blank when (a) reaches 40 % of the screen OR (b) reaches 70 %. (b) catches a blank window with something on
       top of it (the notch, or a Windows pop-up in the middle, which split the rectangle in run 43499354528).

  Measured on demo-ci runs 43491427190, 43496633458, 43498525331 and 43499354528: good full-screen demo frames reach at
  most 21 % (a) and 50 % (b) (Boo's own dark panel with its empty chat area); the blank white window reaches 73-93 %
  (a) or, with a pop-up on it, 78-87 % (b).
  Declared test fixtures (-FixtureColours) are not windows and are ignored: the capture-privacy proof's full-screen
  magenta decoy (255, 0, 255).

  What it checks:
    - every full-desktop screenshot in -EvidenceDir (the PNGs with the largest size there; window-only renders
      *.boo-N.png and smaller crops are skipped). Any blank one fails, named with its demo.
    - the demo video (-VideoFile), two frames a second, decoded with the pinned ffmpeg the recorder already checked.
      A blank screen lasting longer than -MaxBlankSeconds (default 1 s) fails, with its time range and the demo it
      falls in. A single frame is allowed: Windows shows a new window's empty frame for an instant before its first
      paint, and the video sees that flash.
  The result is written to visual-check.txt in the evidence folder (uploaded with it) and to the job summary.

  -SelfTest proves the check on synthetic pictures (no screen capture): a desktop with a drawn panel and a small
  white tooltip and the magenta decoy must pass; a maximised white blank window, a black one, a white one with a
  pop-up in the middle, and a flat pink screen close to (but not) the decoy colour must fail; a frame sequence
  with a one-frame flash must pass and one with a 2 s blank must fail. With -FfmpegPath it also encodes those
  sequences to MP4 and runs the real video path.

  It only reads files, so it may run anywhere; it never captures the screen.

.EXAMPLE
  pwsh scripts/ci/Test-DemoVisual.ps1 -SelfTest -FfmpegPath $env:RUNNER_TEMP/boo-recorder/ffmpeg.exe
  pwsh scripts/ci/Test-DemoVisual.ps1 -EvidenceDir artifacts/demo-evidence -VideoFile artifacts/demo-video/demo.mp4
#>
param(
  [string] $EvidenceDir,
  [string] $VideoFile,
  [string] $FfmpegPath,
  [switch] $SelfTest,
  [double] $MaxBlankFraction = 0.40,
  [double] $MaxColourShare = 0.70,
  # Flat colours that are test fixtures on purpose, never a window: tools/Boo.CaptureProof fills the whole screen with a
  # magenta decoy (255, 0, 255) to prove Boo's screenshots leave it out. Only exact declared fixture colours belong here.
  [string[]] $FixtureColours = @('#FF00FF'),
  [double] $MaxBlankSeconds = 1.0,
  [string] $ReportFile
)
$ErrorActionPreference = 'Stop'

if (-not $FfmpegPath -and $env:RUNNER_TEMP) {
  # The recorder's pinned, SHA-256-checked copy (scripts/ci/Record-Desktop.ps1 keeps it here after -Stop).
  $FfmpegPath = Join-Path $env:RUNNER_TEMP 'boo-recorder\ffmpeg.exe'
}

$VideoFps = 2
$FixtureRgb = [int[]]@($FixtureColours | ForEach-Object { [Convert]::ToInt32($_.TrimStart('#'), 16) })

Add-Type -AssemblyName System.Drawing
if (-not ('BooBlankScan' -as [type])) {
  # The C# part sees only raw pixel bytes (no drawing types), so it compiles the same on Windows PowerShell 5.1 and
  # PowerShell 7 with no assembly references; Read-Pixels below loads the picture with System.Drawing.
  Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;

public static class BooBlankScan
{
    // px: 24-bit pixels (B, G, R), rows "stride" bytes apart. Returns the share (0..1) of the image covered by the
    // largest rectangle of flat, same-coloured tiles, and that rectangle in pixels plus its colour as text.
    public static double LargestFlatRectangle(byte[] px, int width, int height, int stride, out string where, out double colourShare, int[] ignoredColours)
    {
        colourShare = 0;
        int tile = Math.Max(4, (int)Math.Round(width / 80.0));
        int cols = width / tile, rows = height / tile;
        where = "";
        if (cols < 4 || rows < 4) return 0;

        var flat = new bool[rows, cols];
        var key = new int[rows, cols, 3];
        var counts = new Dictionary<int, int>();
        for (int ty = 0; ty < rows; ty++)
        {
            for (int tx = 0; tx < cols; tx++)
            {
                int[] min = { 255, 255, 255 }, max = { 0, 0, 0 };
                long[] sum = { 0, 0, 0 };
                for (int y = ty * tile; y < (ty + 1) * tile; y++)
                {
                    int row = y * stride;
                    for (int x = tx * tile; x < (tx + 1) * tile; x++)
                    {
                        int i = row + x * 3;
                        for (int c = 0; c < 3; c++)
                        {
                            int v = px[i + c];
                            if (v < min[c]) min[c] = v;
                            if (v > max[c]) max[c] = v;
                            sum[c] += v;
                        }
                    }
                }
                bool isFlat = true;
                for (int c = 0; c < 3; c++) { if (max[c] - min[c] > 6) isFlat = false; }
                flat[ty, tx] = isFlat;
                int n = tile * tile;
                for (int c = 0; c < 3; c++) key[ty, tx, c] = (int)(sum[c] / n) / 8;
                if (isFlat)
                {
                    int k = (key[ty, tx, 0] << 10) | (key[ty, tx, 1] << 5) | key[ty, tx, 2];
                    int seen;
                    counts.TryGetValue(k, out seen);
                    counts[k] = seen + 1;
                }
            }
        }

        int total = rows * cols, bestArea = 0, bx = 0, by = 0, bw = 0, bh = 0, bestKey = 0;
        foreach (var pair in counts)
        {
            if (pair.Value * 20 < total) continue; // colours under 5 % of the screen cannot reach the limit
            int kr = (pair.Key >> 10) & 31, kg = (pair.Key >> 5) & 31, kb = pair.Key & 31;
            bool ignored = false;
            foreach (var rgb in ignoredColours)
            {
                if (Math.Abs(((rgb >> 16) & 255) / 8 - kr) <= 1 && Math.Abs(((rgb >> 8) & 255) / 8 - kg) <= 1
                    && Math.Abs((rgb & 255) / 8 - kb) <= 1) ignored = true;
            }
            if (ignored) continue; // a declared test fixture, not a window
            var heights = new int[cols];
            int matched = 0;
            for (int ty = 0; ty < rows; ty++)
            {
                for (int tx = 0; tx < cols; tx++)
                {
                    bool match = flat[ty, tx] && Math.Abs(key[ty, tx, 0] - kr) <= 1
                        && Math.Abs(key[ty, tx, 1] - kg) <= 1 && Math.Abs(key[ty, tx, 2] - kb) <= 1;
                    heights[tx] = match ? heights[tx] + 1 : 0;
                    if (match) matched++;
                }
                // Largest rectangle under the histogram "heights" (classic stack method).
                var starts = new Stack<int>();
                var hs = new Stack<int>();
                for (int x = 0; x <= cols; x++)
                {
                    int h = x < cols ? heights[x] : 0;
                    int start = x;
                    while (hs.Count > 0 && hs.Peek() >= h)
                    {
                        int s = starts.Pop(), sh = hs.Pop();
                        int area = sh * (x - s);
                        if (area > bestArea)
                        {
                            bestArea = area; bx = s; bw = x - s; bh = sh; by = ty - sh + 1; bestKey = pair.Key;
                        }
                        start = s;
                    }
                    starts.Push(start);
                    hs.Push(h);
                }
            }
            colourShare = Math.Max(colourShare, (double)matched / total);
        }
        if (bestArea == 0) return 0;
        where = string.Format("{0},{1} {2}x{3} px, colour #{4:X2}{5:X2}{6:X2}", bx * tile, by * tile, bw * tile, bh * tile,
            ((bestKey >> 10) & 31) * 8, ((bestKey >> 5) & 31) * 8, (bestKey & 31) * 8);
        return (double)bestArea / total;
    }
}
'@
}

$DemoNames = @{
  '00' = 'demo 0 (setup: panel and settings)'
  '01' = 'demo 1 (screen explain with pointer ring)'
  '02' = 'demo 2 (edit, Apply and Undo)'
  '03' = 'demo 3 (research with sources)'
  '04' = 'demo 4 (focus session, nudge, happy face)'
  'zz' = 'the end of the run (final desktop)'
  'proof' = 'the capture-privacy proof'
  'timeout' = 'a demo that timed out'
}

function Get-DemoName([string] $FileName) {
  foreach ($k in $DemoNames.Keys) {
    if ($FileName.StartsWith("$k-", [StringComparison]::Ordinal)) { return $DemoNames[$k] }
  }
  return "the step that saved $FileName"
}

# Loads a picture as 24-bit pixel bytes (B, G, R) for BooBlankScan.
function Read-Pixels([string] $Path) {
  $src = New-Object System.Drawing.Bitmap $Path
  try {
    $rect = New-Object System.Drawing.Rectangle 0, 0, $src.Width, $src.Height
    $bmp = $src.Clone($rect, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    try {
      $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
      try {
        $bytes = New-Object byte[] ($data.Stride * $bmp.Height)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
        return [pscustomobject]@{ Bytes = $bytes; Width = $bmp.Width; Height = $bmp.Height; Stride = $data.Stride }
      } finally { $bmp.UnlockBits($data) }
    } finally { $bmp.Dispose() }
  } finally { $src.Dispose() }
}

# Score 1.0 or more = blank: the largest flat rectangle reaches -MaxBlankFraction of the screen, OR one flat colour
# covers -MaxColourShare of it in any shape (a blank window with a dialog or the notch on top of it is split into
# smaller rectangles, but its colour still fills most of the screen).
function Measure-Image([string] $Path) {
  $where = ''
  $share = 0.0
  $p = Read-Pixels $Path
  $fraction = [BooBlankScan]::LargestFlatRectangle($p.Bytes, $p.Width, $p.Height, $p.Stride, [ref] $where, [ref] $share, $FixtureRgb)
  $score = [math]::Max($fraction / $MaxBlankFraction, $share / $MaxColourShare)
  [pscustomobject]@{ Fraction = $fraction; Share = $share; Score = $score; Blank = ($score -ge 1.0); Where = $where
                     Text = ('largest flat rectangle {0:P0}, one flat colour {1:P0} of the screen [{2}]' -f $fraction, $share, $where) }
}

# Full-desktop screenshots: every top-level PNG with the largest pixel size, except window-only renders (*.boo-N.png).
function Get-DesktopShots([string] $Dir) {
  $shots = @()
  foreach ($f in Get-ChildItem -LiteralPath $Dir -Filter *.png -File | Where-Object { $_.Name -notmatch '\.boo-\d+\.png$' }) {
    $img = [System.Drawing.Image]::FromFile($f.FullName)
    try { $shots += [pscustomobject]@{ File = $f; Area = $img.Width * $img.Height; Size = "$($img.Width)x$($img.Height)" } }
    finally { $img.Dispose() }
  }
  if (-not $shots) { return @() }
  $max = ($shots | Measure-Object -Property Area -Maximum).Maximum
  return @($shots | Where-Object { $_.Area -eq $max } | Sort-Object { $_.File.Name })
}

function Test-Screenshots([string] $Dir) {
  $lines = [System.Collections.Generic.List[string]]::new()
  $errors = [System.Collections.Generic.List[string]]::new()
  $shots = Get-DesktopShots $Dir
  if ($shots.Count -eq 0) { $errors.Add("no full-desktop screenshots found in $Dir, so the demos cannot be checked"); return @{ Lines = $lines; Errors = $errors } }
  foreach ($s in $shots) {
    $m = Measure-Image $s.File.FullName
    $blank = $m.Blank
    $lines.Add(('{0}  {1} ({2}): {3}' -f $(if ($blank) { 'BLANK' } else { 'ok   ' }), $s.File.Name, $s.Size, $m.Text))
    if ($blank) {
      $errors.Add(('{0} in {1} shows a blank window: {2}; the limits are {3:P0} for one rectangle and {4:P0} for one colour.' -f
                   $s.File.Name, (Get-DemoName $s.File.Name), $m.Text, $MaxBlankFraction, $MaxColourShare))
    }
  }
  return @{ Lines = $lines; Errors = $errors }
}

# Runs of blank frames (two frames a second). Returns @{ Start; End; Frames; Peak } per run longer than the limit.
# Scores are Measure-Image scores (1.0 or more = blank).
function Find-BlankRuns([double[]] $Fractions, [double] $Fps) {
  $runs = @()
  $start = -1
  for ($i = 0; $i -le $Fractions.Count; $i++) {
    $isBlank = $i -lt $Fractions.Count -and $Fractions[$i] -ge 1.0
    if ($isBlank -and $start -lt 0) { $start = $i }
    if (-not $isBlank -and $start -ge 0) {
      $count = $i - $start
      if (($count / $Fps) -gt $MaxBlankSeconds) {
        $peak = ($Fractions[$start..($i - 1)] | Measure-Object -Maximum).Maximum
        $runs += [pscustomobject]@{ Start = $start / $Fps; End = $i / $Fps; Frames = $count; Peak = $peak }
      }
      $start = -1
    }
  }
  return $runs
}

function Get-VideoFrames([string] $Video, [string] $OutDir) {
  if (-not $FfmpegPath -or -not (Test-Path -LiteralPath $FfmpegPath)) {
    throw "the pinned ffmpeg is missing ($FfmpegPath), so the video cannot be checked"
  }
  if (Test-Path -LiteralPath $OutDir) { Remove-Item -LiteralPath $OutDir -Recurse -Force }
  New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
  $pattern = Join-Path $OutDir 'f%05d.png'
  $p = Start-Process -FilePath $FfmpegPath -ArgumentList @('-hide_banner', '-nostdin', '-v', 'error', '-i', "`"$Video`"", '-vf', "fps=$VideoFps", "`"$pattern`"") `
        -NoNewWindow -Wait -PassThru -RedirectStandardError (Join-Path $OutDir 'ffmpeg.err')
  if ($p.ExitCode -ne 0) { throw "ffmpeg could not read the video frames (exit $($p.ExitCode)): $(Get-Content -Raw (Join-Path $OutDir 'ffmpeg.err'))" }
  return @(Get-ChildItem -LiteralPath $OutDir -Filter 'f*.png' | Sort-Object Name)
}

# Which demo a moment of the video belongs to: the next full-desktop screenshot written after it (the video ends when
# the recorder stops, so its start is the file's last write time minus its length). A hint, so it says "about".
function Get-DemoAt([double] $Seconds, [string] $Video, [double] $Duration, [object[]] $Shots) {
  if (-not $Shots) { return 'an unknown demo' }
  $videoStart = (Get-Item -LiteralPath $Video).LastWriteTimeUtc.AddSeconds(-$Duration)
  $at = $videoStart.AddSeconds($Seconds)
  $next = $Shots | Where-Object { $_.File.LastWriteTimeUtc -ge $at } | Sort-Object { $_.File.LastWriteTimeUtc } | Select-Object -First 1
  if (-not $next) { return 'after the last demo screenshot' }
  return "about $(Get-DemoName $next.File.Name), before $($next.File.Name)"
}

function Test-Video([string] $Video, [object[]] $Shots, [string] $WorkDir) {
  $lines = [System.Collections.Generic.List[string]]::new()
  $errors = [System.Collections.Generic.List[string]]::new()
  if (-not (Test-Path -LiteralPath $Video)) { $errors.Add("the demo video $Video is missing, so it cannot be checked"); return @{ Lines = $lines; Errors = $errors } }
  try { $frames = Get-VideoFrames $Video $WorkDir } catch { $errors.Add("$_"); return @{ Lines = $lines; Errors = $errors } }
  if ($frames.Count -eq 0) { $errors.Add('the demo video has no frames to check'); return @{ Lines = $lines; Errors = $errors } }
  $measured = @($frames | ForEach-Object { Measure-Image $_.FullName })
  $fractions = [double[]]@($measured | ForEach-Object { $_.Score })
  $duration = $frames.Count / $VideoFps
  $blankFrames = @($measured | Where-Object { $_.Blank }).Count
  $lines.Add(('video {0}: {1} frames checked ({2} a second, {3:N1} s); {4} blank; worst rectangle {5:P0}, worst colour {6:P0}' -f (Split-Path -Leaf $Video), $frames.Count, $VideoFps, $duration, $blankFrames, ($measured | Measure-Object -Property Fraction -Maximum).Maximum, ($measured | Measure-Object -Property Share -Maximum).Maximum))
  foreach ($r in Find-BlankRuns $fractions $VideoFps) {
    $errors.Add(('the demo video shows a blank window from {0:N1} s to {1:N1} s ({2:N1} s, {3}): up to {4:N1} times the blank limit (one flat rectangle {5:P0} or one flat colour {6:P0} of the screen) for more than {7:N1} s.' -f
                 $r.Start, $r.End, ($r.End - $r.Start), (Get-DemoAt $r.Start $Video $duration $Shots), $r.Peak, $MaxBlankFraction, $MaxColourShare, $MaxBlankSeconds))
  }
  Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
  return @{ Lines = $lines; Errors = $errors }
}

# ---------------------------------------------------------------------------------------------------- self-test
function New-Desktop([int] $W, [int] $H, [int] $Seed) {
  $bmp = New-Object System.Drawing.Bitmap $W, $H
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Rectangle 0, 0, $W, $H), ([System.Drawing.Color]::FromArgb(20, 80, 160)), ([System.Drawing.Color]::FromArgb(180, 210, 240)), 35.0
  $g.FillRectangle($brush, 0, 0, $W, $H)
  $rand = [Random]::new($Seed)
  for ($i = 0; $i -lt 1500; $i++) {   # wallpaper detail, like the photo on the runner's desktop
    $c = [System.Drawing.Color]::FromArgb(60, $rand.Next(256), $rand.Next(256), $rand.Next(256))
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $c), $rand.Next($W), $rand.Next($H), 4 + $rand.Next(30), 4 + $rand.Next(30))
  }
  $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(240, 240, 240))), 0, $H - 48, $W, 48) # taskbar
  $g.Dispose()
  return $bmp
}

function Add-Window([System.Drawing.Bitmap] $Bmp, [System.Drawing.Color] $Client, [bool] $Drawn, [int] $X, [int] $Y, [int] $W, [int] $H) {
  $g = [System.Drawing.Graphics]::FromImage($Bmp)
  $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(243, 243, 243))), $X, $Y, $W, 30) # title bar
  $g.DrawString('Boo companion', (New-Object System.Drawing.Font 'Arial', 9), [System.Drawing.Brushes]::Black, $X + 8, $Y + 8)
  $g.FillRectangle((New-Object System.Drawing.SolidBrush $Client), $X, $Y + 30, $W, $H - 30)
  if ($Drawn) {
    # A drawn panel: a header, a side column, rows of text and buttons, like Boo's companion window.
    $fg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(220, 220, 220))
    $font = New-Object System.Drawing.Font 'Arial', 10
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(40, 40, 46))), $X + 12, $Y + 42, $W - 24, 44)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(32, 32, 38))), $X + $W - 260, $Y + 96, 248, $H - 150)
    for ($row = $Y + 100; $row -lt $Y + $H - 40; $row += 34) {
      $g.DrawString('Boo: here is a line of text in the companion window ' + $row, $font, $fg, $X + 24, $row)
      $g.DrawString('Setting ' + $row, $font, $fg, $X + $W - 250, $row)
    }
  }
  $g.Dispose()
}

function Save-Bmp([System.Drawing.Bitmap] $Bmp, [string] $Path) { $Bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png); $Bmp.Dispose(); return $Path }

function Invoke-SelfTest {
  $root = Join-Path ($(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() })) 'boo-visual-selftest'
  if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
  New-Item -ItemType Directory -Force -Path $root | Out-Null
  $W = 1024; $H = 768
  $dark = [System.Drawing.Color]::FromArgb(20, 20, 24)

  $good = New-Desktop $W $H 1; Add-Window $good $dark $true 0 0 $W ($H - 48)
  $goodPath = Save-Bmp $good (Join-Path $root 'good-panel.png')
  $desk = New-Desktop $W $H 2
  $g = [System.Drawing.Graphics]::FromImage($desk); $g.FillRectangle([System.Drawing.Brushes]::White, 400, 300, 220, 90); $g.Dispose() # small tooltip
  $tipPath = Save-Bmp $desk (Join-Path $root 'good-desktop-tooltip.png')
  $white = New-Desktop $W $H 3; Add-Window $white ([System.Drawing.Color]::White) $false 0 0 $W ($H - 48)
  $g = [System.Drawing.Graphics]::FromImage($white); $g.FillEllipse([System.Drawing.Brushes]::Black, 340, -60, 350, 210); $g.Dispose() # the notch on top
  $whitePath = Save-Bmp $white (Join-Path $root 'blank-white.png')
  # The case that slipped past a rectangle-only rule (run 43499354528): Windows' own "System Properties" pop-up in
  # the middle of the blank window splits it into smaller rectangles.
  $split = New-Desktop $W $H 5; Add-Window $split ([System.Drawing.Color]::White) $false 0 0 $W ($H - 48)
  $g = [System.Drawing.Graphics]::FromImage($split)
  $g.FillEllipse([System.Drawing.Brushes]::Black, 410, -60, 210, 120)
  $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(240, 240, 240))), 318, 320, 406, 183)
  $g.DrawRectangle([System.Drawing.Pens]::Gray, 318, 320, 406, 183)
  $g.DrawString('Windows created a temporary paging file on your computer', (New-Object System.Drawing.Font 'Arial', 9), [System.Drawing.Brushes]::Black, 380, 375)
  $g.Dispose()
  $splitPath = Save-Bmp $split (Join-Path $root 'blank-white-with-dialog.png')
  $decoy = New-Object System.Drawing.Bitmap $W, $H
  $g = [System.Drawing.Graphics]::FromImage($decoy); $g.Clear([System.Drawing.Color]::FromArgb(255, 0, 255)); $g.Dispose()
  $decoyPath = Save-Bmp $decoy (Join-Path $root 'capture-proof-decoy.png')
  $pink = New-Object System.Drawing.Bitmap $W, $H
  $g = [System.Drawing.Graphics]::FromImage($pink); $g.Clear([System.Drawing.Color]::FromArgb(255, 120, 255)); $g.Dispose()
  $pinkPath = Save-Bmp $pink (Join-Path $root 'near-decoy-pink.png')
  $black = New-Desktop $W $H 4; Add-Window $black ([System.Drawing.Color]::Black) $false 100 60 800 600
  $blackPath = Save-Bmp $black (Join-Path $root 'blank-black.png')

  $failed = 0
  $cases = @(
    @{ Name = 'desktop with a drawn Boo panel';           Path = $goodPath;  Blank = $false }
    @{ Name = 'desktop with a small white tooltip';       Path = $tipPath;   Blank = $false }
    @{ Name = 'maximised white blank window under notch'; Path = $whitePath; Blank = $true }
    @{ Name = 'black blank window (any colour counts)';   Path = $blackPath; Blank = $true }
    @{ Name = 'white blank window with a pop-up in the middle'; Path = $splitPath; Blank = $true }
    @{ Name = 'capture-proof magenta decoy (declared fixture)';  Path = $decoyPath; Blank = $false }
    @{ Name = 'flat pink, close to but not the decoy colour';    Path = $pinkPath;  Blank = $true }
  )
  foreach ($c in $cases) {
    $m = Measure-Image $c.Path
    $isBlank = $m.Blank
    $pass = $isBlank -eq $c.Blank
    if (-not $pass) { $failed++ }
    Write-Host ('{0}  {1}: expected {2}, got {3} ({4})' -f $(if ($pass) { 'PASS' } else { 'FAIL' }), $c.Name,
                $(if ($c.Blank) { 'blank' } else { 'ok' }), $(if ($isBlank) { 'blank' } else { 'ok' }), $m.Text)
  }

  # The screenshot pass end to end: a folder with one blank desktop shot must name that file and its demo.
  $ev = Join-Path $root 'evidence'
  New-Item -ItemType Directory -Force -Path $ev | Out-Null
  Copy-Item $goodPath (Join-Path $ev '01-explain-ring-fullscreen.png')
  Copy-Item $whitePath (Join-Path $ev '04-nudge-fullscreen.png')
  $crop = New-Object System.Drawing.Bitmap 300, 200
  $g = [System.Drawing.Graphics]::FromImage($crop); $g.Clear([System.Drawing.Color]::White); $g.Dispose()
  $null = Save-Bmp $crop (Join-Path $ev '04-nudge-notch.png')   # a small crop: not a desktop shot, skipped
  $r = Test-Screenshots $ev
  $named = $r.Errors.Count -eq 1 -and $r.Errors[0] -like '04-nudge-fullscreen.png in demo 4 (focus*'
  if (-not $named) { $failed++ }
  Write-Host ('{0}  a blank desktop screenshot fails and is named with its demo (crops skipped): {1}' -f $(if ($named) { 'PASS' } else { 'FAIL' }), ($r.Errors -join ' | '))

  # Video rule on frame sequences (two a second): a one-frame flash passes, a 2 s blank fails with its times.
  $flash = Find-BlankRuns ([double[]](0.3, 0.3, 2.0, 0.3, 0.3)) 2
  $long = Find-BlankRuns ([double[]](0.3, 0.3, 2.0, 2.0, 2.0, 2.0, 0.3)) 2
  $seqOk = (@($flash).Count -eq 0) -and (@($long).Count -eq 1) -and ($long[0].Start -eq 1.0) -and ($long[0].End -eq 3.0)
  if (-not $seqOk) { $failed++ }
  Write-Host ('{0}  video rule: a one-frame first-paint flash passes; 2 s of blank fails (1.0 s to 3.0 s)' -f $(if ($seqOk) { 'PASS' } else { 'FAIL' }))
  $total = $cases.Count + 2

  # The real video path, when the pinned ffmpeg is here: encode the pictures into MP4s and check them.
  if ($FfmpegPath -and (Test-Path -LiteralPath $FfmpegPath)) {
    $vids = @(
      @{ Name = 'MP4 of drawn panels with a one-frame white flash'; Frames = @($goodPath, $goodPath, $whitePath, $goodPath, $goodPath, $goodPath); Blank = $false }
      @{ Name = 'MP4 with 2 s of a white blank window';             Frames = @($goodPath, $whitePath, $whitePath, $whitePath, $whitePath, $goodPath); Blank = $true }
    )
    $n = 0
    foreach ($v in $vids) {
      $n++
      $seq = Join-Path $root "seq$n"
      New-Item -ItemType Directory -Force -Path $seq | Out-Null
      for ($i = 0; $i -lt $v.Frames.Count; $i++) { Copy-Item $v.Frames[$i] (Join-Path $seq ('s{0:D3}.png' -f $i)) }
      $mp4 = Join-Path $root "seq$n.mp4"
      $p = Start-Process -FilePath $FfmpegPath -NoNewWindow -Wait -PassThru -ArgumentList @('-hide_banner', '-nostdin', '-v', 'error', '-y', '-framerate', '2', '-i', "`"$(Join-Path $seq 's%03d.png')`"",
             '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '35', '-pix_fmt', 'yuv420p', '-r', '10', "`"$mp4`"")
      $res = if ($p.ExitCode -eq 0) { Test-Video $mp4 @() (Join-Path $root "frames$n") } else { @{ Errors = @("ffmpeg could not encode the sample (exit $($p.ExitCode))"); Lines = @() } }
      $gotBlank = @($res.Errors | Where-Object { $_ -like 'the demo video shows a blank window*' }).Count -gt 0
      $other = @($res.Errors | Where-Object { $_ -notlike 'the demo video shows a blank window*' })
      $pass = ($gotBlank -eq $v.Blank) -and $other.Count -eq 0
      if (-not $pass) { $failed++ }
      Write-Host ('{0}  {1}: expected {2}, got {3} {4}' -f $(if ($pass) { 'PASS' } else { 'FAIL' }), $v.Name,
                  $(if ($v.Blank) { 'blank' } else { 'ok' }), $(if ($gotBlank) { 'blank' } else { 'ok' }), (@($res.Lines) + @($res.Errors) -join ' | '))
      $total++
    }
  } else {
    Write-Host "SKIP  MP4 cases: no ffmpeg at '$FfmpegPath' (in demo-ci the recorder's pinned copy is used)."
  }

  $line = "Visual check self-test: $($total - $failed)/$total checks passed (blank windows caught in any colour, drawn panels and flashes allowed)."
  Write-Host $line
  if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $line }
  Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
  if ($failed -gt 0) { Write-Host "::error::Visual check self-test failed $failed check(s); the blank-window check cannot be trusted."; exit 1 }
  exit 0
}

if ($SelfTest) { Invoke-SelfTest }

# ---------------------------------------------------------------------------------------------------- real check
if (-not $EvidenceDir) { throw 'Give -EvidenceDir (the demo screenshots) and optionally -VideoFile, or use -SelfTest.' }
if (-not (Test-Path -LiteralPath $EvidenceDir)) {
  Write-Host "::error::Visual check: the evidence folder $EvidenceDir is missing, so no demo could be checked."
  exit 1
}
if (-not $ReportFile) { $ReportFile = Join-Path $EvidenceDir 'visual-check.txt' }

$lines = [System.Collections.Generic.List[string]]::new()
$errors = [System.Collections.Generic.List[string]]::new()
$lines.Add("Visual acceptance (scripts/ci/Test-DemoVisual.ps1): a blank window = one flat rectangle covering $([math]::Round($MaxBlankFraction * 100)) % or more of the screen, or one flat colour covering $([math]::Round($MaxColourShare * 100)) % or more.")
$shotResult = Test-Screenshots $EvidenceDir
$lines.AddRange([string[]]$shotResult.Lines); $errors.AddRange([string[]]$shotResult.Errors)
if ($VideoFile) {
  $tmp = Join-Path ($(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() })) 'boo-visual-frames'
  $videoResult = Test-Video $VideoFile (Get-DesktopShots $EvidenceDir) $tmp
  $lines.AddRange([string[]]$videoResult.Lines); $errors.AddRange([string[]]$videoResult.Errors)
}
foreach ($e in $errors) { $lines.Add("FAIL: $e") }
$verdict = if ($errors.Count -eq 0) { 'Visual check PASSED: no demo screenshot or video moment shows a blank window.' } else { "Visual check FAILED: $($errors.Count) problem(s)." }
$lines.Add($verdict)
$lines | ForEach-Object { Write-Host $_ }
Set-Content -LiteralPath $ReportFile -Value $lines -Encoding utf8
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value (@($verdict) + @($errors | ForEach-Object { "- $_" })) }
foreach ($e in $errors) { Write-Host "::error::Visual check: $e" }
if ($errors.Count -gt 0) { exit 1 }
exit 0
