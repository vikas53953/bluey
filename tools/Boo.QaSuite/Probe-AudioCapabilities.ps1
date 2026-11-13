$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Audio capability probe is restricted to GitHub-hosted VMs.'
}
$services = foreach ($name in 'AudioEndpointBuilder', 'Audiosrv') {
    $service = Get-Service -Name $name
    $before = $service.Status.ToString()
    if ($service.StartType -eq 'Disabled') { Set-Service -Name $name -StartupType Manual }
    if ($service.Status -ne 'Running') { Start-Service -Name $name }
    $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(15))
    [pscustomobject]@{ name = $name; before = $before; after = $service.Status.ToString() }
}
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class BooAudioCapabilityProbe {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct InputCaps {
        public ushort Manufacturer, Product;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Formats;
        public ushort Channels, Reserved;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct Format {
        public ushort Tag, Channels;
        public uint SampleRate, BytesPerSecond;
        public ushort BlockAlign, Bits, Extra;
    }
    [DllImport("winmm.dll")] public static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll")] public static extern uint waveOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint waveInGetDevCapsW(UIntPtr index, out InputCaps caps, uint size);
    [DllImport("winmm.dll")]
    private static extern uint waveInOpen(out IntPtr handle, uint index, ref Format format, IntPtr callback, IntPtr instance, uint flags);
    public static string InputName(uint index) {
        InputCaps caps;
        uint result = waveInGetDevCapsW(new UIntPtr(index), out caps, (uint)Marshal.SizeOf(typeof(InputCaps)));
        if (result != 0) throw new InvalidOperationException("waveInGetDevCapsW returned " + result);
        return caps.Name;
    }
    public static uint QueryPcm24kMono16(uint index) {
        Format format = new Format { Tag = 1, Channels = 1, SampleRate = 24000, BytesPerSecond = 48000, BlockAlign = 2, Bits = 16, Extra = 0 };
        IntPtr ignored;
        return waveInOpen(out ignored, index, ref format, IntPtr.Zero, IntPtr.Zero, 1); // WAVE_FORMAT_QUERY: no capture is opened
    }
}
"@
$inputCount = [BooAudioCapabilityProbe]::waveInGetNumDevs()
$outputCount = [BooAudioCapabilityProbe]::waveOutGetNumDevs()
$inputs = @(for ($index = 0; $index -lt $inputCount; $index++) {
    $result = [BooAudioCapabilityProbe]::QueryPcm24kMono16([uint32]$index)
    [pscustomobject]@{ index = $index; name = [BooAudioCapabilityProbe]::InputName([uint32]$index); query_result = $result; pcm24k_mono16_supported = ($result -eq 0) }
})
$receipt = [pscustomobject]@{
    scope = 'environment capability only; no Boo.exe voice UI proof'
    runner_environment = $env:RUNNER_ENVIRONMENT
    observed_utc = [DateTime]::UtcNow.ToString('o')
    wave_in_count = $inputCount
    wave_out_count = $outputCount
    inputs = $inputs
    services = @($services)
}
$folder = 'artifacts/qa-evidence/capabilities'
New-Item -ItemType Directory -Force -Path $folder | Out-Null
$receipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $folder 'audio.json') -Encoding utf8
$receipt | ConvertTo-Json -Depth 6
if ($inputCount -eq 0) { Write-Output '::warning::No WinMM capture endpoints observed after audio services started; app microphone journeys remain BLOCKED.' }