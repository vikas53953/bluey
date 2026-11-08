param(
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'Boo')
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$source = Join-Path $workspace '.env.local'
$destination = Join-Path ([IO.Path]::GetFullPath($DataDirectory)) 'credential.bin'
$credential = $null
$plaintext = $null
$roundtrip = $null
$temporary = $null
try {
    foreach ($line in [IO.File]::ReadAllLines($source)) {
        if ($line -match '^OPENAI_API_KEY=(.+)$') {
            $credential = $Matches[1].Trim().Trim('"').Trim("'")
        }
    }
    if ([string]::IsNullOrWhiteSpace($credential)) { throw 'Missing credential.' }
    $plaintext = [Text.Encoding]::UTF8.GetBytes($credential)
    $credential = $null
    $protected = [Security.Cryptography.ProtectedData]::Protect($plaintext, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    $temporary = $destination + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllBytes($temporary, $protected)
    $roundtrip = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($temporary), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    if (-not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals($plaintext, $roundtrip)) { throw 'Verification failed.' }
    [IO.File]::Move($temporary, $destination, $true)
    [ordered]@{ success = $true; protection = 'Windows DPAPI CurrentUser'; destination = $destination; sourceRetained = $true } | ConvertTo-Json
} catch {
    throw 'Protected credential import failed. No credential or private error detail was logged.'
} finally {
    $credential = $null
    $line = $null
    $Matches = $null
    if ($plaintext) { [Array]::Clear($plaintext) }
    if ($roundtrip) { [Array]::Clear($roundtrip) }
    if ($temporary -and [IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
}
