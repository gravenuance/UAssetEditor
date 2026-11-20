# 0001: Oodle comes from the user's game install, not from us

**Status:** Accepted · **Date:** 2026-11-18

## Context

Legacy Unreal 4 paks mark some entries `COMPRESS_Custom` (flag `0x04`), which in practice is Oodle; Days Gone's 30 GB pak is entirely uncompressed-or-Oodle. repak (through `repak_bind.dll`) reads them by loading `OodleLZ_Decompress` from an `oo2core_<n>_win64.dll`. The pak is read in the separate pak worker process, and the published app and MCP server contain no such DLL, so every Oodle entry failed with "pointer to OodleLZ_Decompress was not provided".

`oo2core` is Epic Games Tools' (RAD's) proprietary library. This repository and its releases are public. Neither Days Gone nor Marvel Rivals ships the DLL (checked on a Steam install, 2026-11-18): both link Oodle into their executables. Other Unreal games do ship it, for example Final Fantasy VII Remake in `Engine/Binaries/ThirdParty/Oodle/Win64/oo2core_7_win64.dll`.

Two properties of `repak_bind` shape the design. It reads `REPAK_OODLE_DLL` when Oodle is first needed and keeps a successful load for the life of the process (a failed one is not kept, so the next read tries the variable again). And it loads only that absolute path: it searches neither the worker's folder, the working folder nor `PATH`. The worker is one long-lived process shared by every open pak. (Before the unified `repak_bind` of 2026-11-19 it kept a failed lookup too and fell back to `oo2core_9_win64.dll` by bare name.)

## Options

- **Embed `oo2core_9_win64.dll` in the App and Mcp executables** (commit `27d5928` on the dependency branch). Works offline with no setup; but it distributes a proprietary binary in a public repository and in every release.
- **Download it on first use** (what the `oodle_loader` crate does). No redistribution in the repo; but it adds a network dependency and a third-party host to trust, and pins one exact build.
- **Use the user's own copy**: `REPAK_OODLE_DLL`, else a DLL found in the install of the game the pak is in. Nothing proprietary is distributed; but a game that ships no DLL needs the user to point at one from another game.

## Decision

Use the user's own copy (`OodleLibrary` in Core, driven by `PakReaderHandle` and `PakWorkerProcess`).

- **Lookup.** The `REPAK_OODLE_DLL` environment variable: a fully qualified path to an existing file named `oo2core_<n>_win64.dll`; an invalid value is an error, not a reason to guess. Otherwise a search from the pak's folder upward, at most five parents, for the game's root: the first folder with an `Engine` folder or a `.uproject`, or whose child on the way down to the pak is a project folder with both `Content` and `Binaries/Win64`. A drive or share root is never a root. The search stops at that root and looks only inside it (the root itself, `Engine/Binaries/ThirdParty/Oodle/**` three levels deep, `<project>/Binaries/Win64`), so it never lists a folder above the root, and the highest version wins. `PATH` and the working folder are never searched. A search that fails finds nothing.
- **The worker's environment is decided by the host, at spawn.** `PakWorkerProcess` removes `REPAK_OODLE_DLL` from the worker's environment and sets it only to a validated absolute path, so a rejected or relative value is never inherited. A worker's Oodle path is fixed for its life. The default worker (`PakWorkerProcess.Shared`) starts with the validated environment variable or none.
- **A DLL found next to a pak gets a worker of its own.** Most paks never need Oodle, so nothing is searched when a pak opens. When a read fails with repak's Oodle error, `PakReaderHandle` searches near the pak (once per reader; a found DLL is remembered per pak folder), asks `PakWorkerProcess.ForOodleLibrary` for the worker started with that path (started on first use and kept for the session), closes its session on the old worker, reopens its pak on that one and reads once more. Only that reader moves: no worker is ever killed or replaced while another reader or writer has a session in it, so the repack path, other readers and in-flight requests are untouched. Workers are keyed by Oodle path, which also means "Could not load X" is only said after a worker started with X tried it.
- **The search looks only at folders on the pak's path.** Whether a folder is the root is decided from that folder and the one below it on the way down to the pak, never from listing a folder's other children; only the root itself is listed.
- **The error is explained.** When the read still fails, the message says what is missing and how to fix it, identically in the app, the CLI and the MCP tool result.
- The proprietary file is not embedded, and `.gitignore` excludes `oo2core_*_win64.dll` so a local copy cannot be committed.

Reason: the library is not ours to redistribute, and the repository and releases are public.

The app has no settings store for this: `lastSession.json` is per-session UI state owned by the App project, and the CLI and MCP server do not read it, so a stored path would have been a second mechanism. The environment variable works for all three.

## Consequences

- Reading Days Gone or Marvel Rivals Oodle entries needs a DLL from some other game or tool the user owns; the README and the error message say so.
- Auto-detect loads and runs code from the game's folder, which is the same trust as running that game; the project accepts this. A pak opened from a folder laid out like a game (a project folder with `Content` and `Binaries/Win64`, or an `Engine` folder) can carry a planted `oo2core_*_win64.dll`; the name and file checks do not prove it is genuine. Because the search stops at the first root, a DLL in an unrelated folder above or beside the install is not reachable.
- `repak_bind` loads Oodle only from the absolute path in `REPAK_OODLE_DLL`, which this app validates, so a DLL planted beside the worker or on `PATH` is never loaded. `PakNativeLibraryTests` checks that the embedded `repak_bind.dll` names no `oo2core` file to search for, so a re-vendored older build is caught.
- A DLL beside a pak that is not inside a recognisable game root (a loose pak in Downloads with `oo2core_*_win64.dll` next to it) is deliberately not found; point `REPAK_OODLE_DLL` at it instead.
- Each distinct DLL path costs one more worker process for the session, started the first time a pak needs it. A worker is never replaced for another reader's sake, but a reader opened on a worker that crashed reopens its pak on the next read.
- The worker's crash and state entries used by the tests answer only a worker started with `UASSETEDITOR_PAKWORKER_TEST_HOOKS=1`, which only the test process sets.
- A copy of the DLL has been in this repository's public history since commit `84e788b` (2026-09-16), well before `27d5928`; deleting it from the tree does not remove it from old commits or clones. Whether to rewrite history is a separate decision.
- Revisit if RAD licenses redistribution, or if a settings store shared by the app, CLI and MCP server appears.
