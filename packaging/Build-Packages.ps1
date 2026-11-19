[CmdletBinding()]
param(
    [string]$Dotnet = 'dotnet',
    [string]$CompileInstaller = 'makensis.exe',
    [string]$SevenZip = '7z.exe',
    [string]$Python = 'python.exe',
    [string]$EngineCacheDirectory,
    [string]$MpvArchive,
    [string]$ShaderArchive,
    [string]$OutputDirectory,
    [string]$PublishDirectory,
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.1.0-beta.5',
    [switch]$LocalPreview
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'dist' }
if (-not $EngineCacheDirectory) { $EngineCacheDirectory = Join-Path $projectRoot '.deps/msys2-engine' }
$output = (New-Item -Path $OutputDirectory -ItemType Directory -Force).FullName
$buildRoot = (New-Item -Path (Join-Path $projectRoot '.build') -ItemType Directory -Force).FullName
$staging = (New-Item -Path (Join-Path $buildRoot ([Guid]::NewGuid().ToString('N'))) -ItemType Directory).FullName
$payload = Join-Path $staging 'payload'
$suffix = if ($LocalPreview) { '-local' } else { '' }
$name = "Koma-$Version-win-x64$suffix"
$portable = Join-Path $output "$name-portable.zip"
$installer = Join-Path $output "$name-setup.exe"
$checksums = Join-Path $output "$name-SHA256SUMS.txt"
$sourcesOutput = Join-Path $output "$name-engine-source.zip"

Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
    if ($PublishDirectory) {
        & (Join-Path $PSScriptRoot 'Assert-CleanPublishDirectory.ps1') -PublishDirectory $PublishDirectory
        New-Item -Path $payload -ItemType Directory | Out-Null
        Get-ChildItem -LiteralPath $PublishDirectory -Force |
            Where-Object { $_.Name -notin @('data', 'engine', 'portable_config', 'portable.marker') -and $_.Extension -ne '.pdb' } |
            Copy-Item -Destination $payload -Recurse -Force
    }
    else {
        Push-Location -LiteralPath $projectRoot
        try {
            & $Dotnet publish 'src/Koma/Koma.csproj' --configuration Release --runtime win-x64 --self-contained true --output $payload "-p:Version=$Version" '-p:DebugType=None' '-p:RestoreLockedMode=true'
            if ($LASTEXITCODE -ne 0) { throw 'Building the self-contained player failed.' }
        }
        finally { Pop-Location }
    }
    foreach ($required in @('Koma.exe', 'coreclr.dll', 'PresentationFramework.dll', 'player-input.conf')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $required) -PathType Leaf)) {
            throw "Missing $required in the self-contained Windows x64 build."
        }
    }
    $engine = Join-Path $payload 'engine'
    & (Join-Path $PSScriptRoot 'Prepare-Engine.ps1') -Destination $engine -MpvArchive $MpvArchive -ShaderArchive $ShaderArchive -SevenZip $SevenZip -ShadersOnly:(-not $LocalPreview)
    $sourceFile = Join-Path $staging 'engine-source.zip'
    if (-not $LocalPreview) {
        $noticeDirectory = Join-Path $payload 'engine-notices'
        & $Python (Join-Path $PSScriptRoot 'Stage-Msys2Engine.py') --cache $EngineCacheDirectory --engine $engine --notices $noticeDirectory --sources $sourceFile --seven-zip $SevenZip
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $sourceFile)) {
            throw 'The MSYS2 engine and corresponding source could not be verified. Public packages were not built.'
        }
    }
    Copy-Item -LiteralPath @((Join-Path $projectRoot 'LICENSE'), (Join-Path $projectRoot 'THIRD-PARTY.md')) -Destination $payload
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses/Anime4K-MIT.txt') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses/mpv-GPL-2.0.txt') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses/NSIS-COPYING.txt') -Destination $payload
    Copy-Item -LiteralPath @((Join-Path $PSScriptRoot 'licenses/WPF-UI-MIT.txt'),
        (Join-Path $PSScriptRoot 'licenses/WPF-UI-ThirdPartyNotices.txt')) -Destination $payload
    & (Join-Path $PSScriptRoot 'Copy-TorrentNotices.ps1') -Payload $payload
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $payload 'Koma.runtimeconfig.json') -Raw | ConvertFrom-Json
    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
    foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
        $package = "$($framework.name.ToLowerInvariant()).runtime.win-x64/$($framework.version)"
        $path = Join-Path $nugetRoot $package
        $notices = @(Get-ChildItem -LiteralPath $path -File | Where-Object Name -Match '^(LICENSE|THIRD-PARTY-NOTICES)(\.TXT)?$')
        if (-not $notices) { throw "Runtime notices for $package are missing. Use the same NuGet package cache as dotnet publish." }
        foreach ($notice in $notices) {
            Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $payload ('{0}-{1}.txt' -f $framework.name, $notice.BaseName))
        }
    }
    if ($LocalPreview) {
        'Private build for local testing. The pinned mpv binary requires its complete corresponding source before public redistribution.' |
            Set-Content -LiteralPath (Join-Path $payload 'LOCAL-TEST-BUILD.txt') -Encoding UTF8
    }

    $deletePaths = Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($payload.Length + 1).Replace('$', '$$')
        '    Delete "$INSTDIR\{0}"' -f $relative
    }
    $removeDirectories = Get-ChildItem -LiteralPath $payload -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {
        $relative = $_.FullName.Substring($payload.Length + 1).Replace('$', '$$')
        '    RMDir "$INSTDIR\{0}"' -f $relative
    }
    $manifest = Join-Path $staging 'uninstall.nsh'
    $deletePaths + $removeDirectories | Set-Content -LiteralPath $manifest -Encoding UTF8
    $appIcon = Join-Path $projectRoot 'src/Koma/Assets/Player.ico'
    & $CompileInstaller /V2 "/DINSTALLER=$installer" "/DPAYLOAD=$payload" "/DREMOVE_MANIFEST=$manifest" "/DAPP_VERSION=$Version" "/DAPP_ICON=$appIcon" (Join-Path $PSScriptRoot 'Install.nsi')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Compiling the installer failed.' }

    New-Item -Path (Join-Path $payload 'portable.marker') -ItemType File | Out-Null
    $temporaryZip = Join-Path $staging 'portable.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $temporaryZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    Move-Item -LiteralPath $temporaryZip -Destination $portable -Force
    & $SevenZip t $portable | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The portable ZIP failed its integrity check.' }
    $artifacts = @($portable, $installer)
    if (-not $LocalPreview) {
        Move-Item -LiteralPath $sourceFile -Destination $sourcesOutput -Force
        $artifacts += $sourcesOutput
    }
    $artifacts | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path -Leaf $_)
    } | Set-Content -LiteralPath $checksums -Encoding ASCII
    Get-Item -LiteralPath ($artifacts + $checksums) | Select-Object Name, Length
}
finally {
    if ($staging.StartsWith($buildRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}
