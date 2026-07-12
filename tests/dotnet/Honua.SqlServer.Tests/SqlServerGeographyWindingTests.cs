// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.SqlServer.Features.FeatureStore.Services;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.SqlServer.Tests;

/// <summary>
/// Verifies that SQL Server geography filter WKB is normalized to CCW-exterior ring orientation
/// (left-hand rule) so clockwise-exterior Esri polygons select the intended region instead of the
/// complement (#2741).
/// </summary>
public class SqlServerGeographyWindingTests
{
    private const int TestLayerId = 7;

    private static readonly IReadOnlyList<string> _attributeColumns = ["name"];

    private static readonly GeometryFactory _factory =
        NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    // A simple unit square. Listed counter-clockwise (right-hand rule / CCW exterior).
    private static readonly Coordinate[] _ccwRing =
    [
        new(0, 0),
        new(1, 0),
        new(1, 1),
        new(0, 1),
        new(0, 0),
    ];

    [Fact]
    public void NormalizeToCcwExterior_ClockwiseExterior_ProducesCcwExterior()
    {
        var cwWkb = WritePolygonWkb(reverseShell: true);

        var normalized = SqlServerGeographyWinding.NormalizeToCcwExterior(cwWkb);

        var polygon = (Polygon)new WKBReader().Read(normalized);
        Assert.True(Orientation.IsCCW(polygon.ExteriorRing.CoordinateSequence));
    }

    [Fact]
    public void NormalizeToCcwExterior_ClockwiseAndCounterClockwise_ProduceIdenticalWkb()
    {
        var cwWkb = WritePolygonWkb(reverseShell: true);
        var ccwWkb = WritePolygonWkb(reverseShell: false);

        // The two inputs describe the same region with opposite winding; after normalization the
        // serialized WKB must be byte-for-byte identical.
        var normalizedCw = SqlServerGeographyWinding.NormalizeToCcwExterior(cwWkb);
        var normalizedCcw = SqlServerGeographyWinding.NormalizeToCcwExterior(ccwWkb);

        Assert.Equal(normalizedCcw, normalizedCw);
    }

    [Fact]
    public void NormalizeToCcwExterior_AlreadyCcwExterior_ReturnsUnchangedBytes()
    {
        var ccwWkb = WritePolygonWkb(reverseShell: false);

        var normalized = SqlServerGeographyWinding.NormalizeToCcwExterior(ccwWkb);

        Assert.Equal(ccwWkb, normalized);
    }

    [Fact]
    public void NormalizeToCcwExterior_NonPolygonalGeometry_ReturnsUnchangedBytes()
    {
        var point = _factory.CreatePoint(new Coordinate(3, 4));
        var pointWkb = new WKBWriter().Write(point);

        var normalized = SqlServerGeographyWinding.NormalizeToCcwExterior(pointWkb);

        Assert.Equal(pointWkb, normalized);
    }

    [Fact]
    public void NormalizeToCcwExterior_UnparseableWkb_ReturnsInputUnchanged()
    {
        var malformed = new byte[] { 0xFF };

        var normalized = SqlServerGeographyWinding.NormalizeToCcwExterior(malformed);

        Assert.Same(malformed, normalized);
    }

    [Fact]
    public void BuildSelectQuery_GeographyMapping_ClockwiseAndCounterClockwiseFilter_ProduceSameParameter()
    {
        var mapping = BuildGeographyMapping();
        var cwQuery = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create(WritePolygonWkb(reverseShell: true), SpatialRelationship.Intersects, srid: 4326)
        };
        var ccwQuery = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create(WritePolygonWkb(reverseShell: false), SpatialRelationship.Intersects, srid: 4326)
        };

        var cwResult = SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, cwQuery, _attributeColumns);
        var ccwResult = SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, ccwQuery, _attributeColumns);

        Assert.Equal((byte[])ccwResult.WhereParameters[0], (byte[])cwResult.WhereParameters[0]);
    }

    [Fact]
    public void BuildSelectQuery_GeometryMapping_DoesNotReorientFilterWkb()
    {
        // The planar geometry type is orientation-insensitive, so the clockwise WKB must reach the
        // parameter list untouched.
        var mapping = BuildGeometryMapping();
        var cwWkb = WritePolygonWkb(reverseShell: true);
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create(cwWkb, SpatialRelationship.Intersects, srid: 4326)
        };

        var result = SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Equal(cwWkb, (byte[])result.WhereParameters[0]);
    }

    private static byte[] WritePolygonWkb(bool reverseShell)
    {
        var coordinates = reverseShell ? Reverse(_ccwRing) : _ccwRing;
        var polygon = _factory.CreatePolygon(coordinates);
        return new WKBWriter().Write(polygon);
    }

    private static Coordinate[] Reverse(Coordinate[] ring)
    {
        var reversed = new Coordinate[ring.Length];
        for (var i = 0; i < ring.Length; i++)
        {
            reversed[i] = ring[ring.Length - 1 - i];
        }

        return reversed;
    }

    private static SqlServerLayerMapping BuildGeographyMapping()
        => BuildMapping(SqlServerGeometryColumnType.Geography);

    private static SqlServerLayerMapping BuildGeometryMapping()
        => BuildMapping(SqlServerGeometryColumnType.Geometry);

    private static SqlServerLayerMapping BuildMapping(SqlServerGeometryColumnType columnType)
    {
        var providerOptions = new Dictionary<string, string>
        {
            [SqlServerProviderOptions.GeometryType] = columnType == SqlServerGeometryColumnType.Geography ? "geography" : "geometry"
        };

        var storage = new LayerStorageMapping(
            TableName: "parcels",
            SchemaName: "dbo",
            CatalogName: null,
            DatabaseName: null,
            PrimaryKeyColumn: "objectid",
            GeometryColumn: "shape",
            StorageSrid: 4326,
            ProviderOptions: providerOptions);

        return SqlServerLayerMapping.FromStorage(TestLayerId, storage);
    }
}
