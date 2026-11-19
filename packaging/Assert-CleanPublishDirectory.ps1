[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory)

$ErrorActionPreference = 'Stop'
$root = Get-Item -LiteralPath $PublishDirectory
if (-not $root.PSIsContainer -or ($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
    -not (Test-Path -LiteralPath (Join-Path $root.FullName 'Koma.exe') -PathType Leaf)) {
    throw 'Use a clean dotnet publish directory containing Koma.exe.'
}

foreach ($entry in Get-ChildItem -LiteralPath $root.FullName -Force -Recurse) {
    $badDirectory = $entry.PSIsContainer -and $entry.Name -match '^(?:data|private|fixtures?|synthetic(?:[-_].*)?|.*(?:harness|smoke|test-data))$'
    $badFile = -not $entry.PSIsContainer -and (
        ($entry.Extension -eq '.marker' -and $entry.Name -ne 'portable.marker') -or
        $entry.Extension -in @('.log', '.torrent', '.fastresume') -or
        $entry.Name -match '^(?:PublicationCheck|MatchingCheck|RecognitionCheck|TrackingCheck|DownloadCheck|TorrentCheck|Check|.*Harness|.*Smoke|.*Fixture|.*Tests?)\.(?:dll|exe|deps\.json|runtimeconfig\.json)$' -or
        $entry.Name -match '^(?:mal-(?:client-id|account)|download-jobs|downloads).*\.(?:bin|json)$' -or
        $entry.Name -match '^(?:matching|tracking|recognition|download|fixture)[-_].*\.png$'
    )
    if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $badDirectory -or $badFile) {
        throw 'The supplied publish folder contains private app data or testing files. Publish to a fresh directory before packaging.'
    }
}
