# Changelog

All notable changes to this project are documented here.

## [Unreleased]

### Added
- `script --plan` edits many assets in one run: each plan line pairs an asset with its ops file, assets run in parallel, and the usmap loads once. Opening and saving 138 physics assets took 56 s as separate runs and 9 s this way.
- An unknown or misspelled option (for example `--aes-key` instead of `--aes`) is now an error with a suggestion. It used to be silently ignored.
- `set` can set a field that sits at its default, such as a physics node's gravity vector or wind switch. Such fields aren't stored in the file, so `set` used to report them missing; it now adds them first. Misspelled field names are still refused.
- Vector, rotator and quaternion values show as `x,y,z` (or `pitch,yaw,roll`) and can be set the same way; they used to show blank.

### Changed
- Every command starts faster, because loading the usmap got quicker (a small edit run went from about 2.1 s to 1.35 s).
- A `script` checks every ops line before opening the asset, and a refused `splice-node` is reported as skipped instead of ending the run.
- Editing is faster: a `set` or other path lookup follows the path instead of scanning the whole asset. 300 edits in one `script` call on a large physics asset went from about 2.8 s to no measurable cost.
- `splice-node` (command and script op) adds a working copy of an animation node, such as a KawaiiPhysics node, wired into the pose chain right after the original, with its node-table row. It refuses, changing nothing, when the wiring is ambiguous.
- `dump`, `search` and `set` handle 8/16-bit and unsigned integers, such as an animation node's data entries, which used to show as blank.

### Fixed
- `add-node` now saves. Adding a physics node used to switch the file to a format it could not write, so every save failed; the file keeps its normal format and the new node is written like any other.
- A cancelled conversion no longer sometimes produces output anyway.

## [1.3.0] - 2026-09-19

### Added
- Games whose paks use Unreal's older compression flags - Days Gone among them - can now be opened at all. Their assets are Oodle-compressed, which was being misread as a different codec entirely, so every read failed or produced nothing; Oodle is now supported and those assets extract and parse normally.
- `duplicate-export` (command and script op) deep-clones a whole export - a top-level object with its own identity, like a PhysicsAsset's body or constraint - and can optionally add a reference to the clone into an existing list, the counterpart to `append-clone` for references rather than values.
- `unpack` accepts `--filter` to extract only entries whose path contains the given text. Pulling a single asset out of a large game pak previously extracted the entire archive.
- `--strict` opens an asset without the usual fallback, so a parse failure reports its real cause instead of being silently worked around.

### Fixed
- Reading the same asset or mappings file from several processes at once could fail, and did so by returning nothing rather than reporting an error - so a batch run could report assets as empty when it had actually failed to read them.
- A rebuilt copy of the bundled native pak library was ignored once an older copy had been unpacked, so fixes to it appeared to have no effect.
- Failures inside the pak library were reported as a bare "no result", making an unsupported compression method indistinguishable from a missing file.
- An asset that failed to parse looked identical to one with no properties: no warning, no error, just empty exports. This now says what went wrong, and names a missing or mismatched mappings file or engine version as the usual cause.
- `IntPoint` and plain byte properties read as blank and could not be set; both are now supported, alongside the existing handling for enum-backed bytes.
- Cloning a numbered node could produce a name the save step couldn't match back to the property it declared.

## [1.2.0] - 2026-09-17

