# 0001: Oodle comes from the user's game install, not from us

**Status:** Accepted · **Date:** 2026-11-18

## Context

Legacy Unreal 4 paks mark some entries `COMPRESS_Custom` (flag `0x04`), which in practice is Oodle; Days Gone's 30 GB pak is entirely uncompressed-or-Oodle. repak (through `repak_bind.dll`) reads them by loading `OodleLZ_Decompress` from an `oo2core_<n>_win64.dll`. The pak is read in the separate pak worker process, and the published app and MCP server contain no such DLL, so every Oodle entry failed with "pointer to OodleLZ_Decompress was not provided".

`oo2core` is Epic Games Tools' (RAD's) proprietary library. This repository and its releases are public. Neither Days Gone nor Marvel Rivals ships the DLL (checked on a Steam install, 2026-11-18): both link Oodle into their executables. Other Unreal games do ship it, for example Final Fantasy VII Remake in `Engine/Binaries/ThirdParty/Oodle/Win64/oo2core_7_win64.dll`.

## Options

- **Embed `oo2core_9_win64.dll` in the App and Mcp executables** (commit `27d5928` on the dependency branch). Works offline with no setup; but it distributes a proprietary binary in a public repository and in every release.
- **Download it on first use** (what the `oodle_loader` crate does). No redistribution in the repo; but it adds a network dependency and a third-party host to trust, and pins one exact build.
- **Use the user's own copy**: `REPAK_OODLE_DLL`, else a DLL found in the install of the game the pak sits in. Nothing proprietary is distributed; but a game that ships no DLL needs the user to point at one from another game.

## Decision

Use the user's own copy (`OodleLibrary` in Core, called by `PakReaderHandle`). Resolution order: the `REPAK_OODLE_DLL` environment variable (a fully qualified path to an existing file named `oo2core_<n>_win64.dll`; an invalid value is an error, not a reason to guess); else a search upward from the pak's folder, at most five parents, in the game root, `Engine/Binaries/ThirdParty/Oodle/**` (three levels deep) and `<project>/Binaries/Win64`, where the nearest install with a match wins and within it the highest version. Neither `PATH` nor the working folder is searched. The path travels to the worker in the `OpenReader` request, and the worker sets `REPAK_OODLE_DLL` before repak first needs Oodle. When an Oodle entry cannot be read, the error says what is missing and how to fix it, identically in the app, the CLI and the MCP tool result. The proprietary file is not embedded, and `.gitignore` excludes `oo2core_*_win64.dll` so a local copy cannot be committed.

Reason: the library is not ours to redistribute, and the repository and releases are public.

The app has no settings store for this: `lastSession.json` is per-session UI state owned by the App project, and the CLI and MCP server do not read it, so a stored path would have been a second mechanism. The environment variable works for all three.

## Consequences

- Reading Days Gone or Marvel Rivals Oodle entries needs a DLL from some other game or tool the user owns; the README and the error message say so.
- Auto-detect loads and runs code from the folder next to the pak, which is the same trust as running that game. A pak opened from an untrusted download folder could sit beside a planted `oo2core_*_win64.dll`; the name, file and fully-qualified-path checks do not prove the DLL is genuine.
- `repak_bind` keeps the first Oodle lookup for the worker's lifetime, including a failed one. A worker that already hit "not found" does not pick up a DLL found for a later pak; restart the app.
- `repak_bind` also falls back to `oo2core_9_win64.dll` beside the worker and in the working folder when the configured path will not load; ours never puts a file there, but a change in that library to drop the fallback would close the working-folder case.
- The earlier commit that embedded the DLL stays in history; whether to rewrite history is a separate decision.
- Revisit if RAD licenses redistribution, or if a settings store shared by the app, CLI and MCP server appears.
