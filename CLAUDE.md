# Working in this repo

`uacli` (`src/UAssetEditor.Cli`) exists so an agent can read and edit Unreal assets without
a GUI. It is built for programmatic use: no interactive prompts, no confirmation gates,
line-oriented stdout, and a dry-run on every mutating command. Optimise for throughput, not
for what a human could comfortably type.

Everything below is a lesson something already cost. Re-deriving them is expensive.

## The one rule that matters: batch, don't loop

**Never call the CLI once per edit.** Each invocation starts a .NET process, re-reads the
`.usmap`, and re-parses the asset. A per-edit loop over a few hundred assets takes hours;
the same work batched takes seconds. A real measured case in this repo: 233 files, ~36
invocations each → **~2 hours**. Rewritten as one `script` call per file, run 10-way
parallel → **19 seconds**.

**Better still: one process for the whole job.** `script --plan` runs every asset in one
process, in parallel, loading the usmap once (loading it is ~1.5 s of every small run).
Measured on 138 physics assets, open + save: one process per file with `xargs -P 10` 56 s;
one `--plan` run **9 s**, byte-identical output.

```bash
# NO - one process per edit
for bone in a b c; do uacli set "$f" --path "...$bone..." --value 0.04 --save; done

# OK - one process per file
uacli script "$f" --version VER_UE5_3 --usmap "$MAP" --ops ops.txt --save

# BEST - one process for every file. plan.tsv lines: <asset><TAB><opsfile>, Windows paths (D:/...)
uacli script --plan plan.tsv --version VER_UE5_3 --usmap "$MAP" --save
```

`script` accepts `set`, `duplicate`, `remove`, `add-node`, `splice-node`, `append-clone`,
`duplicate-export`, one per line, each with its own `--export`. So a single call can edit
several exports of the same asset. Every line is validated before anything opens: an unknown
op or `--option` stops the run.

`set` creates a field that is absent because it sits at its default (unversioned data omits
those, e.g. a node's `Gravity` vector), using the usmap schema. Vectors are `x,y,z`.

To add a physics node, use `splice-node --export 3 --template <node>`: it clones the node,
its AnimNodeData row, and rewires the pose chain (template → copy → former reader) in one op.

**`duplicate` appends at a predictable index, and later lines in the same ops file can use
it.** If the array's highest index is `N`, the clone is `N+1`. That means an
add-then-configure sequence needs no round-trip to discover the new index — compute it from
cached data and emit the whole block up front. Verify the array's real length first: the
template you clone is not necessarily the last element.

## Shape of an efficient bulk job

1. **Discover once, cache to disk.** One `search` per file into a cache directory. All
   subsequent classification and planning is local `grep` over that cache — zero further CLI
   calls. 233 files cache in ~33 s.
2. **Plan offline.** Generate one ops file per asset from the cache.
3. **Dry-run everything.** `script` without `--save` is a complete preview, printing every
   `old -> new`. It is free and it catches path and index mistakes before they touch disk.
4. **Apply in one `script --plan` run** (parallel inside the process).
5. **Verify by re-reading from disk.** Never conclude from the run log alone.

## Parallelism

Safe now, but it was not always: `.uasset`, `.uexp` and `.usmap` were being opened with
`FileShare.None`, so concurrent readers intermittently failed with
`IOException: ... used by another process`. Fixed in `vendor/UAssetAPI` (`UAsset.PathToStream`,
`Usmap.PathToStream`). If a parallel run ever shows *empty output that looks like missing
data rather than an error*, suspect file sharing first — that failure mode is silent and
was mistaken for a data problem once already.

`--plan` defaults to one worker per CPU (`--jobs` to change). The CLI runs with server GC:
with the default workstation GC, parallel parsing barely beat one thread (24 s vs 29 s).

## Always scope pak operations

`unpack` without `--filter` extracts the entire archive. Real game paks are tens of GB and
hundreds of thousands of entries — Days Gone's is 30 GB / 216,747 entries. Pass `--filter`
whenever you want specific assets; it is the difference between 3 seconds and 10 minutes
plus a filled disk.

```bash
uacli pak-list <pak> --filter Thing     # find the exact entry path first
uacli unpack  <pak> <dir> --filter Thing
```

## Game-specific flags are not optional

`--version` defaults to `VER_UE4_27` and is almost never right. `--usmap` is mandatory for
any game using unversioned properties (Marvel Rivals, and most modern UE5 titles); without
it assets appear to open and then produce garbage. Get both right before concluding an
asset is corrupt.

## Reading structure without parsing

A `.uasset`'s name table is plain text in the file. To answer "does this skeleton have bone
X", grep the raw bytes — no parse, no usmap, instant:

```bash
grep -aoiP '[A-Za-z_0-9]*breast[A-Za-z_0-9]*' SK_Foo_Skeleton.uasset | sort -u
```

This is the only practical way to read a skeleton's bone list: `FReferenceSkeleton` is
custom-serialised native binary and invisible to property reflection.

## Things that have caused wrong conclusions

- **A bone name appearing in a file does not mean it is simulated.**
  `AnimGraphNode_Constraint.ConstraintSetup[].TargetBone` matches the same grep as a jiggle
  chain but is not one. Counting those inflated a roster count by 18 files.
- **Two different node shapes exist.** `AnimGraphNode_KawaiiPhysics` uses
  `Chains[N].BoneSettings.RootBone` with a doubled `PhysicsSettings.PhysicsSettings`;
  `AnimGraphNode_KawaiiPhysicsMerge` uses a flat `Nodes[N].RootBone` and a single
  `PhysicsSettings`. A pattern matching only the first silently misses the second — that
  produced duplicate chains on an asset that already had them.
- **Properties are not all on export 3.** They are commonly on 3 *or* 4. Dumping only one
  reports real values as missing.
- **Copy the `.uexp` with the `.uasset`.** A lone `.uasset` opens "successfully" and then
  yields garbage.
- **Back up before editing in place.** `--backup` covers both files; for a bulk run copy the
  whole tree first.

## Legacy UE4 pak compression (< UE 4.22)

`FPakEntry.CompressionMethod` is an `ECompressionFlags` **bitmask**, not an index into a
codec table: `0x01 = ZLIB`, `0x02 = GZIP`, `0x04 = COMPRESS_Custom`. Treating it as an index
is a category error — it happens to work for ZLIB and GZIP and breaks on everything else.
Confirmed against Days Gone's real pak: the only values that occur are `0` and `4`.

`COMPRESS_Custom` is Oodle in practice. Days Gone links Oodle statically, which is why the
game ships no `oo2core` DLL; `repak_bind` loads `oo2core_9_win64.dll` from beside the host
executable (override with `REPAK_OODLE_DLL`), and the PakWorker project copies it to its own
output because pak reads happen in that process.

CUE4Parse/FModel get this wrong too — their legacy table is indexed directly and lands on
LZ4 — which is why FModel can list a Days Gone pak but not read its contents.

## Native library

`repak_bind.dll` ships gzipped inside `UAssetAPI` and is unpacked next to the host
executable. It is now refreshed when the embedded copy differs in size. Before that fix a
rebuilt native library was silently ignored forever, which burned a lot of time across
sessions — if native behaviour ever seems not to match the source, verify the loaded file's
hash before theorising.

## Checks before committing

Release build (warnings are errors) and the real test invocation:

```bash
dotnet build -c Release
dotnet run --project tests/UAssetEditor.Core.Tests -c Release
```
