using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace UAssetEditor.Core.Games.Rivals;

/// <summary>
/// Rewrites a package's KawaiiPhysics anim nodes from the community plugin's flat layout (RootBone, PhysicsSettings,
/// ... directly on the node) into the game's <c>Chains</c> layout, and optionally fills mesh
/// <c>DefaultHiddenMaterials</c>. Ported from repak-rivals' KawaiiPhysicsLegacyPorter; see THIRD_PARTY_NOTICES.md.
/// </summary>
public static class KawaiiPhysicsLegacyPorter
{
    private const string AnimNodeStruct = "AnimNode_KawaiiPhysics";
    private const string ChainStruct = "KawaiiPhysicsChain";
    private const string BoneSettingsStruct = "BoneSettings";
    private const string BoneChainPhysicsSettingsStruct = "BoneChainPhysicsSettings";
    private const string KawaiiPhysicsSettingsStruct = "KawaiiPhysicsSettings";
    private const string BoneConstraintSettingsStruct = "BoneConstraintSettings";
    private const string ExternalForceSettingsStruct = "ExternalForceSettings";
    private const string WaveAnimSettingsStruct = "WaveAnimSettings";

    /// <summary>Patches <paramref name="asset"/> in memory and reports what changed; the caller writes it back when <see cref="KawaiiPhysicsPortResult.Changed"/>.</summary>
    /// <exception cref="InvalidDataException">Hidden-material bitmaps were given for a mesh with fewer LODs.</exception>
    public static KawaiiPhysicsPortResult PortLegacyAnimNodes(UAsset asset, KawaiiPhysicsPortOptions options)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(options);

        var result = new KawaiiPhysicsPortResult();
        if (asset.Exports == null) return result;

        HiddenMaterialsResult? hiddenMaterials = null;
        if (options.PatchDefaultHiddenMaterials && !options.HasBitmapOverride)
        {
            hiddenMaterials = HiddenMaterialsReader.ReadFromAsset(asset);
        }

        foreach (var export in asset.Exports)
        {
            if (export is not NormalExport { Data: { } data } normalExport) continue;

            var mutated = options.PatchKawaiiPhysics && PortPropertyList(asset, data, options, result);
            mutated |= PatchDefaultHiddenMaterials(asset, normalExport, options, hiddenMaterials, result);
            if (mutated)
            {
                normalExport.ResolveAncestries(asset, new AncestryInfo());
            }
        }

