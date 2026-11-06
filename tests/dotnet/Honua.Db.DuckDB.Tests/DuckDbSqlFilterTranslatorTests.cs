// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using Honua.Core.Exceptions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Db.DuckDB.Queries.Filters;

namespace Honua.Db.DuckDB.Tests;

public sealed class DuckDbSqlFilterTranslatorTests
{
    [Fact]
    public void Translate_IsNullUnaryForUnknownField_ThrowsTypedException()
    {
        var filter = new UnaryExpression(
            UnaryOperator.IsNull,
            new PropertyReference("collection_specific_field"));

        var exception = Assert.Throws<UnknownFilterFieldException>(
            () => new DuckDbSqlFilterTranslator().Translate(filter, CreateResource()));

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
            () => new DuckDbSqlFilterTranslator().Translate(filter, CreateResource()));

        Assert.Equal("collection_specific_field", exception.PropertyName);
    }

    [Fact]
    public void Translate_DWithin_Nad27PointLayer_TransformsToWgs84BeforeSpheroid()
    {
        // NAD27 degrees are not WGS 84. The translator must shift both operands to EPSG:4326
        // before ST_Distance_Spheroid rather than measure the stored degrees on that spheroid.
        var filter = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            new PropertyReference("geometry"),
            new GeometryLiteral(CreatePointWkb(-100, 40), 4267, "POINT"),
            new Literal(1000.0, LiteralType.Number));

        var result = new DuckDbSqlFilterTranslator().Translate(filter, CreateNad27PointResource());

        Assert.Contains(
            "ST_Distance_Spheroid(ST_FlipCoordinates(ST_Transform(\"geometry\", 'EPSG:4267', 'EPSG:4326', always_xy := true)), ST_FlipCoordinates(ST_Transform(ST_GeomFromWKB(",
            result.Sql,
            StringComparison.Ordinal);
        Assert.Contains(", 'EPSG:4267', 'EPSG:4326', always_xy := true)", result.Sql, StringComparison.Ordinal);
        Assert.Contains("<= ", result.Sql, StringComparison.Ordinal);
    }

    private static MetadataV2Resource CreateNad27PointResource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-nad27", Name = "nad27-stations" },
            Type = MetadataV2ResourceType.FeatureDataset,
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = new MetadataV2SpatialReference
                {
                    Srid = 4267,
                    Crs = "EPSG:4267",
                    IsGeographic = true,
                },
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "geometry",
            },
            SchemaFields =
            [
                new MetadataV2Field { Name = "geometry", Type = MetadataV2FieldType.Geometry },
            ],
        };

    private static byte[] CreatePointWkb(double x, double y)
    {
        var wkb = new byte[21];
        wkb[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(wkb.AsSpan(1, 4), 1);
        BinaryPrimitives.WriteDoubleLittleEndian(wkb.AsSpan(5, 8), x);
        BinaryPrimitives.WriteDoubleLittleEndian(wkb.AsSpan(13, 8), y);
        return wkb;
    }

    private static MetadataV2Resource CreateResource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-parcels", Name = "parcels" },
            Type = MetadataV2ResourceType.FeatureDataset,
            SchemaFields =
            [
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
            ],
        };
}
