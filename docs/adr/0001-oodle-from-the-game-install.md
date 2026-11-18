# 0001: Oodle comes from the user's game install, not from us

**Status:** Accepted · **Date:** 2026-11-18

## Context

Legacy Unreal 4 paks mark some entries `COMPRESS_Custom` (flag `0x04`), which in practice is Oodle; Days Gone's 30 GB pak is entirely uncompressed-or-Oodle. repak (through `repak_bind.dll`) reads them by loading `OodleLZ_Decompress` from an `oo2core_<n>_win64.dll`. The pak is read in the separate pak worker process, and the published app and MCP server contain no such DLL, so every Oodle entry failed with "pointer to OodleLZ_Decompress was not provided".

`oo2core` is Epic Games Tools' (RAD's) proprietary library. This repository and its releases are public. Neither Days Gone nor Marvel Rivals ships the DLL (checked on a Steam install, 2026-11-18): both link Oodle into their executables. Other Unreal games do ship it, for example Final Fantasy VII Remake in `Engine/Binaries/ThirdParty/Oodle/Win64/oo2core_7_win64.dll`.

Two properties of `repak_bind` shape the design. It reads `REPAK_OODLE_DLL` once, when Oodle is first needed, and keeps that result (a failed lookup too) for the life of the process. And when the variable's path does not load it falls back to `oo2core_9_win64.dll` by bare name (the worker's folder, the working folder, `PATH`). The worker is one long-lived process shared by every open pak.

## Options

- **Embed `oo2core_9_win64.dll` in the App and Mcp executables** (commit `27d5928` on the dependency branch). Works offline with no setup; but it distributes a proprietary binary in a public repository and in every release.
- **Download it on first use** (what the `oodle_loader` crate does). No redistribution in the repo; but it adds a network dependency and a third-party host to trust, and pins one exact build.
- **Use the user's own copy**: `REPAK_OODLE_DLL`, else a DLL found in the install of the game the pak is in. Nothing proprietary is distributed; but a game that ships no DLL needs the user to point at one from another game.

## Decision

Use the user's own copy (`OodleLibrary` in Core, driven by `PakReaderHandle` and `PakWorkerProcess`).

- **Lookup.** The `REPAK_OODLE_DLL` environment variable: a fully qualified path to an existing file named `oo2core_<n>_win64.dll`; an invalid value is an error, not a reason to guess. Otherwise a search from the pak's folder upward, at most five parents, for the game's root: the first folder with an `Engine` folder or a `.uproject`, or holding a project folder with both `Content` and `Binaries/Win64`. A drive or share root is never a root. The search stops at that root and looks only inside it (the root itself, `Engine/Binaries/ThirdParty/Oodle/**` three levels deep, `<project>/Binaries/Win64`), so it never lists a folder above the root, and the highest version wins. `PATH` and the working folder are never searched. A search that fails finds nothing.
- **The worker's environment is decided by the host, at spawn.** `PakWorkerProcess` removes `REPAK_OODLE_DLL` from the worker's environment and sets it only to a validated absolute path, so a rejected or relative value is never inherited. A worker starts with the path from the environment variable, if valid.
- **The search for a game's DLL is lazy.** Most paks never need Oodle, so nothing is searched when a pak opens. When a read fails with repak's Oodle error, `PakReaderHandle` searches near the pak. A DLL the worker does not already hold goes to a replacement worker (`PakWorkerProcess.UseOodleLibraryAsync`), because the failed worker has cached its failure; the reader reopens its pak there and reads once more. A worker is thus keyed by its Oodle path and replaced after a failure, never given a second path in place. Every worker carries a generation number, and a reader whose pak was opened on an earlier worker reopens it before its next read.
- **The error is explained.** When the read still fails, the message says what is missing and how to fix it, identically in the app, the CLI and the MCP tool result.
- The proprietary file is not embedded, and `.gitignore` excludes `oo2core_*_win64.dll` so a local copy cannot be committed.

Reason: the library is not ours to redistribute, and the repository and releases are public.

The app has no settings store for this: `lastSession.json` is per-session UI state owned by the App project, and the CLI and MCP server do not read it, so a stored path would have been a second mechanism. The environment variable works for all three.

## Consequences

- Reading Days Gone or Marvel Rivals Oodle entries needs a DLL from some other game or tool the user owns; the README and the error message say so.
- Auto-detect loads and runs code from the game's folder, which is the same trust as running that game; the project accepts this. A pak opened from a folder laid out like a game (a project folder with `Content` and `Binaries/Win64`, or an `Engine` folder) can carry a planted `oo2core_*_win64.dll`; the name and file checks do not prove it is genuine. Because the search stops at the first root, a DLL in an unrelated folder above or beside the install is not reachable.
- `repak_bind` itself still falls back to `oo2core_9_win64.dll` beside the worker and in the working folder (then `PATH`) when the path it was given will not load. This app puts no file in either place, but the fallback is the remaining way a planted DLL could load; it is tracked in `TODO.md` for repak-cs-bindings.
- Replacing the worker costs a process start the first time a game's DLL is needed, and a read that spans two games' DLLs in one session restarts the worker once per change.
- A copy of the DLL has been in this repository's public history since commit `84e788b` (2026-09-16), well before `27d5928`; deleting it from the tree does not remove it from old commits or clones. Whether to rewrite history is a separate decision.
- Revisit if RAD licenses redistribution, or if a settings store shared by the app, CLI and MCP server appears.
