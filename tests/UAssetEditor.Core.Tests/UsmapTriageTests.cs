using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetEditor.Core.Games;
using UAssetEditor.Core.Versioning;

namespace UAssetEditor.Core.Tests;

public class UsmapTriageTests
{
    private static ExportReading Parsed(string name, params string[] lines) => new(name, false, lines);

    private static ExportReading Raw(string name) => new(name, true, []);

    private static AssetReading Unversioned(params ExportReading[] exports) => new(false, exports);

    [Fact]
    public void Compare_IdenticalReadings_AreSame()
    {
        var (status, detail) = UsmapTriage.Compare(
            Unversioned(Parsed("Mesh", "Scale=1", "extras=4")),
            Unversioned(Parsed("Mesh", "Scale=1", "extras=4")));

        Assert.Equal(TriageStatus.Same, status);
        Assert.Equal("", detail);
    }

    [Fact]
    public void Compare_ATaggedPackage_IsTagged_WhateverItsReadingsSay()
    {
        var (status, _) = UsmapTriage.Compare(new AssetReading(true, [Parsed("Tex", "A=1")]), new AssetReading(true, [Raw("Tex")]));

        Assert.Equal(TriageStatus.Tagged, status);
    }

    [Fact]
    public void Compare_AValueThatDiffers_IsMisread_NamingTheExportAndFirstProperty()
    {
        var (status, detail) = UsmapTriage.Compare(
            Unversioned(Parsed("Root", "A=1"), Parsed("Mesh", "Scale=1", "AttachParent=Root", "extras=4")),
            Unversioned(Parsed("Root", "A=1"), Parsed("Mesh", "Scale=1", "AttachParent=None", "extras=9")));

        Assert.Equal(TriageStatus.Misread, status);
        Assert.Equal("Mesh: differs at AttachParent", detail);
    }

    [Fact]
    public void Compare_OnlyTheNativeDataLength_Differing_IsMisread()
    {
        var (status, detail) = UsmapTriage.Compare(Unversioned(Parsed("Mesh", "extras=4")), Unversioned(Parsed("Mesh", "extras=12")));

        Assert.Equal(TriageStatus.Misread, status);
        Assert.Equal("Mesh: differs at extras", detail);
    }

    [Fact]
    public void Compare_AReadingWithAnExtraProperty_DiffersAtThatProperty()
    {
        var (_, detail) = UsmapTriage.Compare(Unversioned(Parsed("Mesh", "A=1")), Unversioned(Parsed("Mesh", "A=1", "PawnOwner=null")));

        Assert.Equal("Mesh: differs at PawnOwner", detail);
    }

    // A Blueprint CDO parses under neither usmap; it must not hide the export that only the new usmap fails on.
    [Fact]
    public void Compare_NewOnlyFailure_WinsOverAnExportThatFailsUnderBoth()
    {
        var (status, detail) = UsmapTriage.Compare(
            Unversioned(Raw("Default__X_C"), Parsed("Mesh", "A=1")),
            Unversioned(Raw("Default__X_C"), Raw("Mesh")));

        Assert.Equal(TriageStatus.NewFails, status);
        Assert.Equal("Default__X_C: both-raw; Mesh: new-raw", detail);
    }

    [Fact]
    public void Compare_AMisreadExport_WinsOverAnOldOnlyFailure()
    {
        var (status, _) = UsmapTriage.Compare(
            Unversioned(Raw("Mesh"), Parsed("Widget", "Socket=A")),
            Unversioned(Parsed("Mesh", "A=1"), Parsed("Widget", "Socket=B")));

        Assert.Equal(TriageStatus.Misread, status);
    }

    [Fact]
    public void Compare_AnExportOnlyTheOldUsmapFailsOn_IsOldFails()
    {
        var (status, detail) = UsmapTriage.Compare(Unversioned(Raw("Mesh")), Unversioned(Parsed("Mesh", "A=1")));

        Assert.Equal(TriageStatus.OldFails, status);
        Assert.Equal("Mesh: old-raw", detail);
    }

