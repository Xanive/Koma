# Credits and licenses

Koma's application code is [GPL-3.0-or-later](LICENSE). Bundled components keep their own licenses.

| Component | Version and license | Source |
| --- | --- | --- |
| Anime4K by bloc97 and contributors | 4.0.1 GLSL / MIT | [Anime4K](https://github.com/bloc97/Anime4K/tree/v4.0.1) |
| mpv | MSYS2 UCRT64 0.42.0-2 / GPL-2.0-or-later | [Package](https://packages.msys2.org/packages/mingw-w64-ucrt-x86_64-mpv), [license](https://github.com/mpv-player/mpv/blob/master/Copyright) |
| FFmpeg | MSYS2 UCRT64 9.0.2-4 / GPL-3.0-or-later in this build | [Package](https://packages.msys2.org/packages/mingw-w64-ucrt-x86_64-ffmpeg), [license](https://ffmpeg.org/legal.html) |
| WPF UI | 4.3.0 / MIT | [Source](https://github.com/lepoco/wpfui/tree/4.3.0) |
| MonoTorrent | 3.0.2 / MIT | [Pinned source](https://github.com/alanmcgovern/monotorrent/tree/e78faebd0aec117146cffccaaea987ab0629eec0) |
| Mono.Nat | 3.0.0 / MIT | [Pinned source](https://github.com/mono/Mono.Nat/tree/04754b1e5f139559736c41bf23c1752564d178d9) |
| ReusableTasks | 4.0.0 / MIT | [Pinned source](https://github.com/alanmcgovern/ReusableTasks/tree/3c0c1929375b3b3ddc97ff84fe6c59b024f2e85b) |
| .NET and WPF | .NET 10 runtime packs | [Runtime](https://github.com/dotnet/runtime), [WPF](https://github.com/dotnet/wpf) |
| NSIS | Installer and standard plugins | [Source](https://github.com/nsis-dev/nsis), [license](https://nsis.sourceforge.io/License) |

## What ships with a release

Both Windows packages include dependency licenses and notices. Engine notices and build records are under `engine-notices/`. Anime4K's included GLSL files are source code; its Windows presets follow the [upstream guide](https://github.com/bloc97/Anime4K/blob/master/md/GLSL_Instructions_Windows_MPV.md).

Keep the matching **engine-source ZIP** alongside the installer and portable ZIP. It contains the engine's corresponding code, build recipes and patches. Koma's application source is available from the release's GitHub source archives.

Exact versions, source links and checksums live in the [NuGet lock](src/Koma/packages.lock.json), [torrent notice manifest](packaging/torrent-notices.json) and [engine lock](packaging/msys2-engine.lock.json). Packaging checks these records and the shipped notices. See the [build guide](docs/Engine-provenance.md).

## Private test engine

`-LocalPreview` uses the older [Shinchiro test build](https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20261111), pinned in [dependencies.json](packaging/dependencies.json). Its exact modified libplacebo source was not established. Keep these test packages private; public releases use the source-matched MSYS2 engine.
