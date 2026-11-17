[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Destination,
    [string]$MpvArchive,
    [string]$ShaderArchive,
    [string]$CacheDirectory,
    [string]$SevenZip = '7z.exe',
    [switch]$ShadersOnly
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if (-not $CacheDirectory) { $CacheDirectory = Join-Path $PSScriptRoot '../.deps' }
$dependencies = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dependencies.json') -Raw | ConvertFrom-Json
$CacheDirectory = (New-Item -Path $CacheDirectory -ItemType Directory -Force).FullName

function Get-PinnedArchive($Dependency, [string]$SuppliedPath) {
    $path = if ($SuppliedPath) { (Resolve-Path -LiteralPath $SuppliedPath).Path } else { Join-Path $CacheDirectory $Dependency.file }
    if (-not $SuppliedPath -and -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $download = "$path.download"
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $Dependency.url -OutFile $download
            if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ine $Dependency.sha256) {
                throw "Unexpected checksum for $($Dependency.file)."
            }
            Move-Item -LiteralPath $download -Destination $path
        }
        finally {
            if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download }
        }
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $Dependency.sha256) {
        throw "Unexpected checksum for $path."
    }
    return $path
}

$shader = Get-PinnedArchive $dependencies.anime4k $ShaderArchive
$engine = (New-Item -Path $Destination -ItemType Directory -Force).FullName
$temporary = (New-Item -Path (Join-Path $CacheDirectory ('shader-' + [Guid]::NewGuid().ToString('N'))) -ItemType Directory).FullName
try {
    if (-not $ShadersOnly) {
        $mpv = Get-PinnedArchive $dependencies.mpv $MpvArchive
        # Windows 11 includes D3DCompiler_47. Ship only the player from this archive.
        & $SevenZip e $mpv mpv.exe mpv.com "-o$engine" -y | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $engine 'mpv.exe'))) {
            throw 'mpv could not be extracted.'
        }
    }
    Expand-Archive -LiteralPath $shader -DestinationPath $temporary
    Copy-Item -LiteralPath (Join-Path $temporary 'shaders') -Destination $engine -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $temporary 'input.conf') -Destination (Join-Path $engine 'shader-presets.conf') -Force
}
finally {
    $cachePrefix = $CacheDirectory.TrimEnd('\') + '\'
    if ($temporary.StartsWith($cachePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
