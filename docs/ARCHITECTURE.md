# Architecture notes

Non-obvious decisions that shape this codebase, recorded here so the reasoning survives past the conversation/PR that made the call. This isn't a full architecture guide - the code's own doc comments cover most local detail; this is for decisions that span files or that a new contributor would otherwise have to reverse-engineer from git history.

## Pak reading/writing runs in a separate worker process

All calls into UAssetAPI's embedded native `repak_bind.dll` go through `UAssetEditor.PakWorker`, a separate process talked to over a named pipe (`PakWorkerProcess`, `PakWorkerClient`, `PakReaderHandle`/`PakWriterHandle`). There's a confirmed real-world native crash in that library (`STATUS_STACK_BUFFER_OVERRUN`) on certain entries; running it out-of-process means a crash there kills only the worker, which is transparently respawned, instead of taking the whole editor down mid-edit. The worker is embedded as a resource in the single-file publish and extracted to LocalAppData on first use (see `EmbeddedToolLocator`).

## retoc needs a project-name folder segment for older containers

retoc's `to-zen` conversion (legacy → IoStore) derives each asset's package path by splitting on a fixed `"../../../"` prefix and expecting the very next path segment to be the project name (e.g. `SB` in `../../../SB/Content/...`). This only matters for UE4.27-and-earlier containers (`EIoContainerHeaderVersion::Initial`); confirmed against retoc's own source and reproduced against the real vendored `retoc.exe`.

Two consequences this codebase works around (`RetocDirectoryInputResolver`):
- A folder the user points at directly (e.g. `...\SB`, already the project root) has to be handed to retoc as its *parent* instead, so retoc's own directory walk reproduces the `SB/...` segment.
- A pak's entries are stored relative to *its own* mount point, not wrapped in a project-name folder - so a pak-backed "Repack to IoStore" first extracts to a temp folder named from the pak's real mount point, rather than handing the `.pak` to retoc directly (which does technically accept a `.pak` as input, but hits the same missing-segment problem).

## Buffer pooling on the hot pak-entry path

Every pak entry read used to allocate a fresh `byte[]`; large entries (textures, audio) landed on the Large Object Heap on every open/extract/repack, fragmenting it under load. `RentedBuffer` rents from `ArrayPool<byte>.Shared` end-to-end (worker IPC → reader → repacker/unpacker) instead.

## Most ViewModels are constructed with `new`, not through DI

Only `MainViewModel`/`MainWindow` go through the `ServiceCollection` in `App.xaml.cs` - the various dialog ViewModels (`PackFolderViewModel`, `UnpackPakViewModel`, `ConvertIoStoreToLegacyViewModel`, ...) are constructed directly with `new` where they're opened, since they're short-lived and need per-invocation constructor arguments (a pre-filled path, an AES key) that don't fit a container-managed lifetime well. `AppLog.For<T>()` gives these an ambient logger without threading an `ILoggerFactory` through every one of those constructors for a cross-cutting concern.

## Known gaps

- `UAssetEditor.App` has no test project - only `UAssetEditor.Core` (and by extension `UAssetEditor.Core.Tests`) is unit-tested. ViewModel logic (session save/load, command wiring) currently has no automated coverage.
- Logging (`ILogger` via `AppLog`) is wired into the process-wide crash handlers and a handful of ViewModel error handlers, not yet every catch block in `MainViewModel`.
