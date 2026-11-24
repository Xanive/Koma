[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Payload,
    [string]$ClientId = $env:KOMA_MAL_CLIENT_ID
)

$ErrorActionPreference = 'Stop'
$directory = Get-Item -LiteralPath $Payload
if (-not $directory.PSIsContainer -or
    ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
    -not (Test-Path -LiteralPath (Join-Path $directory.FullName 'Koma.exe') -PathType Leaf)) {
    throw 'Use a fresh player publish directory for the app registration.'
}
$target = Join-Path $directory.FullName 'Koma.MyAnimeList.json'
if (Test-Path -LiteralPath $target) {
    throw 'The publish directory already contains an app registration. Use a fresh publish directory.'
}
if ([string]::IsNullOrEmpty($ClientId)) { return }
if ($ClientId.Length -lt 16 -or $ClientId.Length -gt 256 -or
    -not [regex]::IsMatch($ClientId, '\A[a-zA-Z0-9._-]+\z', [Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
    throw 'The MyAnimeList app registration is invalid.'
}

# The native application identifier is intentionally public in the finished app.
# Never accept or copy a client secret, protected user profile or account tokens.
$registration = [ordered]@{
    Version = 1
    ClientId = $ClientId
    RedirectUri = 'http://127.0.0.1:43857/callback'
} | ConvertTo-Json
$bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($registration + "`n")
$temporary = Join-Path $directory.FullName ('.koma-registration-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length) }
    finally { $stream.Dispose() }
    [IO.File]::Move($temporary, $target)
}
finally {
    [Array]::Clear($bytes, 0, $bytes.Length)
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
