# Third-party notices

This project embeds prebuilt third-party binaries in its single-file publish output.

## retoc

- **Source**: https://github.com/trumank/retoc
- **Vendored version**: v0.1.5 (`retoc_cli-x86_64-pc-windows-msvc.zip`)
- **Vendored at**: `src/UAssetEditor.App/vendor/retoc.exe`
- **License**: MIT (`src/UAssetEditor.App/vendor/retoc-LICENSE.txt`)
- **Purpose**: Converts between Unreal Engine's IoStore/Zen container format (`.utoc`/`.ucas`)
  and legacy `.pak` format. UAssetEditor shells out to it (see
  `UAssetEditor.Core.AssetSources.IoStore.RetocProcess`) so IoStore content can be browsed,
  converted, edited through the normal pak/loose-folder pipeline, and converted back.
- **Checked via**: SHA-256 of the downloaded release archive matched the checksum published
  alongside it on the GitHub release page before vendoring.

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
