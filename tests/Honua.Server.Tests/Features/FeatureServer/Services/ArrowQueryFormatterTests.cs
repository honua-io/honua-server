// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.FeatureServer.Services;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using GeometryType = Honua.Core.Features.Catalog.Domain.GeometryType;

namespace Honua.Server.Tests.Features.FeatureServer.Services;

public sealed class ArrowQueryFormatterTests
{
    private readonly ArrowQueryFormatter _sut = new();

    [Fact]
    public void FormatAsArrow_WithAttributesOnly_ReturnsExpectedColumns()
    {
        var layer = CreateLayer(
            [
                new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false),
                new FieldDefinition("name", FieldType.String, 255),
                new FieldDefinition("category", FieldType.String, 255),
                new FieldDefinition("shape", FieldType.Geometry, Nullable: true)
            ],
            GeometryType.Point);

        var feature = Feature.Create(
            7,
            CreatePointWkb(-157.8, 21.3),
            new Dictionary<string, object?>
            {
                ["objectid"] = 7L,
                ["name"] = "Honolulu",
                ["category"] = "City"
            }.ToImmutableDictionary());

        var result = QueryResult<Feature>.Create(1, [feature]);

        var (payload, contentType) = _sut.FormatAsArrow(
            result,
            layer,
            returnGeometry: false,
            outputSrid: null,
            outFields: null);

        contentType.Should().Be("application/vnd.apache.arrow.stream");
        payload.Should().NotBeEmpty();

        using var stream = new MemoryStream(payload);
        using var reader = new ArrowStreamReader(stream);
        using var batch = reader.ReadNextRecordBatch();

        batch.Should().NotBeNull();
        batch!.Length.Should().Be(1);
        batch.ColumnCount.Should().Be(3);
        batch.Schema.FieldsList.Select(field => field.Name).Should().Contain(["objectid", "name", "category"]);

        var objectIdColumn = batch.Column("objectid").Should().BeOfType<Int64Array>().Subject;
        objectIdColumn.GetValue(0).Should().Be(7);

        var nameColumn = batch.Column("name").Should().BeOfType<StringArray>().Subject;
        nameColumn.GetString(0, Encoding.UTF8).Should().Be("Honolulu");
    }

    [Fact]
    public void FormatAsArrow_WithGeometry_IncludesGeoArrowMetadata()
    {
        var layer = CreateLayer(
            [
                new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false),
                new FieldDefinition("name", FieldType.String, 255),
                new FieldDefinition("shape", FieldType.Geometry, Nullable: false)
            ],
            GeometryType.Point);

        var geometry = CreatePointWkb(-157.8, 21.3);
        var feature = Feature.Create(
            5,
            geometry,
            new Dictionary<string, object?>
            {
                ["objectid"] = 5L,
                ["name"] = "Ala Moana"
            }.ToImmutableDictionary());
        var result = QueryResult<Feature>.Create(1, [feature]);

        var (payload, _) = _sut.FormatAsArrow(
            result,
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            outFields: null);

        using var stream = new MemoryStream(payload);
        using var reader = new ArrowStreamReader(stream);
        using var batch = reader.ReadNextRecordBatch();

        batch.Should().NotBeNull();
        batch!.Schema.Metadata.Should().ContainKey("honua_format").WhoseValue.Should().Be("query_arrow");
        batch.Schema.Metadata.Should().ContainKey("honua_srid").WhoseValue.Should().Be("4326");
        batch.Schema.Metadata.Should().ContainKey("geo");

        var geometryField = batch.Schema.GetFieldByName("shape");
        geometryField.Should().NotBeNull();
        geometryField.Metadata.Should().ContainKey("ARROW:extension:name").WhoseValue.Should().Be("geoarrow.wkb");
        geometryField.Metadata.Should().ContainKey("ARROW:extension:metadata");

        var geometryColumn = batch.Column("shape").Should().BeOfType<BinaryArray>().Subject;
        geometryColumn.IsNull(0).Should().BeFalse();
        geometryColumn.GetBytes(0).ToArray().Should().Equal(geometry);
    }

    [Fact]
    public void FormatAsArrow_WithOutFields_ExcludesNonRequestedAttributes()
    {
        var layer = CreateLayer(
            [
                new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false),
                new FieldDefinition("name", FieldType.String, 255),
                new FieldDefinition("status", FieldType.String, 64),
                new FieldDefinition("shape", FieldType.Geometry, Nullable: true)
            ],
            GeometryType.Point);

        var feature = Feature.Create(
            11,
            CreatePointWkb(0, 0),
            new Dictionary<string, object?>
            {
                ["objectid"] = 11L,
                ["name"] = "test",
                ["status"] = "active"
            }.ToImmutableDictionary());

        var result = QueryResult<Feature>.Create(1, [feature]);

        var (payload, _) = _sut.FormatAsArrow(
            result,
            layer,
            returnGeometry: false,
            outputSrid: null,
            outFields: ["name"]);

        using var stream = new MemoryStream(payload);
        using var reader = new ArrowStreamReader(stream);
        using var batch = reader.ReadNextRecordBatch();

        batch.Should().NotBeNull();
        batch!.ColumnCount.Should().Be(2);
        batch.Schema.FieldsList.Select(field => field.Name).Should().ContainInOrder("objectid", "name");
    }

    private static LayerDefinition CreateLayer(FieldDefinition[] fields, GeometryType geometryType) =>
        new(
            Id: 0,
            Name: "Test Layer",
            Description: "Test layer for Arrow formatter tests",
            GeometryType: geometryType,
            SpatialReference: SpatialReference.Create(4326),
            Fields: fields);

    private static byte[] CreatePointWkb(double x, double y)
    {
        var writer = new WKBWriter();
        return writer.Write(new Point(x, y));
    }
}