    [Fact]
    public void Compare_OnlyExportsThatFailUnderBoth_AreBothFail()
    {
        var (status, _) = UsmapTriage.Compare(
            Unversioned(Raw("Default__X_C"), Parsed("Mesh", "A=1")),
            Unversioned(Raw("Default__X_C"), Parsed("Mesh", "A=1")));

        Assert.Equal(TriageStatus.BothFail, status);
    }

    [Fact]
    public void Compare_DifferentExportCounts_AreMisread()
    {
        var (status, detail) = UsmapTriage.Compare(Unversioned(Parsed("Mesh")), Unversioned(Parsed("Mesh"), Parsed("Extra")));

        Assert.Equal(TriageStatus.Misread, status);
        Assert.Equal("Extra: only new", detail);
    }

    [Fact]
    public void Label_GivesEveryStatusItsOwnLabel()
    {
        var labels = Enum.GetValues<TriageStatus>().Select(UsmapTriage.Label).ToList();

        Assert.Equal(["OK-SAME", "MISREAD", "NEW-FAILS", "OLD-FAILS", "BOTH-FAIL", "TAGGED", "ERROR"], labels);
    }

    [Fact]
    public void Read_ListsEachPropertyThenTheNativeDataLength_AndMarksRawExportsFailed()
    {
        var asset = TestAssets.CreateAsset();
        var good = TestAssets.CreateSampleExport(asset, exportName: "Good");
        good.Extras = new byte[3];
        asset.Exports.Add(new RawExport([1, 2], asset, []) { ObjectName = new FName(asset, "Broken") });

        var reading = UsmapTriage.Read(asset, "Fake/Path.uasset");

        Assert.True(reading.Tagged); // built in memory without PKG_UnversionedProperties
        Assert.Equal(["Good", "Broken"], reading.Exports.Select(export => export.Name));
        Assert.False(reading.Exports[0].Failed);
        Assert.Contains("Count=5", reading.Exports[0].Lines);
        Assert.Equal("extras=3", reading.Exports[0].Lines[^1]);
        Assert.True(reading.Exports[1].Failed);
        Assert.Empty(reading.Exports[1].Lines);
    }

    [Fact]
    public void Read_AnUnversionedPackage_IsNotTagged()
    {
        var asset = TestAssets.CreateAsset();
        asset.PackageFlags |= EPackageFlags.PKG_UnversionedProperties;

        Assert.False(UsmapTriage.Read(asset, "Fake/Path.uasset").Tagged);
    }

    [Fact]
    public void TriageFile_AUassetWithoutItsUexp_IsAnError_NotAMisread()
    {
        var root = Directory.CreateTempSubdirectory("UAssetEditor_TriageTest_").FullName;
        try
        {
            var path = Path.Combine(root, "Lonely.uasset");
            File.WriteAllBytes(path, [0xC1, 0x83, 0x2A, 0x9E]);

            var result = UsmapTriage.TriageFile(path, EngineVersion.VER_UE5_3, new Usmap(), new Usmap(), Game.None);

            Assert.Equal(TriageStatus.Error, result.Status);
            Assert.Equal("no .uexp beside it", result.Detail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TriageFile_AFileThatIsNotAPackage_IsAnError_AndTheRunGoesOn()
    {
        var root = Directory.CreateTempSubdirectory("UAssetEditor_TriageTest_").FullName;
        try
        {
            var path = Path.Combine(root, "Garbage.uasset");
            File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8]);
            File.WriteAllBytes(Path.ChangeExtension(path, ".uexp"), [9, 9, 9, 9]);

            var result = UsmapTriage.TriageFile(path, EngineVersion.VER_UE5_3, new Usmap(), new Usmap(), Game.None);

            Assert.Equal(TriageStatus.Error, result.Status);
            Assert.NotEqual("", result.Detail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
