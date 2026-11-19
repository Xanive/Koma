# Third-party components

Koma's application code uses GPL-3.0-or-later. Bundled components retain their own licenses.

| Component | Provenance and license |
| --- | --- |
| WPF UI 4.3.0 | [WPF UI source](https://github.com/lepoco/wpfui/tree/4.3.0), MIT. Its license and third-party notices ship in both packages. |
| MonoTorrent 3.0.2 | Built-in BitTorrent engine, MIT. [Exact upstream source](https://github.com/alanmcgovern/monotorrent/tree/e78faebd0aec117146cffccaaea987ab0629eec0); notice copied from the pinned NuGet package. |
| Mono.Nat 3.0.0 | MonoTorrent dependency, MIT. [Exact upstream source](https://github.com/mono/Mono.Nat/tree/04754b1e5f139559736c41bf23c1752564d178d9); notice copied from the pinned NuGet package. |
| ReusableTasks 4.0.0 | MonoTorrent dependency, MIT. [Exact upstream source](https://github.com/alanmcgovern/ReusableTasks/tree/3c0c1929375b3b3ddc97ff84fe6c59b024f2e85b); notice copied from the pinned NuGet package. |
| Anime4K 4.0.1 GLSL | [bloc97/Anime4K source](https://github.com/bloc97/Anime4K/tree/v4.0.1), MIT. The [upstream Windows guide](https://github.com/bloc97/Anime4K/blob/master/md/GLSL_Instructions_Windows_MPV.md) links to the pinned Windows preset archive. The included GLSL is source code. |
| mpv | [MSYS2 UCRT64 mpv 0.42.0-2](https://packages.msys2.org/packages/mingw-w64-ucrt-x86_64-mpv), GPL-2.0-or-later. [mpv licensing](https://github.com/mpv-player/mpv/blob/master/Copyright) |
| FFmpeg | Shared libraries from [MSYS2 UCRT64 FFmpeg 9.0.2-4](https://packages.msys2.org/packages/mingw-w64-ucrt-x86_64-ffmpeg), GPL-3.0-or-later in this build. [FFmpeg licensing](https://ffmpeg.org/legal.html) |
| .NET and WPF | Microsoft .NET 10 runtime packs. Their license and available third-party notices ship in the packages. [Runtime source](https://github.com/dotnet/runtime), [WPF source](https://github.com/dotnet/wpf) |
| NSIS | Installer bootstrap and standard plugins. [NSIS license](https://nsis.sourceforge.io/License), [NSIS source](https://github.com/nsis-dev/nsis). |

## Corresponding source and notices

The application [NuGet lock file](src/Koma/packages.lock.json) records exact direct and transitive package versions and NuGet content hashes. Packaging restores in locked mode. The [torrent notice manifest](packaging/torrent-notices.json) records the three torrent dependency notices, their SHA-256 hashes, upstream source commits, NuGet content hashes and downloaded package archive SHA-512 hashes. Packaging checks the published dependency versions against this manifest and the lock, verifies each notice, and ships the notice files and manifest with both packages. These MIT components retain their own copyright and permission notices.

Use a fresh `dotnet publish` directory when supplying `-PublishDirectory`. Packaging rejects profiles, torrent metadata, logs, testing markers, harnesses and synthetic fixtures rather than copying a running user's preview folder into a release.

The [MSYS2 engine lock](packaging/msys2-engine.lock.json) identifies the exact packages, individual DLLs, source archives, and licenses. Release packages contain the notices and the MSYS2 build records under `engine-notices/`. Keep the matching `-engine-source.zip` available alongside both binary downloads. The ZIP includes the matching code, build recipes and distribution patches. See [engine provenance](docs/Engine-provenance.md) for details.

Builds with `-LocalPreview` use the original [Shinchiro test build](https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20261111), pinned separately in [dependencies.json](packaging/dependencies.json). That older test binary reports a modified libplacebo checkout whose exact full corresponding source was not established. Keep those local-preview packages private.
