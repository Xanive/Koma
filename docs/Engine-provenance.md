# Building Koma

These instructions are for contributors. To use the player, download it from [Releases](https://github.com/Xanive/Koma/releases).

## Build and test

Use Windows 11 x64, the .NET 10 SDK selected by `global.json`, Python 3.11 or later, 7-Zip and NSIS 3.13. Run from the repository root in PowerShell:

```powershell
dotnet build Koma.slnx -c Release
dotnet tests/Koma.Tests/bin/Release/net10.0/Koma.Tests.dll
dotnet tests/Koma.Torrent.Tests/bin/Release/net10.0-windows/Koma.Torrent.Tests.dll
```

## Make Windows packages

With the tools above on PATH:

```powershell
./packaging/Build-Packages.ps1
```

The first build downloads about **1.8 GB** of pinned engine, source and shader archives. It writes an installer, portable ZIP, matching engine-source ZIP and checksums to `dist/`.

Use `-EngineCacheDirectory <folder>` to reuse cached `binary-packages/` and `source-packages/`. Keep release archives: mirrors can remove old versions. If using `-PublishDirectory`, supply a fresh publish folder without profiles, logs or test files.

For GitHub builds, open **Actions > Windows build and release draft**, set the version and enable **draft**. Review the [release checklist](Release-testing.md), then publish all generated assets together.

**Keep `-LocalPreview` builds private.** They use the older test engine described in [third-party notices](../THIRD-PARTY.md).

## Where the engine comes from

Public packages use **MSYS2 UCRT64 mpv 0.42.0-2**. The [engine lock](../packaging/msys2-engine.lock.json) records exact binaries, sources, licenses and additional build inputs. `Stage-Msys2Engine.py` verifies those checksums before packaging.

The engine-source ZIP includes the lock, upstream archives, build notes, notices and replacement recipes. Ship it alongside both Windows downloads. Packaged notices and original build records are under `engine-notices/`.

<details>
<summary>Updating or rebuilding the engine</summary>

1. Save the official `ucrt64.files` snapshot and its SHA-256. Start with the intended UCRT64 `mpv.exe` package.
2. Follow normal and delay imports through its DLLs. Record each non-Windows dependency's package and file hashes; retain Windows system imports for review. Use the UCRT64 variants identified by the build records and include the Vulkan loader.
3. Match source packages by base and version. Compare each PKGBUILD hash with `pkgbuild_sha256sum` in the binary's `.BUILDINFO`; verify `.SRCINFO` inputs and checksums. Include generated headers and statically linked shader sources, even when they do not appear in DLL imports.
4. Test launching with only Windows system directories on PATH, then repeat hardware playback checks before replacing the release lock.

To rebuild a component, extract its `.src.tar.zst` and use its recorded dependencies in an MSYS2 UCRT64 shell. The archive contains `.SRCINFO`, source and distribution patches. Follow the [MSYS2 package guide](https://www.msys2.org/dev/creating-packages/).

For **ngtcp2 1.26.0-3**, use the replacement PKGBUILD in `recipes/`. Its hash matches the binary's BUILDINFO; it adds the openssl-3 build dependency without changing the source payload. [Exact change](https://github.com/msys2/MINGW-packages/commit/f6fe8c6a81c3f6e51c900d9b0ceb4bf87f615b17).

Koma does not rely on external mpv configuration, streaming helpers, VapourSynth scripts or optional plugins.

</details>
