# Release testing

Use Windows 11 x64 and the exact installer and ZIP associated with the release's SHA-256 file. Use synthetic videos or media that you have permission to share for any public test evidence.

## Clean environment

1. Check the SHA-256 files. Disconnect networking. Install for the current account, then verify Windows **Videos → Anime** exists before opening the app. Put episodes 02 and 10 under a show and season folder. Launch from the Start menu and confirm both appear without starting playback automatically.
2. Repeat with the extracted portable ZIP on a machine without the .NET Desktop Runtime or mpv installed. Confirm watch history is written beside the portable player under `data`.
3. Play an episode partway, close and restart, and verify Continue Watching resumes it. Let it finish and verify the watched indicator and next episode. Test a removable drive, relocating a saved folder, Unicode paths, and a large collection.
4. Uninstall and confirm the app files and shortcut are removed and media is still available.

## Media and visuals

Use `tests/New-PlaybackFixtures.ps1 -OutputDirectory <scratch folder>` with an FFmpeg build that includes libx264 and libx265. The fixtures use test patterns and tones. They include a 90-second 1080p HEVC MKV with two audio tracks, styled ASS subtitles and two chapters, a short H.264 MP4, an external SRT, and a damaged MKV.

- Play MKV and MP4 clips covering H.264, HEVC, dual audio, chapters, styled ASS subtitles, and external subtitles. Test a damaged clip followed by a working episode.
- Verify manual Anime4K modes and sustained 1080p playback on the RTX 3060. Test the automatic tier on an integrated GPU and record when the active tier lowers.
- Seek while Compare is paused and verify that the two views settle within one frame. Verify Detail zoom and both panes after resizing and entering fullscreen.
- Check the empty player, active playback and fullscreen at 100%, 150% and 200% Windows scaling. Repeat on monitors with different scaling. Check menus, drag-to-seek, focus indicators and reduced motion.
- Verify the player icon in the title bar and the dark Audio, CC, Speed, Chapters, and Quality menus in windowed and fullscreen playback. Check text and selection marks against light video frames.

Record the Windows build, graphics driver, display scale and package hash. Include the available results with the beta draft. Promote the release to `v1.0.0` after completing these checks and resolving playback blockers and reproducible visual issues in the tested workflows.
