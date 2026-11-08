$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$credential = $null
foreach ($line in [IO.File]::ReadAllLines((Join-Path $workspace '.env.local'))) {
    if ($line -match '^OPENAI_API_KEY=(.+)$') { $credential = $Matches[1].Trim().Trim('"').Trim("'") }
}
if ([string]::IsNullOrWhiteSpace($credential)) { throw 'Provider is not configured.' }
$socket = [Net.WebSockets.ClientWebSocket]::new()
$deadline = [Threading.CancellationTokenSource]::new(25000)
$socket.Options.SetRequestHeader('Authorization', ('Bearer ' + $credential))
$credential = $null
$timer = [Diagnostics.Stopwatch]::StartNew()
$result = [ordered]@{ success=$false; model='gpt-realtime-2.1'; proof='Connection and session configuration only; no microphone or acoustic proof'; utc=[DateTime]::UtcNow.ToString('O') }
function Read-Event {
    $bytes = [byte[]]::new(131072)
    $segment = [ArraySegment[byte]]::new($bytes)
    $stream = [IO.MemoryStream]::new()
    try {
        do {
            $received = $socket.ReceiveAsync($segment, $deadline.Token).GetAwaiter().GetResult()
            if ($received.MessageType -eq [Net.WebSockets.WebSocketMessageType]::Close) { throw 'Connection closed.' }
            $stream.Write($bytes, 0, $received.Count)
        } while (!$received.EndOfMessage)
        return ([Text.Encoding]::UTF8.GetString($stream.ToArray()) | ConvertFrom-Json)
    } finally { $stream.Dispose() }
}
try {
    [void]$socket.ConnectAsync([Uri]'wss://api.openai.com/v1/realtime?model=gpt-realtime-2.1', $deadline.Token).GetAwaiter().GetResult()
    $created = Read-Event
    if ($created.type -ne 'session.created') { throw 'Session creation was not acknowledged.' }
    $update = @{ type='session.update'; session=@{type='realtime'; model='gpt-realtime-2.1'; output_modalities=@('audio'); audio=@{input=@{format=@{type='audio/pcm';rate=24000}; transcription=@{model='gpt-4o-mini-transcribe'};turn_detection=@{type='server_vad'}};output=@{format=@{type='audio/pcm';rate=24000};voice='marin'}}}} | ConvertTo-Json -Depth 10 -Compress
    $payload = [ArraySegment[byte]]::new([Text.Encoding]::UTF8.GetBytes($update))
    [void]$socket.SendAsync($payload,[Net.WebSockets.WebSocketMessageType]::Text,$true,$deadline.Token).GetAwaiter().GetResult()
    for ($eventIndex=0; $eventIndex -lt 5; $eventIndex++) {
        $event = Read-Event
        if ($event.type -eq 'error') { throw 'Realtime rejected the session configuration.' }
        if ($event.type -eq 'session.updated') { $result.success=$true; break }
    }
    $result.elapsedMilliseconds=$timer.ElapsedMilliseconds
} catch { $result.error='Connection or configuration failed; no private server data logged.' }
finally { $socket.Abort(); $socket.Dispose(); $deadline.Dispose() }
$json = $result | ConvertTo-Json
$json | Set-Content (Join-Path $workspace 'artifacts/realtime-connection-probe.json')
$json
if (!$result.success) { exit 1 }
