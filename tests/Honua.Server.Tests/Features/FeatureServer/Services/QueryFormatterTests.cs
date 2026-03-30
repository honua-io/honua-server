// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.FeatureServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.FeatureServer.Services;

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

    private static LayerDefinition CreatePointLayer()
        => new(
            7,
            "test-layer",
            null,
            Honua.Core.Features.Catalog.Domain.GeometryType.Point,
            SpatialReference.WGS84,
            [
                new FieldDefinition(FieldNames.ObjectId, FieldType.Integer, Nullable: false),
                new FieldDefinition("name", FieldType.String, Length: 128)
            ]);

    private static LayerDefinition CreateIdBackedPointLayer()
        => new(
            8,
            "id-backed-test-layer",
            null,
            Honua.Core.Features.Catalog.Domain.GeometryType.Point,
            SpatialReference.WGS84,
            [
                new FieldDefinition("id", FieldType.Integer, Nullable: false),
                new FieldDefinition("name", FieldType.String, Length: 128)
            ]);

    private static byte[] CreatePointGeometry(double x, double y, int srid)
    {
        var writer = new WKBWriter();
        var point = new Point(x, y) { SRID = srid };
        return writer.Write(point);
    }
}
