// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class QueryFormatterTests
{
    [Fact]
    public async Task FormatQueryResultAsync_WithOutputSrid_SetsTopLevelSpatialReferenceOnly()
    {
        var limitsOptions = Options.Create(new LimitsOptions());
        var formatter = new QueryFormatter(
            limitsOptions,
            new PbfQueryFormatter(limitsOptions),
            NullLogger<QueryFormatter>.Instance);

        var result = QueryResult<Feature>.Create(
            1,
            [
                Feature.Create(
                    1,
                    CreatePointGeometry(1, 2, 4326),
                    ImmutableDictionary<string, object?>.Empty.Add("name", "alpha"))
            ]);

        var (response, contentType) = await formatter.FormatQueryResultAsync(
            result,
            CreatePointLayer(),
            format: "json",
            returnGeometry: true,
            outputSrid: 3857,
            returnZ: true,
            returnM: true,
            geometryPrecision: null,
            maxAllowableOffset: null);

        contentType.Should().Be("application/json");
        var queryResponse = response.Should().BeOfType<QueryResponse>().Subject;
        queryResponse.SpatialReference?.Wkid.Should().Be(3857);
        queryResponse.Features.Should().HaveCount(1);
        queryResponse.Features[0].Geometry?.SpatialReference.Should().BeNull();
    }

    [Fact]
    public async Task FormatQueryResultAsync_WithGeoJson_UsesSharedIdAndPropertyProjection()
    {
        var limitsOptions = Options.Create(new LimitsOptions());
        var formatter = new QueryFormatter(
            limitsOptions,
            new PbfQueryFormatter(limitsOptions),
            NullLogger<QueryFormatter>.Instance);

        var feature = Feature.Create(
            42,
            geometry: null,
            new Dictionary<string, object?>
            {
                ["name"] = "alpha",
                ["distance"] = 12.5
            }.ToImmutableDictionary());

        var (response, contentType) = await formatter.FormatQueryResultAsync(
            QueryResult<Feature>.Create(1, [feature]),
            CreatePointLayer(),
            format: "geojson",
            returnGeometry: false,
            outputSrid: null,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null,
            outFields: ["distance"]);

        contentType.Should().Be("application/geo+json");
        var geoJson = response.Should().BeOfType<GeoJsonFeatureSet>().Subject;
        geoJson.Features.Should().HaveCount(1);
        geoJson.Features[0].Id.Should().Be(42L);
        geoJson.Features[0].Properties.Should().Contain("objectid", 42L);
        geoJson.Features[0].Properties.Should().Contain("distance", 12.5);
        geoJson.Features[0].Properties.Should().NotContainKey("name");
        geoJson.Features[0].Properties.Should().NotContainKey("OBJECTID");
    }

    [Fact]
    public async Task FormatQueryResultAsync_WithGeoJson_AllFields_PreservesObjectIdAlias()
    {
        var limitsOptions = Options.Create(new LimitsOptions());
        var formatter = new QueryFormatter(
            limitsOptions,
            new PbfQueryFormatter(limitsOptions),
            NullLogger<QueryFormatter>.Instance);

        var feature = Feature.Create(
            42,
            geometry: null,
            new Dictionary<string, object?>
            {
                ["name"] = "alpha"
            }.ToImmutableDictionary());

        var (response, contentType) = await formatter.FormatQueryResultAsync(
            QueryResult<Feature>.Create(1, [feature]),
            CreatePointLayer(),
            format: "geojson",
            returnGeometry: false,
            outputSrid: null,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null);

        contentType.Should().Be("application/geo+json");
        var geoJson = response.Should().BeOfType<GeoJsonFeatureSet>().Subject;
        geoJson.Features.Should().HaveCount(1);
        geoJson.Features[0].Id.Should().Be(42L);
        geoJson.Features[0].Properties.Should().Contain("objectid", 42L);
        geoJson.Features[0].Properties.Should().Contain("OBJECTID", 42L);
        geoJson.Features[0].Properties.Should().Contain("name", "alpha");
    }

    [Fact]
    public async Task FormatQueryResultAsync_WithJson_AllFields_PreservesObjectIdAlias()
    {
        var limitsOptions = Options.Create(new LimitsOptions());
        var formatter = new QueryFormatter(
            limitsOptions,
            new PbfQueryFormatter(limitsOptions),
            NullLogger<QueryFormatter>.Instance);

        var feature = Feature.Create(
            42,
            geometry: null,
            new Dictionary<string, object?>
            {
                ["name"] = "alpha"
            }.ToImmutableDictionary());

        var (response, contentType) = await formatter.FormatQueryResultAsync(
            QueryResult<Feature>.Create(1, [feature]),
            CreatePointLayer(),
            format: "json",
            returnGeometry: false,
            outputSrid: null,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null);

        contentType.Should().Be("application/json");
        var queryResponse = response.Should().BeOfType<QueryResponse>().Subject;
        queryResponse.Features.Should().HaveCount(1);
        queryResponse.Features[0].Attributes.Should().Contain("objectid", 42L);
        queryResponse.Features[0].Attributes.Should().Contain("OBJECTID", 42L);
        queryResponse.Features[0].Attributes.Should().Contain("name", "alpha");
    }

    [Fact]
    public async Task FormatQueryResultAsync_WithDeclaredField_IncludesInJsonAndGeoJson()
    {
        var limitsOptions = Options.Create(new LimitsOptions());
        var formatter = new QueryFormatter(
            limitsOptions,
            new PbfQueryFormatter(limitsOptions),
            NullLogger<QueryFormatter>.Instance);

        var feature = Feature.Create(
            42,
            geometry: null,
            new Dictionary<string, object?>
            {
                ["objectid"] = 42L,
                ["name"] = "alpha",
                ["secret"] = "hidden"
            }.ToImmutableDictionary());
        var resource = CreatePointResourceWithExtraField();

        var (jsonResponse, jsonContentType) = await formatter.FormatQueryResultAsync(
            QueryResult<Feature>.Create(1, [feature]),
            resource,
            format: "json",
            returnGeometry: false,
            outputSrid: null,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null);

        jsonContentType.Should().Be("application/json");
        var queryResponse = jsonResponse.Should().BeOfType<QueryResponse>().Subject;
        queryResponse.Fields.Should().Contain(field => field.Name == "secret");
        queryResponse.Features.Should().ContainSingle();
        queryResponse.Features[0].Attributes.Should().Contain("secret", "hidden");

        var (geoJsonResponse, geoJsonContentType) = await formatter.FormatQueryResultAsync(
            QueryResult<Feature>.Create(1, [feature]),
            resource,
            format: "geojson",
            returnGeometry: false,
            outputSrid: null,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null);

        geoJsonContentType.Should().Be("application/geo+json");
        var geoJson = geoJsonResponse.Should().BeOfType<GeoJsonFeatureSet>().Subject;
        geoJson.Features.Should().ContainSingle();
        geoJson.Features[0].Properties.Should().Contain("secret", "hidden");
    }

    [Fact]
    public async Task FormatQueryResultAsync_WithGeoJsonAndMeasuredGeometry_DropsMOrdinate()
    {
        var limitsOptions = Options.Create(new LimitsOptions());
        var formatter = new QueryFormatter(
            limitsOptions,
            new PbfQueryFormatter(limitsOptions),
            NullLogger<QueryFormatter>.Instance);

        var result = QueryResult<Feature>.Create(
            1,
            [
                Feature.Create(
                    1,
                    CreateMeasuredPointGeometry(1, 2, 3, 4),
                    ImmutableDictionary<string, object?>.Empty.Add("name", "alpha"))
            ]);

        var (response, contentType) = await formatter.FormatQueryResultAsync(
            result,
            CreatePointLayer(),
            format: "geojson",
            returnGeometry: true,
            outputSrid: null,
            returnZ: true,
            returnM: true,
            geometryPrecision: null,
            maxAllowableOffset: null);

        contentType.Should().Be("application/geo+json");

        var json = JsonSerializer.Serialize(
            response,
            FeatureServerJsonContext.Default.GeoJsonFeatureSet);
        using var document = JsonDocument.Parse(json);
        var coordinates = document.RootElement.GetProperty("features")[0].GetProperty("geometry").GetProperty("coordinates");

        coordinates.GetArrayLength().Should().Be(3);
        coordinates[0].GetDouble().Should().Be(1);
        coordinates[1].GetDouble().Should().Be(2);
        coordinates[2].GetDouble().Should().Be(3);
    }

    [Fact]
    public async Task FormatQueryResultAsync_WithJson_OmitsNullExtentAndFalseDimensionFlags()
    {
        var limitsOptions = Options.Create(new LimitsOptions());
        var formatter = new QueryFormatter(
            limitsOptions,
            new PbfQueryFormatter(limitsOptions),
            NullLogger<QueryFormatter>.Instance);

        var result = QueryResult<Feature>.Create(
            1,
            [
                Feature.Create(
                    1,
                    CreatePointGeometry(1, 2, 4326),
                    ImmutableDictionary<string, object?>.Empty.Add("name", "alpha"))
            ]);

        var (response, contentType) = await formatter.FormatQueryResultAsync(
            result,
            CreatePointLayer(),
            format: "json",
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: true,
            returnM: true,
            geometryPrecision: null,
            maxAllowableOffset: null);

        contentType.Should().Be("application/json");

        var json = JsonSerializer.Serialize(
            response,
            FeatureServerJsonContext.Default.QueryResponse);

        using var document = JsonDocument.Parse(json);
        document.RootElement.TryGetProperty("extent", out _).Should().BeFalse();
        document.RootElement.TryGetProperty("hasZ", out _).Should().BeFalse();
        document.RootElement.TryGetProperty("hasM", out _).Should().BeFalse();
        var geometry = document.RootElement.GetProperty("features")[0].GetProperty("geometry");
        geometry.TryGetProperty("hasZ", out _).Should().BeFalse();
        geometry.TryGetProperty("hasM", out _).Should().BeFalse();
    }

    [Fact]
    public async Task FormatQueryResultAsync_WithJson_PointPrecision_RoundsCoordinates()
    {
        var limitsOptions = Options.Create(new LimitsOptions());
        var formatter = new QueryFormatter(
            limitsOptions,
            new PbfQueryFormatter(limitsOptions),
            NullLogger<QueryFormatter>.Instance);

        var result = QueryResult<Feature>.Create(
            1,
            [
                Feature.Create(
                    1,
                    CreatePointGeometry(1.1234567, 2.7654321, 4326),
                    ImmutableDictionary<string, object?>.Empty.Add("name", "alpha"))
            ]);

        var (response, contentType) = await formatter.FormatQueryResultAsync(
            result,
            CreatePointLayer(),
            format: "json",
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: true,
            returnM: true,
            geometryPrecision: 2,
            maxAllowableOffset: null);

        contentType.Should().Be("application/json");
        var queryResponse = response.Should().BeOfType<QueryResponse>().Subject;
        queryResponse.Features.Should().HaveCount(1);
        queryResponse.Features[0].Geometry!.X.Should().Be(1.12);
        queryResponse.Features[0].Geometry!.Y.Should().Be(2.77);
    }

    [Fact]
    public async Task FormatQueryResultAsync_WithJson_ExcludesUndeclaredAttributes()
    {
        var limitsOptions = Options.Create(new LimitsOptions());
        var formatter = new QueryFormatter(
            limitsOptions,
            new PbfQueryFormatter(limitsOptions),
            NullLogger<QueryFormatter>.Instance);

        var feature = Feature.Create(
            42,
            geometry: null,
            new Dictionary<string, object?>
            {
                ["id"] = 42L,
                ["name"] = "alpha",
                ["objectid"] = 999L
            }.ToImmutableDictionary());

        var (response, contentType) = await formatter.FormatQueryResultAsync(
            QueryResult<Feature>.Create(1, [feature]),
            CreateIdBackedPointLayer(),
            format: "json",
            returnGeometry: false,
            outputSrid: null,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null);

        contentType.Should().Be("application/json");
        var queryResponse = response.Should().BeOfType<QueryResponse>().Subject;
        var attributes = queryResponse.Features.Should().ContainSingle().Subject.Attributes;
        attributes.Should().Contain("id", 42L);
        attributes.Should().Contain("name", "alpha");
        attributes.Should().NotContainKey("objectid");
    }

    [Fact]
    public async Task FormatQueryResultAsync_WithJsonAndProjectedFields_PreservesPublicObjectId()
    {
        var limitsOptions = Options.Create(new LimitsOptions());
        var formatter = new QueryFormatter(
            limitsOptions,
            new PbfQueryFormatter(limitsOptions),
            NullLogger<QueryFormatter>.Instance);

        var feature = Feature.Create(
            42,
            geometry: null,
            new Dictionary<string, object?>
            {
                ["id"] = 101L,
                ["name"] = "alpha"
            }.ToImmutableDictionary());

        var (response, contentType) = await formatter.FormatQueryResultAsync(
            QueryResult<Feature>.Create(1, [feature]),
            CreateIdBackedPointLayer(),
            format: "json",
            returnGeometry: false,
            outputSrid: null,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null,
            outFields: ["name"]);

        contentType.Should().Be("application/json");
        var queryResponse = response.Should().BeOfType<QueryResponse>().Subject;
        queryResponse.ObjectIdFieldName.Should().Be("id");
        var attributes = queryResponse.Features.Should().ContainSingle().Subject.Attributes;
        attributes.Should().Contain("id", 101L);
        attributes.Should().Contain("name", "alpha");
        attributes.Should().NotContainKey("objectid");
    }

    [Fact]
    public async Task FormatQueryResultAsync_WithJson_StringIdAndNumericObjectId_UsesObjectIdField()
    {
        var limitsOptions = Options.Create(new LimitsOptions());
        var formatter = new QueryFormatter(
            limitsOptions,
            new PbfQueryFormatter(limitsOptions),
            NullLogger<QueryFormatter>.Instance);

        var feature = Feature.Create(
            42,
            geometry: null,
            new Dictionary<string, object?>
            {
                ["id"] = "alpha-1",
                ["objectid"] = 42L,
                ["name"] = "alpha"
            }.ToImmutableDictionary());

        var (response, contentType) = await formatter.FormatQueryResultAsync(
            QueryResult<Feature>.Create(1, [feature]),
            CreateStringIdPointLayer(),
            format: "json",
            returnGeometry: false,
            outputSrid: null,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null);

        contentType.Should().Be("application/json");
        var queryResponse = response.Should().BeOfType<QueryResponse>().Subject;
        queryResponse.ObjectIdFieldName.Should().Be(FieldNames.ObjectId);
        queryResponse.Fields.Should().Contain(field =>
            field.Name == "id" && field.Type == "esriFieldTypeString");
        queryResponse.Fields.Should().Contain(field =>
            field.Name == FieldNames.ObjectId && field.Type == "esriFieldTypeOID");

        var attributes = queryResponse.Features.Should().ContainSingle().Subject.Attributes;
        attributes.Should().Contain("id", "alpha-1");
        attributes.Should().Contain(FieldNames.ObjectId, 42L);
    }

    [Fact]
    public async Task FormatQueryResultAsync_WithJson_RuntimeDistance_IncludesFieldAndAttribute()
    {
        var limitsOptions = Options.Create(new LimitsOptions());
        var formatter = new QueryFormatter(
            limitsOptions,
            new PbfQueryFormatter(limitsOptions),
            NullLogger<QueryFormatter>.Instance);

        var feature = Feature.Create(
            42,
            geometry: null,
            new Dictionary<string, object?>
            {
                ["objectid"] = 42L,
                ["name"] = "alpha",
                ["distance"] = 12.5
            }.ToImmutableDictionary());

        var (response, contentType) = await formatter.FormatQueryResultAsync(
            QueryResult<Feature>.Create(1, [feature]),
            CreatePointLayer(),
            format: "json",
            returnGeometry: false,
            outputSrid: null,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null);

        contentType.Should().Be("application/json");
        var queryResponse = response.Should().BeOfType<QueryResponse>().Subject;
        queryResponse.Fields.Should().Contain(field => field.Name.Equals("distance", StringComparison.OrdinalIgnoreCase)
            && field.Type == "esriFieldTypeDouble");
        queryResponse.Features.Should().ContainSingle();
        queryResponse.Features[0].Attributes.Should().Contain("distance", 12.5);
    }

    private static MetadataV2Resource CreatePointLayer()
        => CreatePointResource(
            "test-layer",
            new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Length = 128 });

    private static MetadataV2Resource CreatePointResourceWithExtraField()
        => CreatePointResource(
            "field-test-layer",
            new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Length = 128 },
            new MetadataV2Field { Name = "secret", Type = MetadataV2FieldType.String, Length = 128 });

    private static MetadataV2Resource CreateIdBackedPointLayer()
        => CreatePointResource(
            "id-backed-test-layer",
            new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Length = 128 });

    private static MetadataV2Resource CreateStringIdPointLayer()
        => CreatePointResource(
            "string-id-test-layer",
            new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.String, Length = 64, Nullable = false },
            new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Length = 128 });

    private static MetadataV2Resource CreatePointResource(string name, params MetadataV2Field[] fields)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = name,
                Name = name
            },
            SchemaFields =
            [
                .. fields,
                new MetadataV2Field
                {
                    Name = "shape",
                    Type = MetadataV2FieldType.Geometry,
                    Nullable = true,
                    SemanticRoles = ["geometry.primary"]
                }
            ],
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "shape"
            }
        };

    private static byte[] CreatePointGeometry(double x, double y, int srid)
    {
        var writer = new WKBWriter();
        var point = new Point(x, y) { SRID = srid };
        return writer.Write(point);
    }

    private static byte[] CreateMeasuredPointGeometry(double x, double y, double z, double m)
    {
        var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: true, emitM: true);
        var point = new Point(new CoordinateZM(x, y, z, m)) { SRID = 4326 };
        return writer.Write(point);
    }
}
