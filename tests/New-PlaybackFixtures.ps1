[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$OutputDirectory,
    [string]$FFmpeg = 'ffmpeg'
)

$ErrorActionPreference = 'Stop'
$media = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
$ass = Join-Path $media 'styled.ass'
$srt = Join-Path $media 'external test.srt'
$chapters = Join-Path $media 'chapters.txt'
$video = Join-Path $media 'Episode 02 HEVC.mkv'
$second = Join-Path $media 'Episode 10 H264.mp4'

@'
[Script Info]
ScriptType: v4.00+
PlayResX: 1920
PlayResY: 1080
[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Test,Arial,62,&H0000FFFF,&H00000000,&H00150010,&H00000000,-1,0,0,0,100,100,0,0,1,4,1,2,20,20,85,1
[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.25,0:01:29.90,Test,,0,0,0,,{\i1}Styled ASS fixture{\i0}
'@ | Set-Content -LiteralPath $ass -Encoding ascii

@'
1
00:00:00,250 --> 00:01:29,900
External subtitle fixture
'@ | Set-Content -LiteralPath $srt -Encoding ascii

@'
;FFMETADATA1
[CHAPTER]
TIMEBASE=1/1000
START=0
END=15000
title=Opening
[CHAPTER]
TIMEBASE=1/1000
START=15000
END=90000
title=Main scene
'@ | Set-Content -LiteralPath $chapters -Encoding ascii

& $FFmpeg -hide_banner -loglevel error -y `
    -f lavfi -i 'testsrc2=size=1920x1080:rate=24:duration=90' `
    -f lavfi -i 'sine=frequency=440:sample_rate=48000:duration=90' `
    -f lavfi -i 'sine=frequency=660:sample_rate=48000:duration=90' `
    -i $ass -i $chapters `
    -map '0:v' -map '1:a' -map '2:a' -map '3:s' -map_metadata 4 -map_chapters 4 `
    -c:v libx265 -preset ultrafast -x265-params 'pools=4:frame-threads=2:log-level=error' -crf 31 `
    -c:a aac -b:a 96k -c:s ass `
    -metadata:s:a:0 language=jpn -metadata:s:a:0 'title=Japanese test tone' `
    -metadata:s:a:1 language=eng -metadata:s:a:1 'title=English test tone' `
    -metadata:s:s:0 language=eng -metadata:s:s:0 'title=Styled English' $video
if ($LASTEXITCODE -ne 0) { throw 'FFmpeg could not generate the HEVC fixture.' }

& $FFmpeg -hide_banner -loglevel error -y -f lavfi -i 'testsrc2=size=854x480:rate=24:duration=12' `
    -c:v libx264 -preset ultrafast -pix_fmt yuv420p $second
if ($LASTEXITCODE -ne 0) { throw 'FFmpeg could not generate the H.264 fixture.' }

'Malformed fixture' | Set-Content -LiteralPath (Join-Path $media 'Broken.mkv') -Encoding ascii
Get-Item -LiteralPath $video, $second, $srt
