using UAssetEditor.Cli;
using UAssetEditor.Core.AssetSources.PakWorker;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return args.Length == 0 ? 1 : 0;
}

var verb = args[0];
var reader = new ArgReader(args[1..]);

try
{
    return verb switch
    {
        "exports" => Commands.Exports(reader),
        "tree" => Commands.Tree(reader),
        "dump" => Commands.Dump(reader),
        "search" => Commands.Search(reader),
        "set" => Commands.Set(reader),
        "duplicate" => Commands.Duplicate(reader),
        "remove" => Commands.Remove(reader),
        "add-node" => Commands.AddNode(reader),
        "append-clone" => Commands.AppendClone(reader),
        "script" => Commands.Script(reader),
        "batch" => Commands.Batch(reader),
        "pak-info" => PakCommands.PakInfo(reader),
        "pak-list" => PakCommands.PakList(reader),
        "iostore-list" => PakCommands.IoStoreList(reader),
        "unpack" => PakCommands.Unpack(reader),
        "pack" => PakCommands.Pack(reader),
        "to-legacy" => PakCommands.ToLegacy(reader),
        "repack" => PakCommands.Repack(reader),
        _ => Unknown(verb),
    };
}
catch (ArgException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
finally
{
    // Every pak-* command above spawns the shared out-of-process pak worker on first use;
    // unlike the app (which disposes it once in App.OnExit), this process exits after one
    // command, so skipping this would leave that worker running as an orphaned process every
    // single invocation. Harmless no-op for a command that never touched it.
    PakWorkerProcess.Shared.Dispose();
}

static int Unknown(string verb)
{
    Console.Error.WriteLine($"Unknown command '{verb}'. Run with --help for usage.");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        uacli - read/edit .uasset files directly through UAssetEditor.Core, no GUI.

        Global options (any command that opens a file): --version <EngineVersion> (default VER_UE4_27), --usmap <path>.
        --export accepts either a 0-based index or a (sub)string match against the export's name.
        A property --path uses the same scheme the app's tree/search use: "Foo.Bar" for a struct field, "Tags[2]" for an array element.

          uacli exports <file>
              List every export: index, name, export type.

          uacli tree <file> --export <e> [--path <p>] [--depth <n>]
              Print the Browse-tree shape (struct/array/map nodes only) under an export, or under one property if --path is given.

          uacli dump <file> --export <e> [--path <p>] [--filter <substring>]
              Print every leaf property's value as "Path = Value", optionally scoped to --path and/or filtered by a path substring.

          uacli search <file-or-folder> [--export-name <t>] [--property-name <t>] [--value <t>] [--reference <t>] [--regex]
              Search one asset or every .uasset under a folder. Terms are substring matches unless --regex is given.

          uacli set <file> --export <e> --path <p> --value <v> [--save] [--backup]
              Set a scalar/string/name/text property's value.

          uacli duplicate <file> --export <e> --path <p> [--save] [--backup]
              Deep-clone the array element at --path, appended as the array's new last element.

          uacli remove <file> --export <e> --path <p> [--save] [--backup]
              Remove the array element at --path.

          uacli add-node <file> --export <e> --template <PropertyName> [--save] [--backup]
              Declare a brand-new top-level class property by cloning --template's declaration and CDO value (see ClassPropertyDeclarer).
              Does not touch AnimNodeData or ComponentPose.LinkID - splice those in by hand with 'duplicate'/'set' afterward.

          uacli append-clone <file> --export <e> --from <sourcePath> [--into <containerPath>] [--from-export <e>] [--save] [--backup]
              Deep-clone the property at --from and append it into --into, which may be an array (appended as the new last
              element - for one with no existing element of its own to 'duplicate' from, e.g. an empty ExcludeBones list) or a
              struct (appended as a new field - for a scalar field omitted because it sits at its engine default, e.g. a
              Kinematic body's unset GravityScale, which 'set' can't find until something adds it). Omit --into to append
              straight onto the export's own top-level property list instead, for when the whole property is missing (e.g. a
              rigid body whose every DefaultInstance field sat at default, omitting the whole struct, not just its fields).
              --from-export resolves --from against a different export than --into's (defaults to the same --export) -
              needed when the template lives on a different top-level object entirely, e.g. cloning one PhysicsAsset body's
              populated DefaultInstance field onto another body's.

          uacli script <file> --ops <opsfile> [--save] [--backup]
              Run several set/duplicate/remove/add-node/append-clone ops (one per line, same "--flag value" syntax minus the file/verb)
              against one asset opened once - much faster than one CLI invocation per edit for a multi-step workflow (e.g. splicing in a node).
              Blank lines and lines starting with '#' are skipped; a failing line is reported and skipped, the rest still run.
              Example opsfile line: set --export 3 --path Chains[0].PhysicsSettings.Damping --value 0.5

          uacli batch <file-or-folder> --ruleset <ruleset.json> [--apply] [--backup]
              Run a full RuleSet (the same JSON the app's batch-edit tab saves/loads: setValue/numericAdjust/replaceText/removeProperty/
              addTag/removeTag/replaceReference/duplicateElement rules over a search Scope) across one asset or every asset under a folder.
              Defaults to a dry-run preview; --apply actually writes.

        Pak/IoStore archive commands (mirror the app's Unpack/Pack/Convert dialogs and Repack). All take --aes <hex> for an encrypted pak.

          uacli pak-info <pak> [--aes <hex>]
              Print a .pak's mount point and format version.

          uacli pak-list <pak> [--aes <hex>] [--filter <substring>]
              List every entry in a .pak ('*' marks a .uasset).

          uacli unpack <pak> <destFolder> [--aes <hex>]
              Extract every entry of a .pak to a loose folder.

          uacli pack <sourceFolder> <output> [--mount <point>] [--pak-version <v>] [--compression <name>] [--version <EngineVersion>] [--aes <hex>]
              Build a new archive from a loose folder: a legacy .pak if <output> ends in ".pak" (--mount defaults to "../../../<folderName>/",
              --pak-version defaults to V11), or an IoStore .utoc/.ucas pair via retoc if it ends in ".utoc" (needs --version, a UE4.25+ engine version).

          uacli iostore-list <utoc> [--aes <hex>] [--filter <substring>]
              List every chunk path in a .utoc container via retoc. Use this to find the exact entry path 'to-legacy''s --filter expects.

          uacli to-legacy <utoc> <output> [--aes <hex>] [--filter <path1,path2,...>] [--layer original|modded] [--no-paks-resolve]
              Convert an IoStore .utoc to legacy format via retoc, into a loose folder or a .pak. Converts every entry by default, or
              only the comma-separated entry paths passed via --filter (e.g. to pull one specific asset out of a large shared chunk
              like pakchunkCharacter-Windows.utoc). Auto-resolves imports through the enclosing "Paks" folder when found (pass
              --no-paks-resolve to convert with no wider context). --layer picks which version of <utoc>'s own overridden entries
              that resolution returns: "modded" (default) keeps <utoc>'s own edits; "original" discards them for the base game's
              vanilla content instead - useful for diffing a mod against what it actually changed.

          uacli repack <pak> <output.pak> [--ruleset <ruleset.json>] [--aes <hex>]
              Rebuild a .pak, optionally applying a RuleSet's edits first (same JSON as 'batch') - the pak counterpart to editing a loose
              file in place. Always writes a new file; never overwrites the source pak.

        Nothing is written to disk unless --save/--apply is passed (or for a pak-* command, unconditionally, since packing/repacking/converting IS the write);
        --backup copies the original file first ("<file>.bak" for single-file commands).
        """);
}
