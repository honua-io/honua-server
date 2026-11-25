// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Db.Databricks.Queries.Filters;

namespace Honua.Db.Databricks.Tests;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class DatabricksSqlFilterTranslatorTests
{
    [Fact]
    public void Translate_IsNullUnaryForUnknownField_ThrowsTypedException()
    {
        var filter = new UnaryExpression(
            UnaryOperator.IsNull,
            new PropertyReference("collection_specific_field"));

        var exception = Assert.Throws<UnknownFilterFieldException>(
            () => new DatabricksSqlFilterTranslator().Translate(
                filter,
                FilterTranslationContext.FromResource(CreateResource())));

        Assert.Equal("collection_specific_field", exception.PropertyName);
    }

    [Fact]
    public void Translate_SpatialUnknownField_ThrowsTypedException()
    {
        var filter = new SpatialPredicate(
            SpatialOperator.Intersects,
            new PropertyReference("collection_specific_field"),
            new PropertyReference("geometry"));

        var exception = Assert.Throws<UnknownFilterFieldException>(
            () => new DatabricksSqlFilterTranslator().Translate(
                filter,
                FilterTranslationContext.FromResource(CreateResource())));

        Assert.Equal("collection_specific_field", exception.PropertyName);
    }

    [Fact]
    public void Translate_DWithinWithDistanceInMetersOnGeographicSrid_FailsClosed()
    {
        var filter = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            new PropertyReference("geometry"),
            new GeometryLiteral([0x01, 0x01, 0x00, 0x00, 0x00], 4326, "POINT"),
            new Literal(500.0, LiteralType.Number))
        {
            DistanceInMeters = true,
        };

        Assert.Throws<NotSupportedException>(
            () => new DatabricksSqlFilterTranslator().Translate(
                filter,
                FilterTranslationContext.FromResource(CreateSpatialResource())));
    }

    [Fact]
    public void Translate_CrossSridGeometryLiteral_ThrowsNotSupported()
    {
        var filter = new SpatialPredicate(
            SpatialOperator.Intersects,
            new PropertyReference("geometry"),
            new GeometryLiteral([0x01, 0x01, 0x00, 0x00, 0x00], 3857, "POINT"));

        var ex = Assert.Throws<NotSupportedException>(
            () => new DatabricksSqlFilterTranslator().Translate(
                filter,
                FilterTranslationContext.FromResource(CreateSpatialResource())));

        Assert.Contains("Cross-SRID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_EwkbGeometryLiteral_StripsEmbeddedSridAndPassesLayerSrid()
    {
        // Little-endian EWKB POINT with SRID flag (0x20000001) and embedded SRID 4326.
        byte[] ewkbPoint =
        [
            0x01,
            0x01, 0x00, 0x00, 0x20,
            0xE6, 0x10, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        ];
        var filter = new SpatialPredicate(
            SpatialOperator.Intersects,
            new PropertyReference("geometry"),
            new GeometryLiteral(ewkbPoint, 4326, "POINT"));

        var fragment = new DatabricksSqlFilterTranslator().Translate(
            filter,
            FilterTranslationContext.FromResource(CreateSpatialResource()));

        Assert.Contains("st_geomfromwkb(unhex(:p0), 4326)", fragment.Sql, StringComparison.Ordinal);
        var boundHex = Assert.IsType<string>(Assert.Single(fragment.Parameters));
        // Normalized OGC WKB has 21 bytes (42 hex chars), not 25 bytes (50 hex chars).
        Assert.Equal(42, boundHex.Length);
        Assert.StartsWith("0101000000", boundHex, StringComparison.Ordinal);
    }

    private static MetadataV2Object CreateSpatialResource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-parcels", Name = "parcels" },
            Type = MetadataV2ObjectType.FeatureDataset,
            Spatial = new MetadataV2ObjectSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Polygon,
                PrimaryGeometryField = "geometry",
            },
            SchemaFields =
            [
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
                new MetadataV2Field { Name = "geometry", Type = MetadataV2FieldType.Geometry },
            ],
        };

    private static MetadataV2Object CreateResource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-parcels", Name = "parcels" },
            Type = MetadataV2ObjectType.FeatureDataset,
            SchemaFields =
            [
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
            ],
        };
}
