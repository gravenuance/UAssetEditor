using Microsoft.Extensions.DependencyInjection;
using UAssetEditor.Core.Games;
using UAssetEditor.Mcp.Tools;

namespace UAssetEditor.Mcp.Hosting;

internal static class ServiceRegistration
{
    /// <summary>Every tool class the server offers, in one place so a test can compare it with what is listed.</summary>
    public static IReadOnlyList<Type> ToolTypes { get; } =
        [typeof(AssetReadTools), typeof(AssetEditTools), typeof(RulesetTools), typeof(ArchiveReadTools), typeof(ArchiveWriteTools), typeof(UsmapTools), typeof(GameLogTools)];

    /// <summary>What the tools run on: the usmap store (the folder <c>UASSETEDITOR_MAPPINGS_DIR</c> names, else the default)
    /// and the tool classes themselves, built per call.</summary>
    public static IServiceCollection AddUAssetMcpServices(this IServiceCollection services, MappingsStore store)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(store);

        services.AddSingleton(store);
        foreach (var tool in ToolTypes.Where(type => !(type.IsAbstract && type.IsSealed)))
        {
            services.AddTransient(tool); // a static class has nothing to build
        }

        return services;
    }
}
