# Third-party notices

This project embeds prebuilt third-party binaries in its single-file publish output.

## Oodle (not distributed)

- **What**: `oo2core_<n>_win64.dll`, the Oodle compression library by Epic Games Tools (RAD Game Tools), which legacy Unreal 4 paks such as Days Gone's use (`COMPRESS_Custom`).
- **Not included**: it is proprietary, so neither this repository nor any release contains it. The app loads the user's own copy, from `REPAK_OODLE_DLL` or the game's install (README, "Oodle-compressed paks"; `docs/adr/0001-oodle-from-the-game-install.md`).

## retoc

- **Source**: https://github.com/gravenuance/retoc, a fork of https://github.com/trumank/retoc
- **Vendored version**: built from commit `4c97820` on branch `feat/game-variant-marvel-rivals`
  (the 2026-11-18 dependency pass; `cargo build --release --locked`). It adds `--game rivals` (output chunk-identical to retoc-rivals' `pack`, ported from
  natimerry/repak-rivals; see below), `to-zen --compression` and `to-zen --obfuscate`, and reads the
  plaintext directory index of obfuscated containers.
- **Vendored at**: `src/UAssetEditor.App/vendor/retoc.exe`
  (SHA-256 `ce81e7c21bcaa166c0d7d75735628d8ef0783024838b4e35b267e3240e9307ae`)
- **License**: MIT (`src/UAssetEditor.App/vendor/retoc-LICENSE.txt`)
- **Purpose**: Converts between Unreal Engine's IoStore/Zen container format (`.utoc`/`.ucas`)
  and legacy `.pak` format. UAssetEditor shells out to it (see
  `UAssetEditor.Core.AssetSources.IoStore.RetocProcess`) so IoStore content can be browsed,
  converted, edited through the normal pak/loose-folder pipeline, and converted back.

```
MIT License

Copyright (c) 2025 Truman Kilen and Archengius

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## repak-rivals

- **Source**: https://github.com/natimerry/repak-rivals @ `18605be`
- **Used for**: two ports, no binaries.
  - `src/UAssetEditor.Core/Games/Rivals/` is a C# port of its UAssetAPI fork's KawaiiPhysics and
    hidden-materials patches (`UAssetAPI/KawaiiPhysicsLegacyPorter.cs`, `UAssetAPI/HiddenMaterialsReader.cs`
    and the material count of `UAssetAPI/ExportTypes/SkeletalMeshExport.cs`). Licensed MIT, copyright (c) 2020 - 2026 atenfyr
    (`UAssetAPI/LICENSE` in that repository), the same text as `vendor/UAssetAPI-LICENSE.txt`.
  - The vendored retoc's Marvel Rivals mode is a Rust port of `retoc-rivals/`, licensed MIT, copyright
    (c) 2025 Truman Kilen and Archengius (`retoc-rivals/LICENSE`), the same text as
    `src/UAssetEditor.App/vendor/retoc-LICENSE.txt`.
- The repository's workspace declares `MIT OR Apache-2.0`.

## UAssetAPI

- **Source**: https://github.com/atenfyr/UAssetAPI
- **Vendored at**: `vendor/UAssetAPI/` (source, consumed via `ProjectReference` from
  `UAssetEditor.Core`)
- **Vendored version**: `master` @ `3228c1e86261aa08131f7ec0ff1a395f5d0b2a84` (2026-08-31) -
  not the `1.1.0` NuGet package, which predates upstream's `FInstancedStruct` support (added
  2026-05-25, commit `b23892f`) and was silently falling back to an unparseable `RawExport`
  for any asset using that type anywhere in its data - confirmed on 5 real Marvel Rivals
  physics blueprints that were previously permanently unreadable by this tool. Also carries upstream's
  `OptionalPropertyData` (commit `5d9a258`, 2026-10-13), which newer UE5 games' optional properties need (verified on upstream's UE 5.6 and 5.7 test assets).
- **License**: MIT (`vendor/UAssetAPI-LICENSE.txt`)
- **Notice**: `vendor/UAssetAPI-NOTICE.md` (attribution for adapted third-party segments,
  e.g. cue4parse, per upstream's own NOTICE)
- Embeds `repak_bind.dll`, a native binding to the `repak` Rust crate (also
  by trumank). See https://github.com/trumank/repak for that project's own license/attribution.
  The Windows `repak_bind.dll.gz` is built from https://github.com/gravenuance/repak branch
  `feat/marvel-rivals-variant` @ `a347bd3` (`cargo build --release -p repak_bind`; SHA-256 of the
  uncompressed DLL `ef0bd9e7bfb29d6a4857172a7e87897a94df360907b59d7405c5c796597d2460`): upstream's `cs-bindings`
  line with our legacy-compression fix, upstream master's `1ef2c6f` and `355b5f6`, Marvel Rivals' pak format
  (`PakVariant`), a boundary that turns every panic in an export into an error, and an Oodle loader that loads
  only the absolute path in `REPAK_OODLE_DLL` and never downloads (previous vendor:
  `feat/upstream-read-fixes` @ `09dc525`), with the dependencies at their latest majors (aes 0.9,
  sha1/sha2 0.11, ...). `vendor/UAssetAPI/Pak/{Interop,RePak}.cs` follow that branch's `repak_cs/RePak/`.
  No Linux library is embedded (the app is Windows-only); pak access elsewhere throws `PlatformNotSupportedException`.