### Added
- Object/soft-object reference properties (e.g. a material's texture parameter) can now be set directly to another asset by full path (`/Game/Path/Asset.Asset`), resolved against the file's own existing import table - previously only scalars, strings, names, text and enums were settable.

### Fixed
- Vendored retoc (the underlying IoStore/Zen &lt;-&gt; legacy pak converter) picks up two real upstream fixes: a dependency-bundle bug that silently dropped a genuine cross-export preload dependency when rebuilding a package from legacy format, and completely missing write-side AES encryption support for the small companion `.pak` file some containers require alongside their `.utoc`/`.ucas`.

## [1.1.2] - 2026-09-16

### Fixed
- Some assets using Unreal's `FInstancedStruct` type anywhere in their data failed to parse at all and showed up as raw, unreadable bytes instead of editable properties - fixed by vendoring the underlying parsing library from its latest source instead of an older packaged version that predates its fix for this.
- `append-clone` (CLI/scripting) could only add a property missing from an array, not one missing from a struct or from an export's own top-level property list - it now handles both.

## [1.1.1] - 2026-09-16

### Fixed
- Launching the app while another process (e.g. a second instance) held the log file open crashed the entire app with an unhandled exception on the background logging thread, instead of just dropping that log line.

## [1.1.0] - 2026-09-16

### Added
- A new command-line tool (`UAssetEditor.Cli`) for scripting property edits and pak/IoStore operations without the GUI - useful for batch-editing many files at once.
- Browse tree: right-click "Duplicate Array Element" and "Remove Array Element" on any array element, and "Add AnimGraphNode..." to clone a numbered class member as a genuinely new one.
- Browse tree: "Expand All"/"Collapse All" buttons, and a "name contains" filter that can select every matching item at once.
- Convert IoStore to Legacy: a "layer" picker (original/modded) for resolving a mod's own overridden entries either as its real edits or as the base game's vanilla content.
- Long single-child folder chains in the Browse tree now compact into one row (matches VS Code's "compact folders").

### Fixed
- Resolving a mod's own IoStore overrides through its enclosing Paks folder could silently return the base game's vanilla content instead of the mod's real edits.
- A property or class member with a numbered name (e.g. a second "Foo_1") could display identically to its sibling in the Browse tree and search results, making them indistinguishable.

## [1.0.3] - 2026-09-15

### Fixed
- "Convert Selected..." on a browsed IoStore container always failed with a "does not contain ... ScriptObjects" error for any container that doesn't carry the game's global script-objects chunk (true of most single-mod `.utoc` files) - `to-legacy` no longer tries to extract them, since nothing in this app reads that output anyway.
- "Convert IoStore to Legacy...", "Unpack .pak..." and "Pack Folder into .pak..." (Tools menu) were grayed out while browsing a raw IoStore container, even though each is a standalone dialog that browses for its own source and never needed a workspace open at all.
- "Repack Loaded .pak..." (Tools menu) stayed enabled with no pak loaded (or a non-pak workspace open), only failing with a status message after being clicked, instead of graying out like "Repack to IoStore..." already correctly does.
- "Convert IoStore to Legacy..." always opened with an empty source field even while already browsing a container, unlike "Unpack .pak..."/"Pack Folder..." which do pre-fill from the current context - it now pre-fills with the currently-browsed container's path too.
- Converting an IoStore container could silently produce an empty output folder with no error at all: retoc logs a per-asset conversion failure and keeps going rather than failing outright, so "0 of N assets converted" looked exactly like success. This is now treated as a real failure, with retoc's own reasons carried through to the log.
- A single-mod `.utoc` that only overrides a few of a base game's assets always failed every one of them - it can't resolve imports into whatever container actually owns the rest on its own. "Convert Selected..." and the standalone "Convert IoStore to Legacy..." now resolve such a container against its enclosing `Paks` folder (found automatically) for that wider context, while keeping the actual conversion scoped to exactly the requested entries.

## [1.0.2] - 2026-09-15

### Fixed
- Opening an IoStore container with an AES key (list, or converting to/from legacy) always failed with "unexpected argument '--aes-key' found": retoc treats `--aes-key` as a global option, valid only before the subcommand, but it was being appended after instead.
- A failed retoc call showed its entire command line and stderr dump (often several paragraphs) as the app's status message instead of a short summary; the full detail still goes to the log file, but the UI now shows just retoc's own first error line.

## [1.0.1] - 2026-09-12

### Fixed
- "Detect Mount Point..." in the Pack Folder dialog blocked the UI thread while reading an existing pak's header (spawning/reconnecting to the pak worker process synchronously); it now runs off the UI thread like every other pak operation.
- Canceling an IoStore convert/pack (retoc) left the underlying `retoc.exe` process running in the background instead of stopping it, letting it keep writing to the output the app had just reported as canceled.

## [1.0.0] - 2026-09-03

### Added
- Pack Folder can now pack a loose folder straight into IoStore (`.utoc`/`.ucas`) format, not just a legacy `.pak`.
- Repack to IoStore now works from an open legacy `.pak`, not just a loose-folder workspace.
- A standalone "Convert IoStore to Legacy..." dialog (Tools menu), converting a `.utoc` container to either a loose folder or a `.pak`.
- `.github/dependabot.yml` for NuGet dependency updates.
- CI: every push/PR builds, tests, and runs analyzers.
- `UAssetEditor.App.Tests`: a test project for the app layer, covering session save/load schema versioning and the dialog ViewModels' pure logic.

### Fixed
- A real retoc `to-zen` failure ("Failed to get Package Path from Content/...") for UE4.27-era containers, caused by retoc needing a project-name folder segment in the walked path.
- The results grid's columns reflowing (and visibly scroll-jumping) on every value edit.
- `EmbeddedToolLocator` re-reading and SHA-256-hashing the embedded retoc/worker payload on every call instead of only once.
- Unhandled exceptions and unobserved task failures now get logged to a persistent file instead of vanishing when the window closes.
- A path-traversal vulnerability in `PakUnpacker`: a crafted pak entry path could write outside the chosen destination folder.
- The saved session config had no schema version and wrote non-atomically (a crash mid-save could corrupt it); both fixed.

### Changed
- Test suite migrated from xUnit v2 + VSTest to xUnit v3 on Microsoft.Testing.Platform.
- Release builds now treat warnings as errors.

[Unreleased]: https://github.com/gravenuance/UAssetEditor/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/gravenuance/UAssetEditor/compare/v1.1.2...v1.2.0
[1.0.3]: https://github.com/gravenuance/UAssetEditor/compare/v1.0.2...v1.0.3
[1.0.2]: https://github.com/gravenuance/UAssetEditor/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/gravenuance/UAssetEditor/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/gravenuance/UAssetEditor/releases/tag/v1.0.0
