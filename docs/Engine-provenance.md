# Windows engine provenance

Release builds use the dynamically linked MSYS2 UCRT64 build of mpv 0.42.0-2. The lock file pins the exact binary archive, runtime files, compiled recipe hashes, and corresponding source archives for every package. It also records additional inputs used for statically linked shader code and generated headers. The local preview build still uses the original pinned Shinchiro test engine.

## Verify or reproduce the release bundle

Use Python 3.11 or later and 7-Zip. From the repository root run the standard package script without `-LocalPreview`. It invokes `packaging/Stage-Msys2Engine.py`, which verifies each locked checksum, extracts the pinned files, and builds the corresponding-source ZIP. Set `-EngineCacheDirectory` to reuse a cache containing `binary-packages/` and `source-packages/`. Save a copy of the package archives used for releases because availability at mirrors can change.

The engine lock was captured from the official UCRT64 files database. Runtime dependencies are the recursive imports and delay imports of `mpv.exe` and its DLLs. The lock retains the Windows system imports for review. The application uses no external mpv configuration, streaming helper, or VapourSynth scripts and does not bundle optional executables or plugins.

When updating the lock, preserve a copy of the official `ucrt64.files` snapshot and record its SHA-256 before resolving any packages. Start from the `mpv.exe` in the intended UCRT64 package. Inspect each EXE and DLL's normal and delay import tables. Resolve each non-Windows import to its package in that snapshot and record the SHA-256 of both the package archive and the individual file. Use the standard UCRT64 variants from the package's build records where the database lists alternative owners. Include the Vulkan loader even if the development PC's graphics driver already supplies one. Confirm the result with a launch using only the Windows system directories on PATH.

For each archive, look up its source package by the compiled package base and version. Compare the extracted PKGBUILD SHA-256 to the `pkgbuild_sha256sum` in the binary's `.BUILDINFO`. Check that the source ZIP includes the inputs listed by `.SRCINFO` with their recorded checksums. Inspect the build recipes for generated and statically linked components whose sources do not appear in the DLL import graph. Use the exact version listed by the parent package's build record. Repeat the hardware playback tests before replacing a release lock.

To inspect a component's original build, extract its `.src.tar.zst` from the source ZIP. Compare the PKGBUILD hash with its binary's `licenses/<package>/BUILDINFO` record. The archive also contains `.SRCINFO`, upstream code or git sources, and distribution patches. See the MSYS2 guide for rebuilding a package in an MSYS2 UCRT64 shell with its recorded build dependencies. [MSYS2 package creation guide](https://www.msys2.org/dev/creating-packages/)

For ngtcp2 1.26.0-3, use the exact recipe in `recipes/` in place of the recipe in its upstream source package. The replacement's SHA-256 matches the binary BUILDINFO. The only difference is an additional openssl-3 build dependency. The source payload and checksums match the archived recipe. [Exact MSYS2 change](https://github.com/msys2/MINGW-packages/commit/f6fe8c6a81c3f6e51c900d9b0ceb4bf87f615b17)

Publish the source ZIP alongside the installer and the portable ZIP. The app packages include the engine notices and compiled package records. The source ZIP also includes the full lock and build notes.