        return result;
    }

    private static bool PatchDefaultHiddenMaterials(
        UAsset asset,
        NormalExport export,
        KawaiiPhysicsPortOptions options,
        HiddenMaterialsResult? hiddenMaterials,
        KawaiiPhysicsPortResult result)
    {
        var patched = 0;
        if (options.HasBitmapOverride)
        {
            patched = HiddenMaterialsReader.InjectBitmapsIntoLodInfo(export, options.DefaultHiddenMaterialBitmaps!, asset);
        }
        else if (options.PatchDefaultHiddenMaterials && hiddenMaterials?.FoundUserData == true)
        {
            patched = HiddenMaterialsReader.InjectIntoLodInfo(export, hiddenMaterials, asset);
        }

        if (patched <= 0) return false;
        result.PatchedDefaultHiddenMaterialLods += patched;
        return true;
    }

    private static bool PortPropertyList(UAsset asset, List<PropertyData> properties, KawaiiPhysicsPortOptions options, KawaiiPhysicsPortResult result)
    {
        var mutated = false;
        for (var i = 0; i < properties.Count; i++)
        {
            mutated |= PortProperty(asset, properties[i], options, result);
        }

        return mutated;
    }

    private static bool PortProperty(UAsset asset, PropertyData property, KawaiiPhysicsPortOptions options, KawaiiPhysicsPortResult result)
    {
        switch (property)
        {
            case StructPropertyData structProperty:
                var mutated = false;
                if (IsKawaiiAnimNode(structProperty))
                {
                    result.VisitedAnimNodes++;
                    mutated |= PortAnimNode(asset, structProperty, options, result);
                }

                if (structProperty.Value != null)
                {
                    mutated |= PortPropertyList(asset, structProperty.Value, options, result);
                }

                return mutated;

            case ArrayPropertyData { Value: { } items }:
                var arrayMutated = false;
                foreach (var item in items)
                {
                    arrayMutated |= PortProperty(asset, item, options, result);
                }

                return arrayMutated;

            default:
                return false;
        }
    }

    private static bool PortAnimNode(UAsset asset, StructPropertyData node, KawaiiPhysicsPortOptions options, KawaiiPhysicsPortResult result)
    {
        var chains = Get<ArrayPropertyData>(node, "Chains");
        var hasChains = chains?.Value is { Length: > 0 };
        if (hasChains && !options.ForceRebuildChain0)
        {
            result.SkippedExistingChains++;
            return false;
        }

        var hasLegacy = HasLegacyKawaiiSettings(node);
        if (!hasLegacy && !hasChains) return false;

        // Current assets can keep deprecated node-level fields next to a tuned Chains array;
        // once Chains exists, its bones and settings are authoritative.
        var chain = hasChains
            ? RebuildExistingChain(asset, chains!.Value[0])
            : BuildChainFromLegacyNode(asset, node);
        if (chain == null) return false;

        if (chains == null)
        {
            chains = new ArrayPropertyData(Name(asset, "Chains"));
            Set(asset, node, "Chains", chains);
        }

        chains.ArrayType = Name(asset, "StructProperty");
        chains.DummyStruct = chain;
        chains.Value = options.ForceRebuildChain0 && hasChains ? RebuildFirstChain(chains.Value, chain) : [chain];
        result.PortedAnimNodes++;
        return true;
    }

    private static PropertyData[] RebuildFirstChain(PropertyData[]? existingChains, StructPropertyData replacement)
    {
        if (existingChains == null || existingChains.Length == 0) return [replacement];

        var rebuilt = new PropertyData[existingChains.Length];
        rebuilt[0] = replacement;
        for (var i = 1; i < existingChains.Length; i++)
        {
            rebuilt[i] = existingChains[i];
        }

        return rebuilt;
    }

    private static StructPropertyData? RebuildExistingChain(UAsset asset, PropertyData existingChain)
    {
        if (existingChain is not StructPropertyData chain) return null;

        var rebuilt = (StructPropertyData)DeepClone(chain);
        rebuilt.Name = Name(asset, "Chains");
        rebuilt.StructType = Name(asset, ChainStruct);

        NormalizeChainForRivals(asset, rebuilt);
        return rebuilt;
    }

    /// <summary>Makes sure the chain's nested settings structs exist with the game's struct types.</summary>
    private static void NormalizeChainForRivals(UAsset asset, StructPropertyData chain)
    {
        var chainPhysics = EnsureStruct(asset, chain, "PhysicsSettings", BoneChainPhysicsSettingsStruct);
        EnsureStruct(asset, chainPhysics, "PhysicsSettings", KawaiiPhysicsSettingsStruct);
        EnsureStruct(asset, chain, "ExternalForceSettings", ExternalForceSettingsStruct);
    }

    private static StructPropertyData? ClonePhysicsSettings(UAsset asset, StructPropertyData node)
    {
        if (CloneAs(asset, node, "PhysicsSettings", "PhysicsSettings") is not StructPropertyData physics) return null;

        physics.Value ??= [];
        return physics;
    }

    private static StructPropertyData BuildChainFromLegacyNode(UAsset asset, StructPropertyData node)
    {
        var chain = Struct(asset, "Chains", ChainStruct);

        Set(asset, chain, "BoneSettings", Struct(asset, "BoneSettings", BoneSettingsStruct,
            CloneAs(asset, node, "RootBone", "RootBone"),
            CloneAs(asset, node, "ExcludeBones", "ExcludeBones"),
            CloneAs(asset, node, "bRootBoneSimulate", "bRootBoneSimulate"),
            CloneAs(asset, node, "bShouldFixTailBone", "bShouldFixTailBone"),
            CloneAs(asset, node, "FixedBone", "FixedBone"),
            CloneAs(asset, node, "FollowBone", "FollowBone"),
            CloneAs(asset, node, "AdditionalRootBones", "AdditionalRootBones"),
            CloneAs(asset, node, "DummyBoneLength", "DummyBoneLength"),
            CloneAs(asset, node, "BoneForwardAxis", "BoneForwardAxis")));

        var chainPhysicsSettings = Struct(asset, "PhysicsSettings", BoneChainPhysicsSettingsStruct,
            ClonePhysicsSettings(asset, node),
            CloneAs(asset, node, "CustomShapes", "CustomShapes"),
            CloneAs(asset, node, "bUseCurve", "bUseCurve"),
            CloneAs(asset, node, "PhysicsParamsPerBone", "PhysicsParamsPerBone"),
            CloneAs(asset, node, "TeleportDistanceThreshold", "TeleportDistanceThreshold"),
            CloneAs(asset, node, "TeleportRotationThreshold", "TeleportRotationThreshold"),
            CloneAs(asset, node, "PlanarConstraint", "PlanarConstraint"),
            CloneAs(asset, node, "LimitLinearCurveData", "LimitLinearCurveData"),
            CloneAs(asset, node, "GravityCurveData", "GravityCurveData"),
            CloneAs(asset, node, "DampingCurveData", "DampingCurveData"),
            CloneAs(asset, node, "StiffnessCurveData", "StiffnessCurveData"),
            CloneAs(asset, node, "WorldDampingLocationCurveData", "WorldDampingLocationCurveData"),
            CloneAs(asset, node, "WorldDampingRotationCurveData", "WorldDampingRotationCurveData"),
            CloneAs(asset, node, "RadiusCurveData", "RadiusCurveData"),
            CloneAs(asset, node, "LimitAngleCurveData", "LimitAngleCurveData"));
        Set(asset, chain, "PhysicsSettings", chainPhysicsSettings);

        Set(asset, chain, "BoneConstraintSettings", Struct(asset, "BoneConstraintSettings", BoneConstraintSettingsStruct,
            CloneAs(asset, node, "BoneConstraintGlobalComplianceType", "BoneConstraintGlobalComplianceType"),
            CloneAs(asset, node, "BoneConstraintIterationCountBeforeCollision", "BoneConstraintIterationCountBeforeCollision"),
            CloneAs(asset, node, "BoneConstraintIterationCountAfterCollision", "BoneConstraintIterationCountAfterCollision"),
            CloneAs(asset, node, "bAutoAddChildDummyBoneConstraint", "bAutoAddChildDummyBoneConstraint"),
            CloneAs(asset, node, "BoneConstraints", "BoneConstraints"),
            CloneAs(asset, node, "BoneConstraintsDataAsset", "BoneConstraintsDataAsset"),
            CloneAs(asset, node, "BoneConstraintsData", "BoneConstraintsData")));

        var externalForceSettings = Struct(asset, "ExternalForceSettings", ExternalForceSettingsStruct,
            CloneAs(asset, node, "bDisableAllExternalForces", "bDisableAllExternalForces"),
            CloneAs(asset, node, "Gravity", "Gravity"),
            CloneAs(asset, node, "bEnableWind", "bEnableWind"),
            CloneAs(asset, node, "WindScale", "WindScale"),
            CloneAs(asset, node, "ExternalForces", "ExternalForces"),
            CloneAs(asset, node, "CustomExternalForces", "CustomExternalForces"));
        Set(asset, chain, "ExternalForceSettings", externalForceSettings);

        // The plugin spelled the wave frequency "WaveFrequncy"; both spellings feed the same field.
        Set(asset, chain, "WaveAnimSettings", Struct(asset, "WaveAnimSettings", WaveAnimSettingsStruct,
            CloneAs(asset, node, "bEnableWaveAnim", "bEnableWaveAnim"),
            CloneAs(asset, node, "WaveBeginBone", "WaveBeginBone"),
            CloneAs(asset, node, "WaveFrequncy", "WaveFrequncy"),
            CloneAs(asset, node, "WaveFrequency", "WaveFrequncy"),
            CloneAs(asset, node, "WaveNum", "WaveNum"),
            CloneAs(asset, node, "WaveDirection", "WaveDirection"),
            CloneAs(asset, node, "WaveAmplitude", "WaveAmplitude"),
            CloneAs(asset, node, "WaveAmplitudeCurveData", "WaveAmplitudeCurveData")));
        Set(asset, chain, "AutoConfiguredLODThreshold", new IntPropertyData(Name(asset, "AutoConfiguredLODThreshold")) { Value = -1 });
        NormalizeChainForRivals(asset, chain);

        return chain;
    }

    private static bool IsKawaiiAnimNode(StructPropertyData property)
    {
        var structType = property.StructType?.Value?.Value;
        if (string.Equals(structType, AnimNodeStruct, StringComparison.Ordinal)
            || string.Equals(structType, "F" + AnimNodeStruct, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(property.Name?.Value?.Value, "Node", StringComparison.Ordinal)
            && Get(property, "RootBone") != null
            && Get(property, "PhysicsSettings") != null;
    }

    private static bool HasLegacyKawaiiSettings(StructPropertyData node) =>
        Get(node, "RootBone") != null
        || Get(node, "PhysicsSettings") != null
        || Get(node, "ExternalForces") != null
        || Get(node, "BoneConstraints") != null
        || Get(node, "CustomShapes") != null
        || Get(node, "PhysicsParamsPerBone") != null
        || Get(node, "LimitsDataAsset") != null;

    private static StructPropertyData Struct(UAsset asset, string name, string structType, params PropertyData?[] properties)
    {
        var result = new StructPropertyData(Name(asset, name), Name(asset, structType))
        {
            StructGUID = Guid.Empty,
            SerializeNone = true,
            Value = [],
        };

        foreach (var property in properties)
        {
            if (property != null) result.Value.Add(property);
        }

        return result;
    }

    private static StructPropertyData EnsureStruct(UAsset asset, StructPropertyData parent, string name, string structType)
    {
        if (Get(parent, name) is StructPropertyData existing)
        {
            existing.Name = Name(asset, name);
            existing.StructType = Name(asset, structType);
            existing.Value ??= [];
            existing.StructGUID = Guid.Empty;
            existing.SerializeNone = true;
            return existing;
        }

        var created = Struct(asset, name, structType);
        Set(asset, parent, name, created);
        return created;
    }

    private static PropertyData? CloneAs(UAsset asset, StructPropertyData source, string oldName, string newName)
    {
        var original = Get(source, oldName);
        if (original == null) return null;

        var clone = DeepClone(original);
        clone.Name = Name(asset, newName);
        return clone;
    }

    /// <summary>Clones a property including array elements and the array's dummy struct, which a plain Clone shares.</summary>
    private static PropertyData DeepClone(PropertyData property)
    {
        var clone = (PropertyData)property.Clone();

        if (property is ArrayPropertyData sourceArray && clone is ArrayPropertyData clonedArray)
        {
            if (sourceArray.Value != null)
            {
                var clonedValues = new PropertyData[sourceArray.Value.Length];
                for (var i = 0; i < sourceArray.Value.Length; i++)
                {
                    clonedValues[i] = sourceArray.Value[i] == null ? null! : DeepClone(sourceArray.Value[i]);
                }

                clonedArray.Value = clonedValues;
            }

            clonedArray.DummyStruct = sourceArray.DummyStruct == null ? null! : (StructPropertyData)DeepClone(sourceArray.DummyStruct);
        }

        return clone;
    }

    private static PropertyData? Get(StructPropertyData property, string name)
    {
        if (property.Value == null) return null;

        foreach (var child in property.Value)
        {
            if (string.Equals(child?.Name?.Value?.Value, name, StringComparison.Ordinal)) return child;
        }

        return null;
    }

    private static T? Get<T>(StructPropertyData property, string name) where T : PropertyData => Get(property, name) as T;

    private static void Set(UAsset asset, StructPropertyData property, string name, PropertyData? value)
    {
        if (value == null) return;
        value.Name = Name(asset, name);

        property.Value ??= [];
        for (var i = 0; i < property.Value.Count; i++)
        {
            if (string.Equals(property.Value[i]?.Name?.Value?.Value, name, StringComparison.Ordinal))
            {
                property.Value[i] = value;
                return;
            }
        }

        property.Value.Add(value);
    }

    private static FName Name(UAsset asset, string value) => new(asset, value);
}
