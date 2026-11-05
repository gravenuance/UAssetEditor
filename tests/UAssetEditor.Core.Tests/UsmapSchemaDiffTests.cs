using System.Collections.Concurrent;
using UAssetAPI.Unversioned;
using UAssetEditor.Core.Versioning;

namespace UAssetEditor.Core.Tests;

public class UsmapSchemaDiffTests
{
    private static UsmapSchema Schema(string name, string? super, params (string Name, UsmapPropertyType Type)[] fields)
    {
        var properties = new ConcurrentDictionary<int, UsmapProperty>();
        for (var i = 0; i < fields.Length; i++)
            properties[i] = new UsmapProperty(fields[i].Name, (ushort)i, 0, 1, new UsmapPropertyData(fields[i].Type));
        return new UsmapSchema(name, super, (ushort)fields.Length, properties, true, null);
    }

    private static Usmap Map(params UsmapSchema[] schemas) => new()
    {
        Schemas = schemas.ToDictionary(schema => schema.Name, StringComparer.OrdinalIgnoreCase),
        EnumMap = new Dictionary<string, UsmapEnum>(StringComparer.OrdinalIgnoreCase),
    };

    private static readonly (string, UsmapPropertyType) Mesh = ("SkeletalMesh", UsmapPropertyType.ObjectProperty);
    private static readonly (string, UsmapPropertyType) Bounds = ("BoundsScale", UsmapPropertyType.FloatProperty);
    private static readonly (string, UsmapPropertyType) Owner = ("PawnOwner", UsmapPropertyType.ObjectProperty);

    [Fact]
    public void Compare_IdenticalUsmaps_ReportsNothing()
    {
        var diff = UsmapSchemaDiff.Compare(Map(Schema("Comp", null, Mesh, Bounds)), Map(Schema("Comp", null, Mesh, Bounds)));

        Assert.Empty(diff.Changed);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
    }

    [Fact]
    public void Compare_AnInsertedField_IsAChangeListingIt()
    {
        var diff = UsmapSchemaDiff.Compare(Map(Schema("Comp", null, Mesh, Bounds)), Map(Schema("Comp", null, Mesh, Owner, Bounds)));

        var change = Assert.Single(diff.Changed);
        Assert.Equal("Comp", change.Name);
        Assert.Equal(2, change.OldCount);
        Assert.Equal(3, change.NewCount);
        Assert.Equal(["PawnOwner=ObjectProperty"], change.Added);
        Assert.Empty(change.Removed);
        Assert.False(change.LayoutOnly);
    }

    [Fact]
    public void Compare_AFieldWhoseTypeChanged_IsBothRemovedAndAdded()
    {
        var diff = UsmapSchemaDiff.Compare(
            Map(Schema("Comp", null, Mesh, Bounds)),
            Map(Schema("Comp", null, Mesh, ("BoundsScale", UsmapPropertyType.DoubleProperty))));

        var change = Assert.Single(diff.Changed);
        Assert.Equal(["BoundsScale=DoubleProperty"], change.Added);
        Assert.Equal(["BoundsScale=FloatProperty"], change.Removed);
    }

    [Fact]
    public void Compare_SwappedFields_AreALayoutOnlyChange()
    {
        var diff = UsmapSchemaDiff.Compare(Map(Schema("Comp", null, Mesh, Bounds)), Map(Schema("Comp", null, Bounds, Mesh)));

        Assert.True(Assert.Single(diff.Changed).LayoutOnly);
    }

    [Fact]
    public void Compare_ANewParent_IsAChangeButNotLayoutOnly()
    {
        var diff = UsmapSchemaDiff.Compare(Map(Schema("Comp", "Base", Mesh)), Map(Schema("Comp", "OtherBase", Mesh)));

        var change = Assert.Single(diff.Changed);
        Assert.Equal("Base", change.OldSuper);
        Assert.Equal("OtherBase", change.NewSuper);
        Assert.False(change.LayoutOnly);
    }

    [Fact]
    public void Compare_SchemasOnOneSideOnly_AreAddedOrRemoved_SortedByName()
    {
        var diff = UsmapSchemaDiff.Compare(
            Map(Schema("Kept", null, Mesh), Schema("Gone", null, Mesh)),
            Map(Schema("Kept", null, Mesh), Schema("NewB", null, Mesh), Schema("NewA", null, Mesh)));

        Assert.Empty(diff.Changed);
        Assert.Equal(["NewA", "NewB"], diff.Added);
        Assert.Equal(["Gone"], diff.Removed);
    }
}
