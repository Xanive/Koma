# Koma

An offline Windows player for anime you already have. Browse **Videos → Anime**, save folders from other drives, resume an episode, and compare Anime4K upscaling with the original side by side.

Koma itself was completely vibe coded with Codex. Real-time upscaling uses [Anime4K by bloc97 and contributors](https://github.com/bloc97/Anime4K), and video playback uses [mpv](https://mpv.io/).

![Home screen with a saved folder, season, and local episodes](docs/home.png)

## Install and open your collection

1. Get the Windows 11 x64 installer or portable ZIP from [GitHub Releases](https://github.com/Xanive/Koma/releases). For the portable version, extract the whole ZIP into a writable folder and open `Koma.exe`.
2. Put video files or season folders in **Videos → Anime**. Setup creates this folder. In the portable app it appears on first launch, or you can make it yourself beforehand.
3. Open the player and choose an episode. To use files in another location, select **Add folder**. You can also drag files or a folder into the app to start playback.

Beta packages are unsigned and may show a Windows publisher or reputation warning. The release includes a `SHA256SUMS.txt` file so you can verify your download.

The player leaves video files in their original location. Installed app preferences and watch history are stored under `%LOCALAPPDATA%\Koma`. The portable copy stores them in its own `data` folder.

If you used an installed Local Anime Player beta, Koma loads your saved folders and watch history from it on first launch. To upgrade a portable beta, copy its `data` folder into the newly extracted Koma folder before launching.

## Playback controls

Use **Compare** to watch the original and Anime4K versions side by side. **Detail** zooms both views. **Audio**, **CC**, and **Chapters** let you switch tracks and jump to chapters when the file includes them. **Settings** lets you change seek and volume steps, preferred audio and subtitle languages, and the fullscreen control delay.

| Shortcut | Action |
| --- | --- |
| Left / Right | Seek backward / forward |
| Up / Down | Raise / lower volume |
| Ctrl+Left / Ctrl+Right | Play the previous / next episode in the queue |
| Space | Play / pause |
| F or F11 | Toggle fullscreen |
| Escape | Exit fullscreen |
| C | Toggle Compare |
| M | Toggle mute |
| Ctrl+O | Open files |

## Build locally

Use Windows 11 x64, the .NET 10 SDK selected by `global.json`, Python 3.11 or later, 7-Zip, and NSIS 3.13. Run these from the repository root in PowerShell with those tools available.

```powershell
dotnet build Koma.slnx -c Release
dotnet run --project tests/Koma.Tests/Koma.Tests.csproj -c Release
./packaging/Build-Packages.ps1
```

The first package build downloads the pinned MSYS2 engine, its corresponding source, and the shaders. It writes an installer, portable ZIP, corresponding-source ZIP, and SHA-256 checksums to `dist/`. Allow about 1.8 GB for downloads. Use `-EngineCacheDirectory` to reuse previously downloaded MSYS2 archives. See [engine provenance](docs/Engine-provenance.md) and [third-party components](THIRD-PARTY.md). The separate `-LocalPreview` option uses the original test engine for private testing.

## Draft a GitHub prerelease

Run **Windows build and release draft** from **Actions**, set the release version, and choose **draft**. The workflow verifies the locked engine inputs and prepares both Windows downloads together with the corresponding source for review. Follow the [release test guide](docs/Release-testing.md) before publishing.

## Report an issue

[Report a playback, library, or visual issue](https://github.com/Xanive/Koma/issues/new/choose). Include the Koma version, Windows version, and the steps that reproduce it.

## License

The app's source is licensed under [GPL-3.0-or-later](LICENSE).
