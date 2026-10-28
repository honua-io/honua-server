// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Databricks.Features.Infrastructure;

namespace Honua.Db.Databricks.Tests;

/// <summary>
/// Identifier validation and quoting for Metadata v2 <see cref="DatabricksLayerMapping.FromStorage"/>.
/// </summary>
[global::Xunit.Trait("Tier", "Fast")]
public class DatabricksLayerMappingTests
{
    [Fact]
    public void FromStorage_ValidMapping_QuotesCatalogSchemaAndTable()
    {
        var storage = new FeatureStorageMapping(
            TableName: "parcels",
            SchemaName: "gis",
            CatalogName: "main",
            PrimaryKeyColumn: "id",
            GeometryColumn: "geom",
            StorageSrid: 4326);

        var mapping = DatabricksLayerMapping.FromStorage(0, storage);

        Assert.Equal("`main`.`gis`.`parcels`", mapping.QualifiedTable());
        Assert.Equal("geom", mapping.GeometryColumn);
        Assert.Equal("id", mapping.PrimaryKeyColumn);
        Assert.Equal(4326, mapping.Srid);
        Assert.Equal(GeometryType.Point, mapping.GeometryType);
        Assert.Empty(mapping.AttributeColumns);
    }

    [Fact]
    public void FromStorage_NoSchema_OmitsSchemaQualifier()
    {
        var storage = new FeatureStorageMapping(
            TableName: "parcels",
            CatalogName: "main",
            PrimaryKeyColumn: "id",
            GeometryColumn: "geom",
            StorageSrid: 4326);

        var mapping = DatabricksLayerMapping.FromStorage(0, storage);

        Assert.Equal("`main`.`parcels`", mapping.QualifiedTable());
    }

    [Fact]
    public void FromStorage_GeometryTypeOption_ResolvesDeclaredType()
    {
        var storage = new FeatureStorageMapping(
            TableName: "parcels",
            PrimaryKeyColumn: "id",
            GeometryColumn: "geom",
            StorageSrid: 4326,
            ProviderOptions: new Dictionary<string, string>
            {
                [DatabricksProviderOptions.GeometryType] = "Polygon"
            });

        var mapping = DatabricksLayerMapping.FromStorage(0, storage);

        Assert.Equal(GeometryType.Polygon, mapping.GeometryType);
    }

    [Fact]
    public void FromStorage_InvalidGeometryTypeOption_Throws()
    {
        var storage = new FeatureStorageMapping(
            TableName: "parcels",
            PrimaryKeyColumn: "id",
            GeometryColumn: "geom",
            StorageSrid: 4326,
            ProviderOptions: new Dictionary<string, string>
            {
                [DatabricksProviderOptions.GeometryType] = "raster"
            });

        Assert.Throws<ArgumentException>(() => DatabricksLayerMapping.FromStorage(0, storage));
    }

    [Fact]
    public void FromStorage_SharedTableDiscriminator_IsNotDropped()
    {
        var storage = new FeatureStorageMapping(
            TableName: "features",
            SchemaName: "gis",
            CatalogName: "main",
            PrimaryKeyColumn: "id",
            GeometryColumn: "geom",
            StorageSrid: 4326,
            LayerDiscriminatorColumn: "layer_id",
            LayerDiscriminatorValue: 7);

        var ex = Assert.Throws<ArgumentException>(() => DatabricksLayerMapping.FromStorage(7, storage));

        Assert.Contains("layer_id", ex.Message, StringComparison.Ordinal);
        Assert.Contains("= 7", ex.Message, StringComparison.Ordinal);
        Assert.Contains("cannot enforce", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromStorage_DiscriminatorValueWithoutColumn_StillBinds()
    {
        var storage = new FeatureStorageMapping(
            TableName: "parcels",
            PrimaryKeyColumn: "id",
            GeometryColumn: "geom",
            StorageSrid: 4326,
            LayerDiscriminatorValue: 0);

        var mapping = DatabricksLayerMapping.FromStorage(0, storage);

        Assert.Equal("`parcels`", mapping.QualifiedTable());
    }

    [Fact]
    public void FromStorage_MissingGeometryColumn_Throws()
    {
        var storage = new FeatureStorageMapping(
            TableName: "parcels",
            PrimaryKeyColumn: "id",
            GeometryColumn: null,
            StorageSrid: 4326);

        Assert.Throws<ArgumentException>(() => DatabricksLayerMapping.FromStorage(0, storage));
    }

    [Theory]
    [InlineData("parcels; DROP TABLE x")]
    [InlineData("bad-name")]
    [InlineData("1table")]
    public void FromStorage_InvalidTableIdentifier_Throws(string tableName)
    {
        var storage = new FeatureStorageMapping(
            TableName: tableName,
            PrimaryKeyColumn: "id",
            GeometryColumn: "geom",
            StorageSrid: 4326);

        Assert.Throws<ArgumentException>(() => DatabricksLayerMapping.FromStorage(0, storage));
    }
}
