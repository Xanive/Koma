[CmdletBinding()]
param([Parameter(Mandatory)][string]$Payload)

$ErrorActionPreference = 'Stop'
$manifestFile = Join-Path $PSScriptRoot 'torrent-notices.json'
$manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
$lockFile = Join-Path $PSScriptRoot '../src/Koma/packages.lock.json'
$lock = Get-Content -LiteralPath $lockFile -Raw | ConvertFrom-Json
$dependencies = Get-Content -LiteralPath (Join-Path $Payload 'Koma.deps.json') -Raw | ConvertFrom-Json
if ($manifest.Version -ne 1 -or @($manifest.Components).Count -ne 3) { throw 'The torrent dependency notice manifest is invalid.' }

foreach ($component in $manifest.Components) {
    if ($component.Notice -ne [IO.Path]::GetFileName($component.Notice)) { throw 'A dependency notice filename is invalid.' }
    $dependency = $component.Id + '/' + $component.Version
    if ($dependencies.libraries.PSObject.Properties.Name -notcontains $dependency) {
        throw "The published dependency does not match the pinned notice: $dependency"
    }
    $locked = @($lock.dependencies.PSObject.Properties.Value | ForEach-Object {
        $_.PSObject.Properties | Where-Object Name -eq $component.Id | Select-Object -ExpandProperty Value
    })
    if (-not ($locked | Where-Object { $_.resolved -eq $component.Version -and $_.contentHash -eq $component.NuGetContentHash })) {
        throw "The package lock does not match the notice manifest: $dependency"
    }
    $notice = Join-Path $PSScriptRoot ('licenses/' + $component.Notice)
    if ((Get-FileHash -LiteralPath $notice -Algorithm SHA256).Hash.ToLowerInvariant() -ne $component.NoticeSha256) {
        throw "The dependency notice failed its checksum: $dependency"
    }
    Copy-Item -LiteralPath $notice -Destination $Payload
}
Copy-Item -LiteralPath $manifestFile -Destination $Payload
