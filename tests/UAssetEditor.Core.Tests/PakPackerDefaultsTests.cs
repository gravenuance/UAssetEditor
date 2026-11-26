using UAssetEditor.Core.AssetSources;

namespace UAssetEditor.Core.Tests;

/// <summary>The mount point every front end assumes for a pak built from a folder.</summary>
public sealed class PakPackerDefaultsTests
{
    [Theory]
    [InlineData("D:/Mods/Marvel", "../../../Marvel/")]
    [InlineData("D:/Mods/Marvel/", "../../../Marvel/")]
    [InlineData("D:\\Mods\\Marvel\\\\", "../../../Marvel/")]
    public void DefaultMountPoint_IsTheFoldersOwnName_WhateverSlashesEndThePath(string folder, string expected) =>
        Assert.Equal(expected, PakPacker.DefaultMountPoint(folder));

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("\\\\")]
    public void DefaultMountPoint_OfAPathWithNoFolderName_IsNull(string folder) =>
        Assert.Null(PakPacker.DefaultMountPoint(folder));
}
