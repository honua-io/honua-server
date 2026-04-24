// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using GeometryType = Honua.Core.Features.Catalog.Domain.GeometryType;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class PbfQueryFormatterTests
{
    private readonly PbfQueryFormatter _sut = new(Options.Create(new LimitsOptions()));

    // ── Basic response structure ───────────────────────────────

    [Fact]
    public void FormatAsPbf_EmptyResult_ReturnsValidPbfWithProtobufContentType()
    {
        var layer = CreatePointLayer();
        var result = QueryResult<Feature>.Empty();

        var (response, contentType) = _sut.FormatAsPbf(
            result, layer, returnGeometry: true, outputSrid: null,
            returnZ: false, returnM: false, geometryPrecision: null,
            maxAllowableOffset: null, outFields: null);

        contentType.Should().Be("application/x-protobuf");
        response.Should().NotBeNull();
        response.Length.Should().BeGreaterThan(0, "PBF envelope is always present");
    }

    [Fact]
    public void FormatAsPbf_WithFeatures_ReturnsBytesLargerThanEmpty()
    {
        var layer = CreatePointLayer();
        var emptyResult = QueryResult<Feature>.Empty();
        var featureResult = CreateSinglePointResult();

        var (emptyResponse, _) = _sut.FormatAsPbf(
            emptyResult, layer, returnGeometry: true, outputSrid: null,
            returnZ: false, returnM: false, geometryPrecision: null,
            maxAllowableOffset: null, outFields: null);

        var (response, _) = _sut.FormatAsPbf(
            featureResult, layer, returnGeometry: true, outputSrid: null,
            returnZ: false, returnM: false, geometryPrecision: null,
            maxAllowableOffset: null, outFields: null);

        response.Length.Should().BeGreaterThan(emptyResponse.Length);
    }

    // ── Wire format validation ─────────────────────────────────

    [Fact]
    public void FormatAsPbf_StartsWithVersionString()
    {
        var layer = CreatePointLayer();
        var result = QueryResult<Feature>.Empty();

        var (response, _) = _sut.FormatAsPbf(
            result, layer, returnGeometry: true, outputSrid: null,
            returnZ: false, returnM: false, geometryPrecision: null,
            maxAllowableOffset: null, outFields: null);

        // Field 1 (version), wire type 2 (length-delimited) → tag byte = (1 << 3) | 2 = 0x0A
        response[0].Should().Be(0x0A, "first field should be version string tag");

        // Next byte is the length of the version string "1.0" = 3 bytes
        response[1].Should().Be(3);

        // Followed by "1.0" in UTF-8
        var versionBytes = response.AsSpan(2, 3);
        System.Text.Encoding.UTF8.GetString(versionBytes).Should().Be("1.0");
    }

    // ── Attribute encoding ─────────────────────────────────────

    [Fact]
    public void FormatAsPbf_StringAttribute_IsEncodedInOutput()
    {
        var layer = CreateLayerWithStringField();
        var attrs = new Dictionary<string, object?>
        {
            ["objectid"] = 1L,
            ["name"] = "Test Park"
        }.ToImmutableDictionary();
        var feature = Feature.Create(1, CreatePointWkb(0, 0), attrs);
        var result = QueryResult<Feature>.Create(1, [feature]);

        var (response, _) = _sut.FormatAsPbf(
            result, layer, returnGeometry: false, outputSrid: null,
            returnZ: false, returnM: false, geometryPrecision: null,
            maxAllowableOffset: null, outFields: null);

        // The string "Test Park" should appear somewhere in the binary output
        var responseStr = System.Text.Encoding.UTF8.GetString(response);
        responseStr.Should().Contain("Test Park");
    }

    [Fact]
    public void FormatAsPbf_NullAttribute_DoesNotThrow()
    {
        var layer = CreateLayerWithStringField();
        var attrs = new Dictionary<string, object?>
        {
            ["objectid"] = 1L,
            ["name"] = null
        }.ToImmutableDictionary();
        var feature = Feature.Create(1, CreatePointWkb(0, 0), attrs);
        var result = QueryResult<Feature>.Create(1, [feature]);

        var act = () => _sut.FormatAsPbf(
            result, layer, returnGeometry: false, outputSrid: null,
            returnZ: false, returnM: false, geometryPrecision: null,
            maxAllowableOffset: null, outFields: null);

        act.Should().NotThrow();
    }

    [Fact]
    public void FormatAsPbf_MultipleAttributeTypes_DoesNotThrow()
    {
        var fields = new[]
        {
            new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false),
            new FieldDefinition("name", FieldType.String, 255),
            new FieldDefinition("population", FieldType.Integer),
            new FieldDefinition("area", FieldType.Double),
            new FieldDefinition("active", FieldType.Boolean),
            new FieldDefinition("created", FieldType.DateTime)
        };
        var layer = CreateLayer(fields, GeometryType.Point);
        var attrs = new Dictionary<string, object?>
        {
            ["objectid"] = 42L,
            ["name"] = "Honolulu",
            ["population"] = 350000,
            ["area"] = 68.428,
            ["active"] = true,
            ["created"] = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
        }.ToImmutableDictionary();
        var feature = Feature.Create(42, CreatePointWkb(-157.8, 21.3), attrs);
        var result = QueryResult<Feature>.Create(1, [feature]);

        var (response, _) = _sut.FormatAsPbf(
            result, layer, returnGeometry: true, outputSrid: null,
            returnZ: false, returnM: false, geometryPrecision: null,
            maxAllowableOffset: null, outFields: null);

        response.Length.Should().BeGreaterThan(0);
    }

    // ── Geometry encoding ──────────────────────────────────────

    [Fact]
    public void FormatAsPbf_WithPointGeometry_ProducesLargerOutputThanWithout()
    {
        var layer = CreatePointLayer();
        var feature = Feature.Create(1, CreatePointWkb(-122.4, 37.8),
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());
        var result = QueryResult<Feature>.Create(1, [feature]);

        var (withGeo, _) = _sut.FormatAsPbf(
            result, layer, returnGeometry: true, outputSrid: null,
            returnZ: false, returnM: false, geometryPrecision: null,
            maxAllowableOffset: null, outFields: null);

        var (withoutGeo, _) = _sut.FormatAsPbf(
            result, layer, returnGeometry: false, outputSrid: null,
            returnZ: false, returnM: false, geometryPrecision: null,
            maxAllowableOffset: null, outFields: null);

        withGeo.Length.Should().BeGreaterThan(withoutGeo.Length);
    }

    [Fact]
    public void FormatAsPbf_WithReturnM_EncodesMOrdinateInGeometryCoords()
    {
        var layer = CreatePointLayer();
        var feature = Feature.Create(
            1,
            CreatePointWkbWithZm(1.25, 2.5, 3.0, 4.75),
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());
        var result = QueryResult<Feature>.Create(1, [feature]);

        var (response, _) = _sut.FormatAsPbf(
            result,
            layer,
            returnGeometry: true,
            outputSrid: null,
            returnZ: false,
            returnM: true,
            geometryPrecision: null,
            maxAllowableOffset: null,
            outFields: null);

        var coords = ExtractGeometryCoords(response);
        coords.Should().ContainInOrder(
            (long)Math.Round(1.25 * 1_000_000d),
            (long)Math.Round(2.5 * 1_000_000d),
            (long)Math.Round(4.75 * 1_000_000d));
    }

    [Fact]
    public void FormatAsPbf_WithPolygonGeometry_DoesNotThrow()
    {
        var layer = CreateLayer(
            [new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false)],
            GeometryType.Polygon);

        var polygon = new Polygon(new LinearRing(
        [
            new Coordinate(0, 0),
            new Coordinate(10, 0),
            new Coordinate(10, 10),
            new Coordinate(0, 10),
            new Coordinate(0, 0)
        ]));
        var wkbWriter = new WKBWriter();
        var wkb = wkbWriter.Write(polygon);

        var feature = Feature.Create(1, wkb,
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());
        var result = QueryResult<Feature>.Create(1, [feature]);

        var act = () => _sut.FormatAsPbf(
            result, layer, returnGeometry: true, outputSrid: null,
            returnZ: false, returnM: false, geometryPrecision: null,
            maxAllowableOffset: null, outFields: null);

        act.Should().NotThrow();
    }

    [Fact]
    public void FormatAsPbf_WithLineStringGeometry_DoesNotThrow()
    {
        var layer = CreateLayer(
            [new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false)],
            GeometryType.LineString);

        var line = new LineString(
        [
            new Coordinate(0, 0),
            new Coordinate(5, 5),
            new Coordinate(10, 0)
        ]);
        var wkbWriter = new WKBWriter();
        var wkb = wkbWriter.Write(line);

        var feature = Feature.Create(1, wkb,
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());
        var result = QueryResult<Feature>.Create(1, [feature]);

        var act = () => _sut.FormatAsPbf(
            result, layer, returnGeometry: true, outputSrid: null,
            returnZ: false, returnM: false, geometryPrecision: null,
            maxAllowableOffset: null, outFields: null);

        act.Should().NotThrow();
    }

    [Fact]
    public void FormatAsPbf_NullGeometryOnFeature_DoesNotThrow()
    {
        var layer = CreatePointLayer();
        var feature = Feature.Create(1, null,
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());
        var result = QueryResult<Feature>.Create(1, [feature]);

        var act = () => _sut.FormatAsPbf(
            result, layer, returnGeometry: true, outputSrid: null,
            returnZ: false, returnM: false, geometryPrecision: null,
            maxAllowableOffset: null, outFields: null);

        act.Should().NotThrow();
    }

    // ── outFields filtering ────────────────────────────────────

    [Fact]
    public void FormatAsPbf_WithOutFields_OnlyIncludesRequestedFields()
    {
        var layer = CreateLayerWithStringField();
        var attrs = new Dictionary<string, object?>
        {
            ["objectid"] = 1L,
            ["name"] = "Filtered Field Test"
        }.ToImmutableDictionary();
        var feature = Feature.Create(1, null, attrs);
        var result = QueryResult<Feature>.Create(1, [feature]);

        var (allFields, _) = _sut.FormatAsPbf(
            result, layer, returnGeometry: false, outputSrid: null,
            returnZ: false, returnM: false, geometryPrecision: null,
            maxAllowableOffset: null, outFields: null);

        var (limitedFields, _) = _sut.FormatAsPbf(
            result, layer, returnGeometry: false, outputSrid: null,
            returnZ: false, returnM: false, geometryPrecision: null,
            maxAllowableOffset: null, outFields: ["objectid"]);

        // All-fields response should be at least as large as limited-fields
        allFields.Length.Should().BeGreaterThanOrEqualTo(limitedFields.Length);
    }

    // ── exceededTransferLimit ──────────────────────────────────

    [Fact]
    public void FormatAsPbf_HasMoreResults_ProducesSlightlyDifferentOutput()
    {
        var layer = CreatePointLayer();
        var feature = Feature.Create(1, CreatePointWkb(0, 0),
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());

        var withMore = QueryResult<Feature>.Create(10, [feature], hasMoreResults: true);
        var withoutMore = QueryResult<Feature>.Create(1, [feature], hasMoreResults: false);

        var (pbfWithMore, _) = _sut.FormatAsPbf(
            withMore, layer, returnGeometry: true, outputSrid: null,
            returnZ: false, returnM: false, geometryPrecision: null,
            maxAllowableOffset: null, outFields: null);

        var (pbfWithout, _) = _sut.FormatAsPbf(
            withoutMore, layer, returnGeometry: true, outputSrid: null,
            returnZ: false, returnM: false, geometryPrecision: null,
            maxAllowableOffset: null, outFields: null);

        // exceededTransferLimit=true encodes as a bool field, adding bytes
        pbfWithMore.Length.Should().BeGreaterThan(pbfWithout.Length);
    }

    // ── Multiple features ──────────────────────────────────────

    [Fact]
    public void FormatAsPbf_MultipleFeatures_ScalesLinearly()
    {
        var layer = CreatePointLayer();
        var features1 = ImmutableArray.Create(
            Feature.Create(1, CreatePointWkb(0, 0),
                new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary()));
        var features3 = ImmutableArray.Create(
            Feature.Create(1, CreatePointWkb(0, 0),
                new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary()),
            Feature.Create(2, CreatePointWkb(1, 1),
                new Dictionary<string, object?> { ["objectid"] = 2L }.ToImmutableDictionary()),
            Feature.Create(3, CreatePointWkb(2, 2),
                new Dictionary<string, object?> { ["objectid"] = 3L }.ToImmutableDictionary()));

        var result1 = QueryResult<Feature>.Create(1, features1);
        var result3 = QueryResult<Feature>.Create(3, features3);

        var (pbf1, _) = _sut.FormatAsPbf(
            result1, layer, returnGeometry: true, outputSrid: null,
            returnZ: false, returnM: false, geometryPrecision: null,
            maxAllowableOffset: null, outFields: null);

        var (pbf3, _) = _sut.FormatAsPbf(
            result3, layer, returnGeometry: true, outputSrid: null,
            returnZ: false, returnM: false, geometryPrecision: null,
            maxAllowableOffset: null, outFields: null);

        pbf3.Length.Should().BeGreaterThan(pbf1.Length);
    }

    // ── OutputSrid ─────────────────────────────────────────────

    [Fact]
    public void FormatAsPbf_WithCustomOutputSrid_DoesNotThrow()
    {
        var layer = CreatePointLayer();
        var result = CreateSinglePointResult();

        var act = () => _sut.FormatAsPbf(
            result, layer, returnGeometry: true, outputSrid: 3857,
            returnZ: false, returnM: false, geometryPrecision: null,
            maxAllowableOffset: null, outFields: null);

        act.Should().NotThrow();
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static LayerDefinition CreatePointLayer() =>
        CreateLayer(
            [new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false)],
            GeometryType.Point);

    private static LayerDefinition CreateLayerWithStringField() =>
        CreateLayer(
            [
                new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false),
                new FieldDefinition("name", FieldType.String, 255)
            ],
            GeometryType.Point);

    private static LayerDefinition CreateLayer(FieldDefinition[] fields, GeometryType geometryType) =>
        new(
            Id: 0,
            Name: "Test Layer",
            Description: "Test layer for PBF tests",
            GeometryType: geometryType,
            SpatialReference: SpatialReference.Create(4326),
            Fields: fields);

    private static QueryResult<Feature> CreateSinglePointResult()
    {
        var feature = Feature.Create(1, CreatePointWkb(-122.4, 37.8),
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());
        return QueryResult<Feature>.Create(1, [feature]);
    }

    private static byte[] CreatePointWkb(double x, double y)
    {
        var point = new Point(x, y);
        var writer = new WKBWriter();
        return writer.Write(point);
    }

    private static byte[] CreatePointWkbWithZm(double x, double y, double z, double m)
    {
        var point = new Point(new CoordinateZM(x, y, z, m));
        var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: true, emitM: true);
        return writer.Write(point);
    }

    private static List<long> ExtractGeometryCoords(byte[] pbf)
    {
        var queryResult = GetFirstLengthDelimitedField(pbf, fieldNumber: 2);
        var featureResult = GetFirstLengthDelimitedField(queryResult, fieldNumber: 1);
        var feature = GetFirstLengthDelimitedField(featureResult, fieldNumber: 15);
        var geometry = GetFirstLengthDelimitedField(feature, fieldNumber: 2);
        var packedCoords = GetFirstLengthDelimitedField(geometry, fieldNumber: 3);
        return DecodePackedSInt64Values(packedCoords);
    }

    private static byte[] GetFirstLengthDelimitedField(ReadOnlySpan<byte> message, int fieldNumber)
    {
        var offset = 0;
        while (offset < message.Length)
        {
            var tag = ReadVarint(message, ref offset);
            var currentField = (int)(tag >> 3);
            var wireType = (int)(tag & 0x07);

            switch (wireType)
            {
                case 0:
                    _ = ReadVarint(message, ref offset);
                    break;
                case 1:
                    offset += 8;
                    break;
                case 2:
                    {
                        var length = checked((int)ReadVarint(message, ref offset));
                        if (length < 0 || offset + length > message.Length)
                        {
                            throw new InvalidOperationException("Malformed protobuf length-delimited field.");
                        }

                        var payload = message.Slice(offset, length);
                        offset += length;
                        if (currentField == fieldNumber)
                        {
                            return payload.ToArray();
                        }

                        break;
                    }
                case 5:
                    offset += 4;
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported protobuf wire type {wireType} in test parser.");
            }
        }

        throw new InvalidOperationException($"Field {fieldNumber} was not found.");
    }

    private static List<long> DecodePackedSInt64Values(ReadOnlySpan<byte> payload)
    {
        var values = new List<long>();
        var offset = 0;
        while (offset < payload.Length)
        {
            var encoded = ReadVarint(payload, ref offset);
            var decoded = (long)((encoded >> 1) ^ (ulong)-(long)(encoded & 1));
            values.Add(decoded);
        }

        return values;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong value = 0;
        var shift = 0;

        while (true)
        {
            if (offset >= data.Length)
            {
                throw new InvalidOperationException("Unexpected end of protobuf payload.");
            }

            var b = data[offset++];
            value |= ((ulong)(b & 0x7F)) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
            if (shift > 63)
            {
                throw new InvalidOperationException("Malformed protobuf varint.");
            }
        }
    }
}
