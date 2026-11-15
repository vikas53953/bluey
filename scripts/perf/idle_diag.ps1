# Issue #124 diagnostic: what does Boo spend CPU on while it sits hidden and idle?
# Launches the published Boo.exe several times the way the perf budget test does (fresh data dir, BOO_PERF_DIAG
# set, one UI Automation lookup of the notch, mouse parked far away) and, once a second for -Seconds, records the
# process CPU seen from outside (what the perf test measures) next to the probe's own counters (user and kernel
# CPU, JIT methods and time, probe writes and their time, GC). Writes one samples-<n>.jsonl per launch.
# It measures only; it asserts nothing. scripts/perf/idle_diag_summary.py turns the samples into a table.
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [int]$Launches = 10,
    [int]$Seconds = 45
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -Namespace Diag -Name Native -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$exe = (Resolve-Path $ExePath).Path

for ($n = 1; $n -le $Launches; $n++) {
    if (Get-Process -Name Boo -ErrorAction SilentlyContinue) { throw 'Boo is already running; it is single-instance.' }
    $data = Join-Path ([IO.Path]::GetTempPath()) ("boo-diag-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $data | Out-Null
    $diag = Join-Path $OutDir "diag-$n.json"
    $env:BOO_QA_CAPTURABLE = '1'
    $env:BOO_DATA_DIR = $data
    $env:BOO_PERF_DIAG = $diag
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $proc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
    Remove-Item Env:BOO_PERF_DIAG, Env:BOO_DATA_DIR, Env:BOO_QA_CAPTURABLE

    # Like the perf test: find the notch through UI Automation, then park the mouse in the bottom-right corner.
    $cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $notch = $null
    while (-not $notch -and $clock.Elapsed.TotalSeconds -lt 60) {
        $notch = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
        if (-not $notch) { Start-Sleep -Milliseconds 50 }
    }
    $notchMs = [math]::Round($clock.Elapsed.TotalMilliseconds)
    [Diag.Native]::SetCursorPos([Diag.Native]::GetSystemMetrics(0) - 4, [Diag.Native]::GetSystemMetrics(1) - 4) | Out-Null

    $out = Join-Path $OutDir "samples-$n.jsonl"
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add((@{ launch = $n; notchFoundMs = $notchMs; pid = $proc.Id } | ConvertTo-Json -Compress))
    $deadline = $clock.Elapsed.TotalSeconds + $Seconds + 7
    $next = 1.0
    while ($clock.Elapsed.TotalSeconds -lt $deadline) {
        $wait = $next - $clock.Elapsed.TotalSeconds
        if ($wait -gt 0) { Start-Sleep -Milliseconds ([int]($wait * 1000)) }
        $next += 1.0
        $proc.Refresh()
        $tMs = $clock.Elapsed.TotalMilliseconds
        $cpuMs = $proc.TotalProcessorTime.TotalMilliseconds
        $text = $null
        $readError = 'file missing'
        for ($try = 0; $try -lt 5 -and -not $text; $try++) {
            try {
                # Share read/write/delete like the perf test's reader: Boo replaces the file by temp + move.
                $fs = [IO.FileStream]::new($diag, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete')
                try { $text = [IO.StreamReader]::new($fs).ReadToEnd().Trim() } finally { $fs.Dispose() }
            } catch { $readError = $_.Exception.GetType().Name; Start-Sleep -Milliseconds 10 }
        }
        $row = [ordered]@{ tMs = [math]::Round($tMs, 1); extCpuMs = [math]::Round($cpuMs, 3) }
        if ($text) { $row.diag = ($text | ConvertFrom-Json) } else { $row.diag = $null; $row.readError = $readError }
        $lines.Add(($row | ConvertTo-Json -Compress -Depth 5))
    }
    [IO.File]::WriteAllLines($out, $lines)
    Stop-Process -Id $proc.Id -Force
    $proc.WaitForExit(15000) | Out-Null
    Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
    Write-Host "launch $n done: $($lines.Count - 1) samples, notch found after $notchMs ms"
}
