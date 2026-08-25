using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.Editing;
using UAssetEditor.Core.PropertyAccess;
using UAssetEditor.Core.Search;
using UAssetEditor.Core.Versioning;

namespace UAssetEditor.Core.Tests;

public class EditExecutorTests
{
    [Fact]
    public async Task PreviewAsync_ComputesChangeAndAppliesItInMemoryButNeverSaves()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset);
        var source = new InMemoryAssetSource(new Dictionary<string, UAsset> { ["a.uasset"] = asset });
        var ruleSet = new RuleSet
        {
            Scope = new SearchQuery { PropertyNameTerms = ["Count"] },
            Rules = { new SetPropertyValueRule { NewValue = "42" } },
        };

        var changeSets = await EditExecutor.PreviewAsync(source, new EngineVersionResolver(), ruleSet);

        var change = Assert.Single(Assert.Single(changeSets).Changes);
        Assert.Equal("5", change.OldValue);
        Assert.Equal("42", change.NewValue);
        Assert.Equal(0, source.SaveCount);

        var countNode = PropertyWalker.Walk(export).Single(n => n.Path == "Count");
        Assert.Equal("42", PropertyValueAccessor.AsSearchableString(countNode.Property, asset));
    }

    [Fact]
    public async Task ApplyAsync_SavesAssetWhenChangesWereMade()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);
        var source = new InMemoryAssetSource(new Dictionary<string, UAsset> { ["a.uasset"] = asset });
        var ruleSet = new RuleSet
        {
            Scope = new SearchQuery { PropertyNameTerms = ["Count"] },
            Rules = { new SetPropertyValueRule { NewValue = "42" } },
        };

        await EditExecutor.ApplyAsync(source, new EngineVersionResolver(), ruleSet, createBackup: false, backupFolder: null);

        Assert.Equal(1, source.SaveCount);
    }

    [Fact]
    public async Task StageAsync_MutatesWhateverOpenAssetReturnsButNeverSaves()
    {
        // Regression test: Apply used to open a fresh, throwaway UAsset per asset and save it
        // immediately - bypassing whatever cache (e.g. AssetWorkspace) the caller uses to back
        // an editable grid, so the grid kept showing stale pre-Apply values even though the
        // file on disk was correctly updated. StageAsync instead mutates the exact instance its
        // caller-supplied openAsset function hands back (standing in here for a workspace's
        // GetOrOpen, which always returns the same cached instance for a path) and must never
        // call SaveAsset itself - saving becomes the caller's own explicit, later decision.
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset); // Count = 5
        var source = new InMemoryAssetSource(new Dictionary<string, UAsset> { ["a.uasset"] = asset });
        var ruleSet = new RuleSet
        {
            Scope = new SearchQuery { PropertyNameTerms = ["Count"] },
            Rules = { new SetPropertyValueRule { NewValue = "42" } },
        };

        var changeSets = await EditExecutor.StageAsync(source, path => source.OpenAsset(path, EngineVersion.UNKNOWN, null), ruleSet);

        Assert.Equal(0, source.SaveCount);
        var change = Assert.Single(Assert.Single(changeSets).Changes);
        Assert.Equal("5", change.OldValue);
        Assert.Equal("42", change.NewValue);

        var countNode = PropertyWalker.Walk(export).Single(n => n.Path == "Count");
        Assert.Equal("42", PropertyValueAccessor.AsSearchableString(countNode.Property, asset));
    }

    [Fact]
    public async Task ApplyAsync_DoesNotSaveWhenNoRuleMatched()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);
        var source = new InMemoryAssetSource(new Dictionary<string, UAsset> { ["a.uasset"] = asset });
        var ruleSet = new RuleSet
        {
            Scope = new SearchQuery { PropertyNameTerms = ["NoSuchProperty"] },
            Rules = { new SetPropertyValueRule { NewValue = "42" } },
        };

        var changeSets = await EditExecutor.ApplyAsync(source, new EngineVersionResolver(), ruleSet, createBackup: false, backupFolder: null);

        Assert.Empty(changeSets);
        Assert.Equal(0, source.SaveCount);
    }

    [Fact]
    public async Task RemoveTagRule_RemovesMatchingElementFromNameArray()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);
        var source = new InMemoryAssetSource(new Dictionary<string, UAsset> { ["a.uasset"] = asset });
        var ruleSet = new RuleSet
        {
            Scope = new SearchQuery { PropertyNameTerms = ["Tags"] },
            Rules = { new RemoveTagRule { Tag = "Alpha" } },
        };

        var changeSets = await EditExecutor.PreviewAsync(source, new EngineVersionResolver(), ruleSet);

        // Both "Tags" (the array itself, matched by name) and "Tags[0]"/"Tags[1]" (elements)
        // match the scope; only the array-typed node can honor a RemoveTagRule.
        var change = Assert.Single(Assert.Single(changeSets).Changes);
        Assert.Equal("RemoveTag", change.RuleDescription);
        Assert.Equal("Alpha", change.NewValue);
    }

    [Theory]
    [InlineData("add", "3", 8)]
    [InlineData("sub", "3", 2)]
    [InlineData("mul", "3", 15)]
    [InlineData("set", "3", 3)]
    public async Task NumericAdjustRule_AppliesArithmeticToIntProperty(string operation, string target, int expected)
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset); // Count = 5
        var source = new InMemoryAssetSource(new Dictionary<string, UAsset> { ["a.uasset"] = asset });
        var ruleSet = new RuleSet
        {
            Scope = new SearchQuery { PropertyNameTerms = ["Count"] },
            Rules = { new NumericAdjustRule { Operation = operation, TargetValue = target } },
        };

        var changeSets = await EditExecutor.PreviewAsync(source, new EngineVersionResolver(), ruleSet);

        var change = Assert.Single(Assert.Single(changeSets).Changes);
        Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), change.NewValue);
    }

    [Fact]
    public async Task NumericAdjustRule_DivideByZero_SkipsInsteadOfThrowing()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset); // Count = 5
        var source = new InMemoryAssetSource(new Dictionary<string, UAsset> { ["a.uasset"] = asset });
        var ruleSet = new RuleSet
        {
            Scope = new SearchQuery { PropertyNameTerms = ["Count"] },
            Rules = { new NumericAdjustRule { Operation = "div", TargetValue = "0" } },
        };

        var changeSets = await EditExecutor.PreviewAsync(source, new EngineVersionResolver(), ruleSet);

        Assert.Empty(changeSets);
    }

    [Fact]
    public async Task NumericAdjustRule_SkipCondition_LeavesMatchingValuesUntouched()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset); // Count = 5
        var source = new InMemoryAssetSource(new Dictionary<string, UAsset> { ["a.uasset"] = asset });
        var ruleSet = new RuleSet
        {
            Scope = new SearchQuery { PropertyNameTerms = ["Count"] },
            Rules = { new NumericAdjustRule { Operation = "add", TargetValue = "1", Skip = new SkipCondition { Comparison = SkipComparison.Eq, Value = "5" } } },
        };

        var changeSets = await EditExecutor.PreviewAsync(source, new EngineVersionResolver(), ruleSet);

        Assert.Empty(changeSets);
    }

    [Fact]
    public async Task SetPropertyValueRule_SkipCondition_ComparesAgainstCurrentTextValue()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset); // DisplayName = "Hello World"
        var source = new InMemoryAssetSource(new Dictionary<string, UAsset> { ["a.uasset"] = asset });
        var ruleSet = new RuleSet
        {
            Scope = new SearchQuery { PropertyNameTerms = ["DisplayName"] },
            Rules = { new SetPropertyValueRule { NewValue = "Changed", Skip = new SkipCondition { Comparison = SkipComparison.Eq, Value = "Hello World" } } },
        };

        var changeSets = await EditExecutor.PreviewAsync(source, new EngineVersionResolver(), ruleSet);

        Assert.Empty(changeSets);
    }

    [Fact]
    public async Task SetPropertyValueRule_UpdatesIsZeroFlagAfterMutation()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset); // Count = 5 (non-zero)
        var source = new InMemoryAssetSource(new Dictionary<string, UAsset> { ["a.uasset"] = asset });
        var ruleSet = new RuleSet
        {
            Scope = new SearchQuery { PropertyNameTerms = ["Count"] },
            Rules = { new SetPropertyValueRule { NewValue = "0" } },
        };

        await EditExecutor.PreviewAsync(source, new EngineVersionResolver(), ruleSet);

        var countNode = PropertyWalker.Walk(export).Single(n => n.Path == "Count");
        Assert.True(countNode.Property.IsZero);
    }

    [Fact]
    public async Task RemovePropertyRule_MultipleMatchesInSameExport_UnaffectedByEarlierRemovalShiftingIndices()
    {
        // Regression test for the property-node cache added to avoid re-walking the whole
        // export per (rule, match): removing "Count" (index 1) shifts "DisplayName" from
        // index 2 to index 1 in the underlying list. If the cache weren't invalidated after
        // a structural mutation, the second match's stale OwnerIndex would remove whatever
        // now sits at index 2 ("Location") instead of "DisplayName".
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset); // bEnabled, Count, DisplayName, Location, Tags
        var source = new InMemoryAssetSource(new Dictionary<string, UAsset> { ["a.uasset"] = asset });
        var ruleSet = new RuleSet
        {
            Scope = new SearchQuery { PropertyNameTerms = [new ConditionTerm("Count", TermTag.Or), new ConditionTerm("DisplayName", TermTag.Or)] },
            Rules = { new RemovePropertyRule() },
        };

        var changeSets = await EditExecutor.PreviewAsync(source, new EngineVersionResolver(), ruleSet);

        var changes = Assert.Single(changeSets).Changes;
        Assert.Equal(2, changes.Count);

        var remainingNames = export.Data.Select(p => p.Name.Value!.Value).ToList();
        Assert.DoesNotContain("Count", remainingNames);
        Assert.DoesNotContain("DisplayName", remainingNames);
        Assert.Contains("bEnabled", remainingNames);
        Assert.Contains("Location", remainingNames);
        Assert.Contains("Tags", remainingNames);
    }

    [Fact]
    public async Task PreviewAsync_DoesNotThrow_WhenAScopePatternIsInvalidRegex()
    {
        // Regression test: an unhandled exception from one asset's rule application (or
        // scope evaluation) used to propagate out of the whole batch, discarding results
        // already computed for every other asset. Now it's caught per-asset.
        var assetOne = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(assetOne);
        var assetTwo = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(assetTwo);

        var source = new InMemoryAssetSource(new Dictionary<string, UAsset>
        {
            ["a.uasset"] = assetOne,
            ["b.uasset"] = assetTwo,
        });
        var ruleSet = new RuleSet
        {
            Scope = new SearchQuery { PropertyNameTerms = ["["], PropertyNameCompare = TextCompare.Regex }, // unterminated character class
            Rules = { new SetPropertyValueRule { NewValue = "x" } },
        };

        var exception = await Record.ExceptionAsync(() => EditExecutor.PreviewAsync(source, new EngineVersionResolver(), ruleSet));

        Assert.Null(exception);
    }
}
