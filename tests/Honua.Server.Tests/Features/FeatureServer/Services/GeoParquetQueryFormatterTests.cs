// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.FeatureServer.Services;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Parquet;
using Parquet.Schema;
using GeometryType = Honua.Core.Features.Catalog.Domain.GeometryType;

namespace Honua.Server.Tests.Features.FeatureServer.Services;

public sealed class GeoParquetQueryFormatterTests
{
    [Fact]
    public async Task FormatAsGeoParquetAsync_WithFeatures_WritesReadableParquetWithGeoMetadata()
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

        var (payload, contentType) = await GeoParquetQueryFormatter.FormatAsGeoParquetAsync(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            geometryLimits: new GeometryLimits());

        contentType.Should().Be("application/vnd.apache.parquet");
        using var stream = new MemoryStream(payload);
        using var reader = await ParquetReader.CreateAsync(stream);

        reader.Schema.GetDataFields().Select(field => field.Name)
            .Should().Equal("objectid", "name", "created", "geometry");
        reader.CustomMetadata.Should().ContainKey("geo");

        using var metadataDocument = JsonDocument.Parse(reader.CustomMetadata["geo"]);
        metadataDocument.RootElement.GetProperty("primary_column").GetString().Should().Be("geometry");

        var createdField = reader.Schema.FindDataField("created");
        createdField.Should().BeOfType<DateTimeDataField>();

        using var rowGroupReader = reader.OpenRowGroupReader(0);
        var createdColumn = await rowGroupReader.ReadColumnAsync(createdField!);
        createdColumn.Data.Should().BeAssignableTo<DateTime?[]>();
        ((DateTime?[])createdColumn.Data)[0].Should().Be(new DateTime(2024, 01, 02, 03, 04, 05, DateTimeKind.Utc));
    }

    [Fact]
    public async Task FormatAsGeoParquetAsync_EmptyResult_PreservesRequestedSchema()
    {
        var layer = CreateLayer(
            new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false),
            new FieldDefinition("name", FieldType.String, 255),
            new FieldDefinition("created", FieldType.DateTime));

        var (payload, _) = await GeoParquetQueryFormatter.FormatAsGeoParquetAsync(
            QueryResult<Feature>.Empty(),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            geometryLimits: new GeometryLimits(),
            outFields: ["name", "created"]);

        using var stream = new MemoryStream(payload);
        using var reader = await ParquetReader.CreateAsync(stream);

        reader.RowGroupCount.Should().Be(1);
        reader.Schema.GetDataFields().Select(field => field.Name)
            .Should().Equal("objectid", "name", "created", "geometry");

        using var rowGroupReader = reader.OpenRowGroupReader(0);
        var objectIdColumn = await rowGroupReader.ReadColumnAsync(reader.Schema.FindDataField("objectid")!);
        objectIdColumn.Data.Should().BeAssignableTo<long[]>();
        ((long[])objectIdColumn.Data).Should().BeEmpty();
    }

    [Fact]
    public async Task FormatAsGeoParquetAsync_AppliesGeometryPrecisionAndDimensionFiltering()
    {
        var layer = CreateLayer(new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false));
        var feature = Feature.Create(
            1,
            CreatePointWkbWithZm(1.2349, 2.3451, 3.4567, 4.5678),
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());

        var (payload, _) = await GeoParquetQueryFormatter.FormatAsGeoParquetAsync(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            geometryLimits: new GeometryLimits { MaxCoordinatePrecision = 2 });

        using var stream = new MemoryStream(payload);
        using var reader = await ParquetReader.CreateAsync(stream);
        using var rowGroupReader = reader.OpenRowGroupReader(0);
        var geometryColumn = await rowGroupReader.ReadColumnAsync(reader.Schema.FindDataField("geometry")!);
        var geometryBytes = ((byte[]?[])geometryColumn.Data)[0];

        geometryBytes.Should().NotBeNull();
        var geometry = new WKBReader().Read(geometryBytes!);
        var point = geometry.Should().BeOfType<Point>().Subject;

        point.X.Should().Be(1.23);
        point.Y.Should().Be(2.35);
        double.IsNaN(point.Coordinate.Z).Should().BeTrue();
        double.IsNaN(point.Coordinate.M).Should().BeTrue();
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
