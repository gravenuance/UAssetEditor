using ModelContextProtocol;
using UAssetEditor.Tooling;

namespace UAssetEditor.Mcp.Tools;

/// <summary>What an ops line may name when the model wrote it. The tool's own parameters already pass through
/// <see cref="ToolPaths"/> and <see cref="OpenSettings"/>; a line must not get round them with options of its own.</summary>
internal static class ScriptOpPolicy
{
    // The tool's game, engineVersion and usmap parameters decide how every op opens assets, so a line may not pick its own.
    private static readonly string[] SettingOptions = ["usmap", "version", "game"];

    /// <summary>Throws when a line sets its own open settings or names a file the tools would not open (relative, network, missing).</summary>
    public static void Validate(IReadOnlyList<ScriptOp> ops, string source)
    {
        ArgumentNullException.ThrowIfNull(ops);

        foreach (var op in ops)
        {
            foreach (var name in SettingOptions)
            {
                if (op.Args.Option(name) != null || op.Args.Flag(name))
                    throw new McpException($"{source}:{op.Line}: --{name} is not allowed in an op; pass the {ParameterFor(name)} parameter of the tool instead.");
            }

            if (op.Args.Option("from-file") is { } donor)
            {
                try
                {
                    ToolPaths.ExistingFile(donor, "from-file", ".uasset");
                }
                catch (McpException ex)
                {
                    throw new McpException($"{source}:{op.Line}: {ex.Message}", ex);
                }
            }
        }
    }

    private static string ParameterFor(string option) => option == "version" ? "engineVersion" : option;
}
