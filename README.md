# UAssetEditor

An editor for .pak and .uasset files.

![UI](/eg.png)

## Build

Requires the .NET 10 SDK (pinned in `global.json`).

```
dotnet build UAssetEditor.slnx
```

Run the test suite (Release matches what CI runs):

```
dotnet test tests/UAssetEditor.Core.Tests/UAssetEditor.Core.Tests.csproj -c Release
```

## Run

For development, run straight from source:

```
dotnet run --project src/UAssetEditor.App
```

For a standalone build, publish the single-file executable:

```
dotnet publish src/UAssetEditor.App/UAssetEditor.App.csproj -p:PublishProfile=SingleFileRelease
```

The result is a single self-contained `UAssetEditor.App.exe` under `src/UAssetEditor.App/bin/Release/net10.0-windows/publish/win-x64/` - no separate .NET runtime install needed on the target machine.

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the non-obvious design decisions behind the pak worker process, retoc integration, and a few other things that aren't visible just from reading the code top to bottom.

## How this is different from UAssetGUI

UAssetGUI opens one .uasset at a time and you edit it by hand in a property tree. UAssetEditor is built around doing the same edit to a lot of assets at once, and working straight from the archive instead of unpacking everything first.

- **Search across a whole pak or folder, not one file.** Filter by export name, property name, value, or reference, and see every match from every asset in one results grid, editable right there.
- **Turn a search into a repeatable rule.** Set a value, adjust a number, find/replace text (regex supported), add or remove a tag, remove a property, or swap a reference, then preview the change and apply it across every matching asset at once instead of doing it by hand one by one.
- **Browse and act on pak/.utoc archives directly.** Check the files you want in the tree, then load, extract, or repack just those, without unpacking the whole archive to disk with a separate tool first. Large archives are handled a piece at a time so browsing a multi-gigabyte pak doesn't mean loading the whole thing into memory.
- **Handles encrypted paks and IoStore containers.** There's a spot for an AES key for encrypted paks, and .utoc/.ucas (IoStore) files are supported through a bundled converter.
- **Doesn't give up on an asset just because part of it is broken.** If something in an asset fails to parse, UAssetEditor keeps what it can read (header, names, other exports) instead of refusing to open the file at all. Running a batch edit over many assets also tells you which ones failed instead of silently skipping them or aborting the whole run.
- **A crash in the pak reader doesn't take the app down with it.** Pak reading and writing runs in its own worker process, so a bad entry can fail without losing your session.
- **Never touches your original file.** Edits are written out to a new pak, so there's nothing to undo if a batch edit goes wrong.
- **Pack and unpack pak files from the same window**, no separate command-line tool needed.
