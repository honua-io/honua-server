// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Redshift.Features.FeatureStore.Services;

namespace Honua.Redshift.Tests;

/// <summary>
/// Ungated unit tests for <see cref="RedshiftLayerMapping"/> identifier validation and
/// provider-option resolution.
/// </summary>
public class RedshiftLayerMappingTests
{
    [Fact]
    public void FromStorage_ValidMapping_QuotesTableAndColumns()
    {
        var storage = new FeatureStorageMapping(
            TableName: "parcels",
            SchemaName: "honua",
            PrimaryKeyColumn: "id",
            GeometryColumn: "geom",
            StorageSrid: 4326);

        var mapping = RedshiftLayerMapping.FromStorage(7, storage);

        Assert.Equal("\"honua\".\"parcels\"", mapping.QuotedTableReference);
        Assert.Equal("\"id\"", mapping.QuotedPrimaryKeyColumn);
        Assert.Equal("\"geom\"", mapping.QuotedGeometryColumn);
        Assert.Equal(RedshiftGeometryColumnType.Geometry, mapping.GeometryColumnType);
    }

    [Fact]
    public void FromStorage_NoSchema_OmitsSchemaQualifier()
    {
        var storage = new FeatureStorageMapping(
            TableName: "parcels",
            PrimaryKeyColumn: "id",
            GeometryColumn: "geom",
            StorageSrid: 4326);

        var mapping = RedshiftLayerMapping.FromStorage(7, storage);

        Assert.Equal("\"parcels\"", mapping.QuotedTableReference);
    }

    [Fact]
    public void FromStorage_GeographyOption_ResolvesGeographyColumnType()
    {
        var storage = new FeatureStorageMapping(
            TableName: "parcels",
            PrimaryKeyColumn: "id",
            GeometryColumn: "geom",
            StorageSrid: 4326,
            ProviderOptions: new Dictionary<string, string> { [RedshiftProviderOptions.GeometryType] = "geography" });

        var mapping = RedshiftLayerMapping.FromStorage(7, storage);

        Assert.Equal(RedshiftGeometryColumnType.Geography, mapping.GeometryColumnType);
    }

    [Fact]
    public void FromStorage_InvalidGeometryTypeOption_Throws()
    {
        var storage = new FeatureStorageMapping(
            TableName: "parcels",
            PrimaryKeyColumn: "id",
            GeometryColumn: "geom",
            StorageSrid: 4326,
            ProviderOptions: new Dictionary<string, string> { [RedshiftProviderOptions.GeometryType] = "raster" });

        Assert.Throws<ArgumentException>(() => RedshiftLayerMapping.FromStorage(7, storage));
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
            StorageSrid: 4326);

        Assert.Throws<ArgumentException>(() => RedshiftLayerMapping.FromStorage(7, storage));
    }

    [Fact]
    public void Quote_DoublesEmbeddedQuotesIsRejectedByAllowList()
    {
        // The allow-list rejects embedded quotes before they reach the doubling step.
        Assert.Throws<ArgumentException>(() => RedshiftIdentifier.Quote("col\"umn"));
    }
}
