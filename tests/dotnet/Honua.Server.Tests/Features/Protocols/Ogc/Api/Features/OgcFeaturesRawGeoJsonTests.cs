// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Protocols.Ogc.Api.Features;
using Honua.Server.Features.Protocols.Ogc.Common;
using CatalogGeometryType = Honua.Core.Features.Catalog.Domain.GeometryType;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

public sealed class OgcFeaturesRawGeoJsonTests
{
    [Fact]
    public void CreateRawFeatureCollectionPayload_UsesConfiguredIdAttribute()
    {
        var layer = CreateLayer(new FieldDefinition("id", FieldType.Integer, Nullable: false));
        var feature = RawGeoJsonFeature.Create(
            id: 987,
            geometryGeoJson: "{\"type\":\"Point\",\"coordinates\":[1,2]}",
            propertiesJson: "{\"id\":123,\"category\":\"park\"}");

        var payload = OgcFeaturesQueryHandler.CreateRawFeatureCollectionPayload(
            [feature],
            layer,
            ImmutableArray<Link>.Empty,
            numberMatched: null);

        using var document = JsonDocument.Parse(payload);
        var rawFeature = document.RootElement.GetProperty("features")[0];
        rawFeature.GetProperty("id").GetInt64().Should().Be(123);
        rawFeature.GetProperty("properties").TryGetProperty("id", out _).Should().BeFalse();
        rawFeature.GetProperty("properties").GetProperty("category").GetString().Should().Be("park");
    }

    [Fact]
    public void CreateRawFeatureCollectionPayload_UsesProjectedRawPublicIdWithoutParsingProperties()
    {
        var layer = CreateLayer(new FieldDefinition("id", FieldType.Integer, Nullable: false));
        var feature = RawGeoJsonFeature.Create(
            id: 987,
            geometryGeoJson: "{\"type\":\"Point\",\"coordinates\":[1,2]}",
            publicIdJson: "123",
            propertiesJson: "{\"category\":\"park\"}");

        var payload = OgcFeaturesQueryHandler.CreateRawFeatureCollectionPayload(
            [feature],
            layer,
            ImmutableArray<Link>.Empty,
            numberMatched: null);

        using var document = JsonDocument.Parse(payload);
        var rawFeature = document.RootElement.GetProperty("features")[0];
        rawFeature.GetProperty("id").GetInt64().Should().Be(123);
        rawFeature.GetProperty("properties").TryGetProperty("id", out _).Should().BeFalse();
        rawFeature.GetProperty("properties").GetProperty("category").GetString().Should().Be("park");
    }

    [Fact]
    public void CreateRawFeatureCollectionPayload_FallsBackToObjectIdWhenPublicIdAttributeMissing()
    {
        var layer = CreateLayer(new FieldDefinition("id", FieldType.Integer, Nullable: false));
        var feature = RawGeoJsonFeature.Create(
            id: 987,
            geometryGeoJson: "{\"type\":\"Point\",\"coordinates\":[1,2]}",
            propertiesJson: "{\"category\":\"park\"}");

        var payload = OgcFeaturesQueryHandler.CreateRawFeatureCollectionPayload(
            [feature],
            layer,
            ImmutableArray<Link>.Empty,
            numberMatched: null);

        using var document = JsonDocument.Parse(payload);
        var rawFeature = document.RootElement.GetProperty("features")[0];
        rawFeature.GetProperty("id").GetInt64().Should().Be(987);
    }

    [Fact]
    public void CreateRawPointFeatureCollectionPayload_WritesPointGeometryWithoutGeoJsonParsing()
    {
        var layer = CreateLayer(new FieldDefinition("id", FieldType.Integer, Nullable: false));
        var feature = RawGeoServicesFeature.Create(
            id: 987,
            attributesJson: "{\"id\":123,\"category\":\"park\"}",
            x: 1.25,
            y: 2.5);

        var payload = OgcFeaturesQueryHandler.CreateRawPointFeatureCollectionPayload(
            [feature],
            layer,
            ImmutableArray<Link>.Empty,
            numberMatched: null);

        using var document = JsonDocument.Parse(payload);
        var rawFeature = document.RootElement.GetProperty("features")[0];
        rawFeature.GetProperty("id").GetInt64().Should().Be(123);
        rawFeature.GetProperty("geometry").GetProperty("type").GetString().Should().Be("Point");
        var coordinates = rawFeature.GetProperty("geometry").GetProperty("coordinates");
        coordinates[0].GetDouble().Should().Be(1.25);
        coordinates[1].GetDouble().Should().Be(2.5);
        rawFeature.GetProperty("properties").TryGetProperty("id", out _).Should().BeFalse();
        rawFeature.GetProperty("properties").GetProperty("category").GetString().Should().Be("park");
    }

    [Fact]
    public void CreateRawPointFeatureCollectionPayload_UsesProjectedRawPublicIdWithoutParsingAttributes()
    {
        var layer = CreateLayer(new FieldDefinition("id", FieldType.Integer, Nullable: false));
        var feature = RawGeoServicesFeature.Create(
            id: 987,
            publicIdJson: "123",
            attributesJson: "{\"category\":\"park\"}",
            x: 1.25,
            y: 2.5);

        var payload = OgcFeaturesQueryHandler.CreateRawPointFeatureCollectionPayload(
            [feature],
            layer,
            ImmutableArray<Link>.Empty,
            numberMatched: null);

        using var document = JsonDocument.Parse(payload);
        var rawFeature = document.RootElement.GetProperty("features")[0];
        rawFeature.GetProperty("id").GetInt64().Should().Be(123);
        rawFeature.GetProperty("properties").TryGetProperty("id", out _).Should().BeFalse();
        rawFeature.GetProperty("properties").GetProperty("category").GetString().Should().Be("park");
    }

    private static LayerDefinition CreateLayer(params FieldDefinition[] fields)
        => new(
            1,
            "bench_points",
            "Benchmark points",
            CatalogGeometryType.Point,
            SpatialReference.WGS84,
            [
                .. fields,
                new FieldDefinition("category", FieldType.String)
            ]);
}
