namespace UAssetEditor.Core.Tests;

/// <summary>
/// UAssetAPI's PakBuilder self-extracts its embedded native repak library to the test
/// output directory on first use; running pak tests from different classes in parallel
/// (xUnit's default across collections) races on writing that same file. Grouping every
/// pak-related test class into one collection keeps them sequential relative to each
/// other without slowing down the rest of the suite.
/// </summary>
[CollectionDefinition("Pak", DisableParallelization = true)]
public class PakTestCollection;
