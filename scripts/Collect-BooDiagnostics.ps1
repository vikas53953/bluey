<#
.SYNOPSIS
  Collects Boo diagnostics into one ZIP on your Desktop. Read-only: it changes nothing in Boo or Windows.

.DESCRIPTION
  Copies ONLY settings.json, appearance.json and redacted logs\crash-*.txt from Boo's data folder (%LOCALAPPDATA%\Boo, or BOO_DATA_DIR).
  It never copies credential.bin (the DPAPI-protected OpenAI key) or its temporary copies; it only lists
  file names and sizes. Focus history (sessions.db: task names and check results) is copied only with
  -IncludeFocusHistory. Also records Windows version, monitors and scaling, GPU, audio devices,
  microphone privacy setting, Boo's Run key, the running Boo process and recent Boo crash events.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Collect-BooDiagnostics.ps1
#>
[CmdletBinding()]
param([switch]$IncludeFocusHistory)

$ErrorActionPreference = 'Stop'

if ($env:BOO_DATA_DIR) { $dataDir = [IO.Path]::GetFullPath($env:BOO_DATA_DIR) }
else { $dataDir = Join-Path $env:LOCALAPPDATA 'Boo' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$zip = Join-Path ([Environment]::GetFolderPath('Desktop')) "Boo-diagnostics-$stamp.zip"
$stage = Join-Path ([IO.Path]::GetTempPath()) "Boo-diagnostics-$stamp"
New-Item -ItemType Directory -Path $stage | Out-Null

function Section([string]$title, [scriptblock]$body) {
    "===== $title ====="
    try { & $body | Out-String -Width 300 } catch { "(unavailable: $($_.Exception.Message))" }
}

try {
    # 1. Boo data folder: allowlisted files only. credential* is never copied, whatever the allowlist says.
    $allow = @('settings.json', 'appearance.json')
    if ($IncludeFocusHistory) { $allow += 'sessions.db' }
    $listing = @()
    if (Test-Path -LiteralPath $dataDir) {
        foreach ($file in Get-ChildItem -LiteralPath $dataDir -File -Force) {
            $listing += '{0,-50} {1,12} bytes  {2:u}' -f $file.Name, $file.Length, $file.LastWriteTimeUtc
            if (($allow -contains $file.Name) -and ($file.Name -notlike 'credential*')) {
                Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $stage $file.Name)
            }
        }
    } else { $listing += "Data folder not found: $dataDir" }
    # 1b. Redacted crash logs (Boo strips keys before writing them; we redact again below anyway).
    $logsDir = Join-Path $dataDir 'logs'
    if (Test-Path -LiteralPath $logsDir) {
        $logStage = Join-Path $stage 'logs'
        New-Item -ItemType Directory -Path $logStage -Force | Out-Null
        foreach ($log in Get-ChildItem -LiteralPath $logsDir -File -Filter 'crash-*.txt' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 10) {
            $listing += '{0,-50} {1,12} bytes  {2:u}' -f ("logs\" + $log.Name), $log.Length, $log.LastWriteTimeUtc
            Copy-Item -LiteralPath $log.FullName -Destination (Join-Path $logStage $log.Name)
        }
    }
    # Belt and braces: redact anything that looks like an OpenAI key in the copied text files.
    foreach ($json in Get-ChildItem -LiteralPath $stage -Recurse -File -Include *.json, *.txt) {
        $text = [IO.File]::ReadAllText($json.FullName)
        $clean = $text -replace 'sk-[A-Za-z0-9_\-]{8,}', 'sk-REDACTED'
        if ($clean -ne $text) { [IO.File]::WriteAllText($json.FullName, $clean) }
    }

    # 2. Per-monitor bounds and scaling in physical pixels.
    $monitorCode = @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class BooDiagDisplays {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct MONITORINFOEX {
    public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
  public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);
  [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
  [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
  [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
  public static List<string> Describe() {
    var lines = new List<string>();
    var previous = SetThreadDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2: physical pixels
    MonitorEnumProc proc = delegate(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data) {
      var info = new MONITORINFOEX(); info.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
      GetMonitorInfo(monitor, ref info);
      uint dpiX, dpiY; if (GetDpiForMonitor(monitor, 0, out dpiX, out dpiY) != 0) { dpiX = 0; }
      var b = info.rcMonitor;
      lines.Add(String.Format("{0}  origin=({1},{2})  {3}x{4} px  dpi={5}  scale={6}%  primary={7}",
        info.szDevice, b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top, dpiX, dpiX * 100 / 96, (info.dwFlags & 1) == 1));
      return true; };
    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
    GC.KeepAlive(proc);
    SetThreadDpiAwarenessContext(previous);
    return lines;
  }
}
'@

    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $report = @(
        "Boo diagnostics $stamp (local time). Data folder: $dataDir"
        "Focus history included: $IncludeFocusHistory"
        Section 'Data folder files (names and sizes only)' { $listing }
        Section 'Windows' {
            $os = Get-CimInstance Win32_OperatingSystem
            $nt = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
            "$($os.Caption) $($nt.DisplayVersion) build $($os.BuildNumber).$($nt.UBR) $($os.OSArchitecture)"
            "Culture: $((Get-Culture).Name)  UI: $((Get-UICulture).Name)  Keyboard layouts: $((Get-WinUserLanguageList).InputMethodTips -join ', ')"
            Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer, Model, @{n = 'RAM_GB'; e = { [math]::Round($_.TotalPhysicalMemory / 1GB, 1) } }
        }
        Section 'Monitors (physical pixels)' {
            if (-not ('BooDiagDisplays' -as [type])) { Add-Type -TypeDefinition $monitorCode }
            [BooDiagDisplays]::Describe()
        }
        Section 'Display scaling registry' {
            Get-ItemProperty 'HKCU:\Control Panel\Desktop' -Name LogPixels, Win8DpiScaling -ErrorAction SilentlyContinue | Select-Object LogPixels, Win8DpiScaling
            Get-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics' -Name AppliedDPI -ErrorAction SilentlyContinue | Select-Object AppliedDPI
        }
        Section 'Graphics' { Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate }
        Section 'Audio devices' { Get-CimInstance Win32_SoundDevice | Select-Object Name, Status }
        Section 'Microphone privacy (Allow / Deny)' {
            $mic = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone'
            "Apps: $((Get-ItemProperty $mic -Name Value -ErrorAction SilentlyContinue).Value)"
            "Desktop apps: $((Get-ItemProperty "$mic\NonPackaged" -Name Value -ErrorAction SilentlyContinue).Value)"
        }
        Section 'Start with Windows (HKCU Run value "Boo")' {
            $value = (Get-ItemProperty $runKey -Name Boo -ErrorAction SilentlyContinue).Boo
            if ($value) { $value } else { '(not set)' }
        }
        Section 'Environment' { "BOO_QA_CAPTURABLE=$env:BOO_QA_CAPTURABLE"; "BOO_DATA_DIR=$env:BOO_DATA_DIR" }
        Section 'Running Boo process' {
            $procs = @(Get-Process -Name Boo -ErrorAction SilentlyContinue)
            if ($procs.Count -eq 0) { '(Boo is not running)' }
            foreach ($p in $procs) {
                $p | Select-Object Id, Path, StartTime, @{n = 'CPU_s'; e = { [math]::Round($_.CPU, 1) } }, @{n = 'Memory_MB'; e = { [math]::Round($_.WorkingSet64 / 1MB) } }
                if ($p.Path) {
                    "Boo.exe version: $((Get-Item -LiteralPath $p.Path).VersionInfo.ProductVersion)"
                    "Boo.exe SHA-256: $((Get-FileHash -LiteralPath $p.Path -Algorithm SHA256).Hash)"
                    $note = Join-Path (Split-Path $p.Path) 'READ-ME-FIRST.txt'
                    if (Test-Path -LiteralPath $note) { Get-Content -LiteralPath $note -TotalCount 2 }
                }
            }
        }
        Section 'Boo crash events, last 7 days (Windows Application log)' {
            $filter = @{ LogName = 'Application'; StartTime = (Get-Date).AddDays(-7); ProviderName = '.NET Runtime', 'Application Error', 'Windows Error Reporting' }
            $events = @(Get-WinEvent -FilterHashtable $filter -MaxEvents 500 -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'Boo\.exe' } | Select-Object -First 20)
            if ($events.Count -eq 0) { '(none)' }
            foreach ($e in $events) { "--- $($e.TimeCreated) $($e.ProviderName) $($e.Id)"; $e.Message }
        }
    )
    [IO.File]::WriteAllText((Join-Path $stage 'diagnostics.txt'), ($report -join "`r`n"))

    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
    Write-Host "Saved $zip"
    Write-Host 'It contains no API key and no credential.bin. Open it to check before sending if you like.'
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
