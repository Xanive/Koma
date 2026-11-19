# Release checklist

Use the exact Windows 11 x64 packages and release checksums. Share only synthetic or permitted media.

## Installation and library

- [ ] Install offline on a clean PC without .NET or mpv.
- [ ] Put episodes 02 and 10 in **Videos → Anime** before launch. Confirm correct order and no automatic playback.
- [ ] Test the portable ZIP, empty folders, Unicode names and large collections.
- [ ] Check resume, watched markers and next-episode playback after restarting.
- [ ] Disconnect and relocate a saved folder; progress should survive.
- [ ] Update and uninstall. Media and user data should remain.

## Playback and appearance

- [ ] Check MKV/MP4, H.264/HEVC, dual audio, ASS/external subtitles and chapters. Recover from a damaged clip.
- [ ] Test sustained 1080p upscaling on the RTX 3060 and Auto fallback on integrated graphics.
- [ ] Check windowed, maximized and fullscreen layouts at 100%, 150% and 200%, including mixed-DPI monitors.
- [ ] Check readable Audio/CC menus, the app icon, keyboard focus, scrubbing, reduced motion and fullscreen control fading.
- [ ] Paused Compare should settle within one frame after seeking. Check Detail zoom and autoplay.

## Tracking and downloads

- [ ] Test MyAnimeList sign-in, list loading, token refresh, account switching and disconnecting.
- [ ] Test clear and ambiguous matches. One confirmation should save the show and episode number.
- [ ] Watch past 80% coverage and verify the correct MAL update. Seeking to the end, replaying or watching out of order must not falsely advance progress.
- [ ] Pause, restart and resume downloads. Completed episodes should appear without disrupting playback.
- [ ] Recover old completed `.!mt` files. Preserve damaged files and filename conflicts.

## Record the result

Record the package hash, Windows build, GPU/driver and display scale. Beta notes should name remaining gaps. Tag `v1.0.0` only after checks pass and playback or visual blockers are resolved.

<details>
<summary>Generate playback fixtures</summary>

With FFmpeg including libx264 and libx265:

```powershell
./tests/New-PlaybackFixtures.ps1 -OutputDirectory "$env:TEMP/Koma-fixtures"
```

Creates synthetic video, dual audio, subtitles, chapters and a damaged file.

</details>
