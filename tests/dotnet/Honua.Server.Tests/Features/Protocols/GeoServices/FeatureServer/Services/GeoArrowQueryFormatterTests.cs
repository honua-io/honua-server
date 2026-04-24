// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using GeometryType = Honua.Core.Features.Catalog.Domain.GeometryType;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class GeoArrowQueryFormatterTests
{
    [Fact]
    public async Task FormatAsGeoArrowAsync_WithFeatures_WritesReadableArrowStreamWithGeoMetadata()
    {
        var layer = CreateLayer(
            new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false),
            new FieldDefinition("name", FieldType.String, 255),
            new FieldDefinition("created", FieldType.DateTime));
        var feature = Feature.Create(
            1,
            CreatePointWkb(-157.8583, 21.3069),
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["name"] = "Honolulu Harbor",
                ["created"] = new DateTimeOffset(2024, 01, 02, 03, 04, 05, TimeSpan.Zero)
            }.ToImmutableDictionary());

        var (payload, contentType) = await GeoArrowQueryFormatter.FormatAsGeoArrowAsync(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            geometryLimits: new GeometryLimits());

        contentType.Should().Be("application/vnd.apache.arrow.stream");

        using var stream = new MemoryStream(payload);
        using var reader = new ArrowStreamReader(stream);
        var batch = await reader.ReadNextRecordBatchAsync();

        batch.Should().NotBeNull();
        batch!.Length.Should().Be(1);

        // Verify schema fields
        var schema = reader.Schema;
        schema.FieldsList.Select(f => f.Name)
            .Should().Equal("objectid", "name", "created", "geometry");

        // Verify GeoArrow extension metadata on geometry field
        var geometryField = schema.GetFieldByName("geometry");
        geometryField.Metadata.Should().ContainKey("ARROW:extension:name");
        geometryField.Metadata["ARROW:extension:name"].Should().Be("geoarrow.wkb");
        geometryField.Metadata.Should().ContainKey("ARROW:extension:metadata");

        // Verify schema-level geo metadata
        schema.Metadata.Should().ContainKey("geo");
        using var geoDoc = JsonDocument.Parse(schema.Metadata["geo"]);
        geoDoc.RootElement.GetProperty("primary_column").GetString().Should().Be("geometry");
        geoDoc.RootElement.GetProperty("version").GetString().Should().Be("1.1.0");

        // Verify attribute values
        var nameArray = batch.Column("name") as StringArray;
        nameArray.Should().NotBeNull();
        nameArray!.GetString(0).Should().Be("Honolulu Harbor");

        // Verify geometry is readable WKB
        var geometryArray = batch.Column("geometry") as BinaryArray;
        geometryArray.Should().NotBeNull();
        var wkb = geometryArray!.GetBytes(0).ToArray();
        var point = new WKBReader().Read(wkb);
        point.Should().BeOfType<Point>();
        ((Point)point).X.Should().BeApproximately(-157.8583, 0.0001);
        ((Point)point).Y.Should().BeApproximately(21.3069, 0.0001);
    }

    [Fact]
    public async Task FormatAsGeoArrowAsync_EmptyResult_PreservesRequestedSchema()
    {
        var layer = CreateLayer(
            new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false),
            new FieldDefinition("name", FieldType.String, 255),
            new FieldDefinition("created", FieldType.DateTime));

        var (payload, _) = await GeoArrowQueryFormatter.FormatAsGeoArrowAsync(
            QueryResult<Feature>.Empty(),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            geometryLimits: new GeometryLimits(),
            outFields: ["name", "created"]);

        using var stream = new MemoryStream(payload);
        using var reader = new ArrowStreamReader(stream);

        // Schema should be present even with no data
        reader.Schema.FieldsList.Select(f => f.Name)
            .Should().Equal("objectid", "name", "created", "geometry");

        var batch = await reader.ReadNextRecordBatchAsync();
        batch.Should().NotBeNull();
        batch!.Length.Should().Be(0);
    }

    [Fact]
    public async Task FormatAsGeoArrowAsync_AppliesGeometryPrecisionAndDimensionFiltering()
    {
        var layer = CreateLayer(new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false));
        var feature = Feature.Create(
            1,
            CreatePointWkbWithZm(1.2349, 2.3451, 3.4567, 4.5678),
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());

        var (payload, _) = await GeoArrowQueryFormatter.FormatAsGeoArrowAsync(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            geometryLimits: new GeometryLimits { MaxCoordinatePrecision = 2 });

        using var stream = new MemoryStream(payload);
        using var reader = new ArrowStreamReader(stream);
        var batch = await reader.ReadNextRecordBatchAsync();

        var geometryArray = batch!.Column("geometry") as BinaryArray;
        var wkb = geometryArray!.GetBytes(0).ToArray();
        var geometry = new WKBReader().Read(wkb);
        var point = geometry.Should().BeOfType<Point>().Subject;

        point.X.Should().Be(1.23);
        point.Y.Should().Be(2.35);
        double.IsNaN(point.Coordinate.Z).Should().BeTrue();
        double.IsNaN(point.Coordinate.M).Should().BeTrue();
    }

    [Fact]
    public async Task FormatAsGeoArrowAsync_NullGeometry_WritesNullInGeometryColumn()
    {
        var layer = CreateLayer(new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false));
        var feature = Feature.Create(
            1,
            null,
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());

        var (payload, _) = await GeoArrowQueryFormatter.FormatAsGeoArrowAsync(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            geometryLimits: new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ArrowStreamReader(stream);
        var batch = await reader.ReadNextRecordBatchAsync();

        var geometryArray = batch!.Column("geometry") as BinaryArray;
        geometryArray!.IsNull(0).Should().BeTrue();
    }

    [Fact]
    public async Task FormatAsGeoArrowAsync_FieldTypeMappings_CorrectArrowTypes()
    {
        var layer = CreateLayer(
            new FieldDefinition("id", FieldType.BigInteger, Nullable: false),
            new FieldDefinition("count", FieldType.Integer),
            new FieldDefinition("rating", FieldType.Float),
            new FieldDefinition("area", FieldType.Double),
            new FieldDefinition("active", FieldType.Boolean),
            new FieldDefinition("label", FieldType.String, 100));

        var feature = Feature.Create(
            1,
            CreatePointWkb(0, 0),
            new Dictionary<string, object?>
            {
                ["id"] = 42L,
                ["count"] = 10,
                ["rating"] = 4.5f,
                ["area"] = 123.456,
                ["active"] = true,
                ["label"] = "test"
            }.ToImmutableDictionary());

        var (payload, _) = await GeoArrowQueryFormatter.FormatAsGeoArrowAsync(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            geometryLimits: new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ArrowStreamReader(stream);
        var batch = await reader.ReadNextRecordBatchAsync();

        reader.Schema.GetFieldByName("id").DataType.Should().BeOfType<Int64Type>();
        reader.Schema.GetFieldByName("count").DataType.Should().BeOfType<Int32Type>();
        reader.Schema.GetFieldByName("rating").DataType.Should().BeOfType<FloatType>();
        reader.Schema.GetFieldByName("area").DataType.Should().BeOfType<DoubleType>();
        reader.Schema.GetFieldByName("active").DataType.Should().BeOfType<BooleanType>();
        reader.Schema.GetFieldByName("label").DataType.Should().BeOfType<StringType>();
        reader.Schema.GetFieldByName("geometry").DataType.Should().BeOfType<BinaryType>();

        // Verify values round-trip
        ((Int64Array)batch!.Column("id")).GetValue(0).Should().Be(42L);
        ((Int32Array)batch.Column("count")).GetValue(0).Should().Be(10);
        ((FloatArray)batch.Column("rating")).GetValue(0).Should().Be(4.5f);
        ((DoubleArray)batch.Column("area")).GetValue(0).Should().Be(123.456);
        ((BooleanArray)batch.Column("active")).GetValue(0).Should().BeTrue();
        ((StringArray)batch.Column("label")).GetString(0).Should().Be("test");
    }

    [Fact]
    public async Task FormatAsGeoArrowAsync_ExtensionMetadata_ContainsGeometryTypesAndOmitsCrsFor4326()
    {
        var layer = CreateLayer(new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false));
        var feature = Feature.Create(
            1,
            CreatePointWkb(0, 0),
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());

        var (payload, _) = await GeoArrowQueryFormatter.FormatAsGeoArrowAsync(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            geometryLimits: new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ArrowStreamReader(stream);

        var geometryField = reader.Schema.GetFieldByName("geometry");
        var extensionMetadata = geometryField.Metadata["ARROW:extension:metadata"];
        using var doc = JsonDocument.Parse(extensionMetadata);

        doc.RootElement.GetProperty("geometry_types").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("Point");

        // EPSG:4326 omits CRS key (implies OGC:CRS84), matching GeoParquet convention
        doc.RootElement.TryGetProperty("crs", out _).Should().BeFalse(
            "CRS key should be omitted for EPSG:4326 (implies OGC:CRS84 per GeoParquet 1.1.0 spec)");
    }

    [Fact]
    public async Task FormatAsGeoArrowAsync_WithReturnZ_IncludesZSuffix()
    {
        var layer = CreateLayer(new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false));
        var feature = Feature.Create(
            1,
            CreatePointWkbWithZm(1.0, 2.0, 3.0, double.NaN),
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());

        var (payload, _) = await GeoArrowQueryFormatter.FormatAsGeoArrowAsync(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: true,
            returnM: false,
            geometryLimits: new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ArrowStreamReader(stream);

        var extensionMetadata = reader.Schema.GetFieldByName("geometry")
            .Metadata["ARROW:extension:metadata"];
        using var doc = JsonDocument.Parse(extensionMetadata);
        doc.RootElement.GetProperty("geometry_types").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("Point Z");

        // Schema-level geo metadata should also show Z
        using var geoDoc = JsonDocument.Parse(reader.Schema.Metadata["geo"]);
        var columns = geoDoc.RootElement.GetProperty("columns");
        var geomCol = columns.GetProperty("geometry");
        geomCol.GetProperty("geometry_types").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("Point Z");
    }

    [Fact]
    public async Task FormatAsGeoArrowAsync_WithoutGeometry_OmitsGeometryColumn()
    {
        var layer = CreateLayer(
            new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false),
            new FieldDefinition("name", FieldType.String, 255));
        var feature = Feature.Create(
            1,
            CreatePointWkb(0, 0),
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["name"] = "test"
            }.ToImmutableDictionary());

        var (payload, _) = await GeoArrowQueryFormatter.FormatAsGeoArrowAsync(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: false,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            geometryLimits: new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ArrowStreamReader(stream);

        reader.Schema.FieldsList.Select(f => f.Name)
            .Should().Equal("objectid", "name");

        // When no geometry is included, schema metadata should either be null or not contain "geo"
        if (reader.Schema.Metadata != null)
        {
            reader.Schema.Metadata.Should().NotContainKey("geo");
        }
    }

    [Fact]
    public async Task FormatAsGeoArrowAsync_RuntimeFields_IncludedInSchema()
    {
        var layer = CreateLayer(new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false));
        // Simulate a KNN result with a runtime "distance" attribute not in the layer schema
        var feature = Feature.Create(
            1,
            CreatePointWkb(0, 0),
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["distance"] = 42.5
            }.ToImmutableDictionary());

        var (payload, _) = await GeoArrowQueryFormatter.FormatAsGeoArrowAsync(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            geometryLimits: new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ArrowStreamReader(stream);
        var batch = await reader.ReadNextRecordBatchAsync();

        reader.Schema.FieldsList.Select(f => f.Name)
            .Should().Contain("distance", "runtime-computed fields should appear in schema");

        var distanceArray = batch!.Column("distance") as DoubleArray;
        distanceArray.Should().NotBeNull();
        distanceArray!.GetValue(0).Should().Be(42.5);
    }

    [Fact]
    public async Task FormatAsGeoArrowAsync_EmptyResult_EmitsEmptyGeometryTypes()
    {
        var layer = CreateLayer(
            new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false));

        var (payload, _) = await GeoArrowQueryFormatter.FormatAsGeoArrowAsync(
            QueryResult<Feature>.Empty(),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            geometryLimits: new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ArrowStreamReader(stream);

        // Extension metadata on geometry field should have empty geometry_types
        var geometryField = reader.Schema.GetFieldByName("geometry");
        var extMeta = geometryField.Metadata["ARROW:extension:metadata"];
        using var extDoc = JsonDocument.Parse(extMeta);
        extDoc.RootElement.GetProperty("geometry_types").GetArrayLength().Should().Be(0,
            "empty results must emit geometry_types: [] per GeoParquet 1.1.0 §4.1");

        // Schema-level geo metadata should also have empty geometry_types
        using var geoDoc = JsonDocument.Parse(reader.Schema.Metadata["geo"]);
        geoDoc.RootElement.GetProperty("columns").GetProperty("geometry")
            .GetProperty("geometry_types").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task FormatAsGeoArrowAsync_WithNon4326GeometrySrid_ThrowsInvalidOperation()
    {
        var layer = CreateLayer(
            new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false));

        var act = async () => await GeoArrowQueryFormatter.FormatAsGeoArrowAsync(
            QueryResult<Feature>.Empty(),
            layer,
            returnGeometry: true,
            outputSrid: 3857,
            returnZ: false,
            returnM: false,
            geometryLimits: new GeometryLimits());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*GeoArrow*EPSG:4326*3857*");
    }

    [Fact]
    public async Task FormatAsGeoArrowAsync_WithReturnMTrue_ThrowsInvalidOperation()
    {
        var layer = CreateLayer(
            new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false));

        var feature = Feature.Create(
            1,
            CreatePointWkbWithZm(1.0, 2.0, 3.0, 4.0),
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L
            }.ToImmutableDictionary());

        var act = async () => await GeoArrowQueryFormatter.FormatAsGeoArrowAsync(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: true,
            returnM: true,
            geometryLimits: new GeometryLimits());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*GeoArrow*returnM=true*XY and XYZ geometries*");
    }

    private static LayerDefinition CreateLayer(params FieldDefinition[] fields)
        => new(
            Id: 1,
            Name: "harbors",
            Description: "Harbors",
            GeometryType: GeometryType.Point,
            SpatialReference: SpatialReference.WGS84,
            Fields:
            [
                .. fields,
                new FieldDefinition("shape", FieldType.Geometry, Nullable: true)
            ]);

    private static byte[] CreatePointWkb(double x, double y)
        => new WKBWriter().Write(new Point(x, y) { SRID = 4326 });

    private static byte[] CreatePointWkbWithZm(double x, double y, double z, double m)
        => new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: true, emitM: true)
            .Write(new Point(new CoordinateZM(x, y, z, m)) { SRID = 4326 });
}
