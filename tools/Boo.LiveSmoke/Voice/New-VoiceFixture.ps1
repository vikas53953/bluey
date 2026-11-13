<#
.SYNOPSIS
  Regenerates the synthetic spoken question used by the live voice round trip (LiveVoiceRoundTripTests).

.DESCRIPTION
  Uses Windows' built-in offline speech synthesizer (System.Speech, SAPI): no microphone, no network, no AI service, no
  cost. Writes 24 kHz, 16-bit, mono PCM in a WAV container, the format Boo's OpenAI Realtime session expects
  (RealtimeProtocol.SessionUpdate: audio/pcm, rate 24000). Run it in Windows PowerShell 5.1 (System.Speech ships with
  .NET Framework):

    powershell -NoProfile -File tools/Boo.LiveSmoke/Voice/New-VoiceFixture.ps1

  The checked-in WAV is the source of truth for CI; regenerate it only when the question changes, and update the
  expected words in LiveVoiceRoundTripTests in the same commit.
#>
param(
    [string]$Text = 'What is two plus three? Please answer with just the number.',
    [string]$OutFile = (Join-Path $PSScriptRoot 'what-is-two-plus-three.wav')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
    $synth.Rate = 0
    $format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(24000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
    $synth.SetOutputToWaveFile($OutFile, $format)
    $synth.Speak($Text)
} finally {
    $synth.Dispose()
}
$bytes = (Get-Item $OutFile).Length
"Wrote $OutFile ($bytes bytes, about {0:0.0} s of 24 kHz 16-bit mono) with voice '{1}'" -f (($bytes - 44) / 48000), (New-Object System.Speech.Synthesis.SpeechSynthesizer).Voice.Name
