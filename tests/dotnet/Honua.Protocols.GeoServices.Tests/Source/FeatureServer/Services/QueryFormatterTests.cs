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
    public async Task FormatQueryResultAsync_WithJsonReturnCentroid_OnPolygonLayer_EmitsCentroidWithoutGeometry()
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
                    CreatePolygonGeometry(),
                    ImmutableDictionary<string, object?>.Empty.Add("name", "parcel"))
            ]);

        var (response, contentType) = await formatter.FormatQueryResultAsync(
            result,
            CreatePolygonLayer(),
            format: "json",
            returnGeometry: false,
            outputSrid: null,
            returnZ: true,
            returnM: true,
            geometryPrecision: null,
            maxAllowableOffset: null,
            returnCentroid: true);

        contentType.Should().Be("application/json");
        var queryResponse = response.Should().BeOfType<QueryResponse>().Subject;
        var feature = queryResponse.Features.Should().ContainSingle().Subject;
        feature.Geometry.Should().BeNull();
        feature.Centroid.Should().NotBeNull();
        feature.Centroid!.X.Should().BeApproximately(0.5, 0.0001);
        feature.Centroid.Y.Should().BeApproximately(0.5, 0.0001);

        var json = JsonSerializer.Serialize(feature, FeatureServerJsonContext.Default.GeoServicesFeature);
        json.Should().Contain("\"centroid\"");
        json.Should().NotContain("\"geometry\"");
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
        // GeoJSON properties mirror the f=json attributes (lowercase objectid only);
        // the synthetic uppercase OBJECTID alias is intentionally suppressed (#1518).
        geoJson.Features[0].Properties.Should().NotContainKey("OBJECTID");
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

    [Fact]
    public async Task FormatQueryResultAsync_GeoJson_MultipartPolygon_EmitsMultiPolygonNotGeometryCollection()
    {
        // Regression (#1824): a multipart Esri geometry surfaces as an NTS GeometryCollection
        // of single-part polygons; RFC 7946 requires the homogeneous form to be tagged
        // MultiPolygon, not GeometryCollection.
        var geoJson = await FormatGeoJsonGeometryAsync(
            CreateGeometryCollectionWkb(CreatePolygon(0, 0), CreatePolygon(10, 10)),
            CreatePolygonLayer());

        geoJson.Features.Should().HaveCount(1);
        geoJson.Features[0].Geometry!.Type.Should().Be("MultiPolygon");
        geoJson.Features[0].Geometry!.Geometries.Should().BeNull("a Multi* geometry has coordinates, not nested geometries");
    }

    [Fact]
    public async Task FormatQueryResultAsync_GeoJson_MultipartPolyline_EmitsMultiLineString()
    {
        var geoJson = await FormatGeoJsonGeometryAsync(
            CreateGeometryCollectionWkb(CreateLine(0, 0), CreateLine(5, 5)),
            CreateResource("polyline-layer", MetadataV2GeometryType.LineString,
                new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false }));

        geoJson.Features[0].Geometry!.Type.Should().Be("MultiLineString");
    }

    [Fact]
    public async Task FormatQueryResultAsync_GeoJson_MultipartPoint_EmitsMultiPoint()
    {
        var geoJson = await FormatGeoJsonGeometryAsync(
            CreateGeometryCollectionWkb(new Point(0, 0), new Point(3, 4)),
            CreateResource("multipoint-layer", MetadataV2GeometryType.MultiPoint,
                new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false }));

        geoJson.Features[0].Geometry!.Type.Should().Be("MultiPoint");
    }

    [Fact]
    public async Task FormatQueryResultAsync_GeoJson_SinglePartHoledPolygon_StaysPolygon()
    {
        // A single-part holed polygon must remain a Polygon (no false-positive Multi*).
        var shell = new LinearRing([
            new Coordinate(0, 0), new Coordinate(10, 0), new Coordinate(10, 10),
            new Coordinate(0, 10), new Coordinate(0, 0)
        ]);
        var hole = new LinearRing([
            new Coordinate(3, 3), new Coordinate(3, 6), new Coordinate(6, 6),
            new Coordinate(6, 3), new Coordinate(3, 3)
        ]);
        var holed = new Polygon(shell, [hole]) { SRID = 4326 };

        var geoJson = await FormatGeoJsonGeometryAsync(new WKBWriter().Write(holed), CreatePolygonLayer());

        geoJson.Features[0].Geometry!.Type.Should().Be("Polygon");
    }

    private async Task<GeoJsonFeatureSet> FormatGeoJsonGeometryAsync(byte[] wkb, MetadataV2Resource resource)
    {
        var limitsOptions = Options.Create(new LimitsOptions());
        var formatter = new QueryFormatter(
            limitsOptions,
            new PbfQueryFormatter(limitsOptions),
            NullLogger<QueryFormatter>.Instance);

        var feature = Feature.Create(
            1,
            wkb,
            ImmutableDictionary<string, object?>.Empty.Add("objectid", 1L));

        var (response, _) = await formatter.FormatQueryResultAsync(
            QueryResult<Feature>.Create(1, [feature]),
            resource,
            format: "geojson",
            returnGeometry: true,
            outputSrid: null,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null);

        return response.Should().BeOfType<GeoJsonFeatureSet>().Subject;
    }

    private static Polygon CreatePolygon(double originX, double originY)
    {
        var ring = new LinearRing(
        [
            new Coordinate(originX, originY),
            new Coordinate(originX + 1, originY),
            new Coordinate(originX + 1, originY + 1),
            new Coordinate(originX, originY + 1),
            new Coordinate(originX, originY)
        ]);
        return new Polygon(ring) { SRID = 4326 };
    }

    private static LineString CreateLine(double originX, double originY)
    {
        var coordinates = new[]
        {
            new Coordinate(originX, originY),
            new Coordinate(originX + 1, originY + 1),
            new Coordinate(originX + 2, originY)
        };
        return new LineString(coordinates) { SRID = 4326 };
    }

    private static byte[] CreateGeometryCollectionWkb(params Geometry[] parts)
    {
        var collection = new GeometryCollection(parts) { SRID = 4326 };
        return new WKBWriter().Write(collection);
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

    private static MetadataV2Resource CreatePolygonLayer()
        => CreateResource(
            "polygon-test-layer",
            MetadataV2GeometryType.Polygon,
            new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Length = 128 });

    private static MetadataV2Resource CreatePointResource(string name, params MetadataV2Field[] fields)
        => CreateResource(name, MetadataV2GeometryType.Point, fields);

    private static MetadataV2Resource CreateResource(
        string name,
        MetadataV2GeometryType geometryType,
        params MetadataV2Field[] fields)
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
                GeometryType = geometryType,
                PrimaryGeometryField = "shape"
            }
        };

    private static byte[] CreatePointGeometry(double x, double y, int srid)
    {
        var writer = new WKBWriter();
        var point = new Point(x, y) { SRID = srid };
        return writer.Write(point);
    }

    private static byte[] CreatePolygonGeometry()
    {
        var writer = new WKBWriter();
        var ring = new LinearRing(
            [
                new Coordinate(0, 0),
                new Coordinate(1, 0),
                new Coordinate(1, 1),
                new Coordinate(0, 1),
                new Coordinate(0, 0)
            ]);
        return writer.Write(new Polygon(ring) { SRID = 4326 });
    }

    private static byte[] CreateMeasuredPointGeometry(double x, double y, double z, double m)
    {
        var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: true, emitM: true);
        var point = new Point(new CoordinateZM(x, y, z, m)) { SRID = 4326 };
        return writer.Write(point);
    }
}
