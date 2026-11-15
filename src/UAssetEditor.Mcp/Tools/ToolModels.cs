namespace UAssetEditor.Mcp.Tools;

// Result shapes of the tools. Lists are capped by the tool that fills them and say so in a Truncated flag or Note.
// Text read from files is passed through Untrusted.Clean first.

internal sealed record MemberDto(int Index, string Name, string? Type);

internal sealed record ExportDto(int Index, string Name, string Type, IReadOnlyList<MemberDto>? Members);

internal sealed record ExportsResult(string File, int Total, IReadOnlyList<ExportDto> Exports, bool Truncated, IReadOnlyList<string> Warnings);

internal sealed record ImportDto(int Index, string Path, string? Class);

internal sealed record ImportsResult(string File, int Total, IReadOnlyList<ImportDto> Imports, bool Truncated, IReadOnlyList<string> Warnings);

internal sealed record TreeNodeDto(int Depth, string Name, string Path);

internal sealed record TreeResult(string File, int Export, string? Path, IReadOnlyList<TreeNodeDto> Nodes, bool Truncated, IReadOnlyList<string> Warnings);

internal sealed record PropertyDto(string Path, string Value);

internal sealed record DumpResult(
    string File, int Export, int Total, IReadOnlyList<PropertyDto> Properties, bool Truncated, IReadOnlyList<string> Warnings);

internal sealed record SearchHitDto(string Asset, int Export, string ExportName, string Kind, string? Path, string Value);

internal sealed record SearchResultDto(
    string Target, int Total, IReadOnlyList<SearchHitDto> Hits, bool Truncated, IReadOnlyList<string> Warnings);

internal sealed record OpDto(int Line, string Verb, bool Skipped, string Text);

internal sealed record EditResultDto(
    string File,
    bool Saved,
    string? Backup,
    string? OriginalBackup,
    int OpsRun,
    int OpsSkipped,
    IReadOnlyList<OpDto> Ops,
    bool Truncated,
    string Note,
    IReadOnlyList<string> Warnings);

internal sealed record ChangeDto(int Export, string ExportName, string? Path, string Rule, string Old, string New);

internal sealed record AssetChangesDto(string Asset, string Status, string? Error, IReadOnlyList<ChangeDto> Changes);

internal sealed record RulesetResultDto(
    string Target,
    bool Applied,
    int AssetsChanged,
    int Changes,
    int Skipped,
    int Failed,
    IReadOnlyList<AssetChangesDto> Assets,
    bool Truncated,
    string Note,
    IReadOnlyList<string> Warnings);

internal sealed record PakInfoResult(string Pak, string MountPoint, string Version);

internal sealed record EntryDto(string Path, bool IsAsset);

internal sealed record PakListResult(
    string Pak, int Total, int Matched, IReadOnlyList<EntryDto> Entries, bool Truncated, string Note);

internal sealed record IoStoreListResult(
    string Container, int Matched, IReadOnlyList<string> Entries, bool Truncated, string Note);

internal sealed record UnpackResult(
    string Pak, string Destination, int Matched, int Unpacked, IReadOnlyList<string> Failed, string Note);

internal sealed record PackResult(string Output, string Format, int? Files, IReadOnlyList<string> Log, IReadOnlyList<string> Warnings);

internal sealed record ConvertResult(string Output, IReadOnlyList<string> Log, IReadOnlyList<string> Warnings);

internal sealed record RepackResult(
    string Source, string Output, int Entries, int AssetsEdited, int EditsApplied, IReadOnlyList<string> Log, IReadOnlyList<string> Warnings);

internal sealed record UsmapDto(string Game, int? Build, string Path);

internal sealed record UsmapListResult(string Store, IReadOnlyList<UsmapDto> Usmaps);

internal sealed record UsmapAddResult(UsmapDto Stored, string Note);

internal sealed record ModDiffDto(IReadOnlyList<string> OnlyInLog, IReadOnlyList<string> OnlyInOther);

internal sealed record GameLogResult(
    string Log,
    string? Engine,
    int? Build,
    bool Crashed,
    IReadOnlyList<int> Allies,
    IReadOnlyList<int> Enemies,
    IReadOnlyList<int> CharacterLoadsBeforeCrash,
    IReadOnlyList<string> Mods,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> CrashBlock,
    ModDiffDto? Diff,
    IReadOnlyList<string> Notes);
