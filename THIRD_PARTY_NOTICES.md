# Third-party notices

This project embeds prebuilt third-party binaries in its single-file publish output.

## retoc

- **Source**: https://github.com/gravenuance/retoc, a fork of https://github.com/trumank/retoc
- **Vendored version**: built locally from branch `feat/game-variant-marvel-rivals`, based on
  `8c47bd5`. It adds `--game rivals` (output byte-identical to retoc-rivals' `pack`, ported from
  natimerry/repak-rivals; see below), `to-zen --compression` and `to-zen --obfuscate`, and reads the
  plaintext directory index of obfuscated containers.
- **Vendored at**: `src/UAssetEditor.App/vendor/retoc.exe`
  (SHA-256 `22612284ef508266093f6d0357d1bdffc4aa5bbbd4e2d965325e5d736e8c5b58`)
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
  physics blueprints that were previously permanently unreadable by this tool.
- **License**: MIT (`vendor/UAssetAPI-LICENSE.txt`)
- **Notice**: `vendor/UAssetAPI-NOTICE.md` (attribution for adapted third-party segments,
  e.g. cue4parse, per upstream's own NOTICE)
- Embeds `repak_bind.dll`/`repak_bind.so`, a native binding to the `repak` Rust crate (also
  by trumank). See https://github.com/trumank/repak for that project's own license/attribution.
