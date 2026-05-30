// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Common;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

public sealed class OgcFeaturesRawGeoJsonTests
{
    [Fact]
    public void CreateRawFeatureCollectionPayload_UsesConfiguredIdAttribute()
    {
        var resource = CreateResource(
            new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.Integer, Nullable = false });
        var feature = RawGeoJsonFeature.Create(
            id: 987,
            geometryGeoJson: "{\"type\":\"Point\",\"coordinates\":[1,2]}",
            propertiesJson: "{\"id\":123,\"category\":\"park\"}");

        var payload = OgcFeaturesQueryHandler.CreateRawFeatureCollectionPayload(
            [feature],
            resource,
            ImmutableArray<Link>.Empty,
            numberMatched: null);

        using var document = JsonDocument.Parse(payload);
        var rawFeature = document.RootElement.GetProperty("features")[0];
        rawFeature.GetProperty("id").GetInt64().Should().Be(123);
        rawFeature.GetProperty("properties").TryGetProperty("id", out _).Should().BeFalse();
        rawFeature.GetProperty("properties").GetProperty("category").GetString().Should().Be("park");
    }

    [Fact]
    public void CreateRawFeatureCollectionPayload_UsesProjectedRawPublicIdAndFiltersProperties()
    {
        var resource = CreateResource(
            new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.Integer, Nullable = false });
        var feature = RawGeoJsonFeature.Create(
            id: 987,
            geometryGeoJson: "{\"type\":\"Point\",\"coordinates\":[1,2]}",
            publicIdJson: "123",
            propertiesJson: "{\"category\":\"park\",\"internal_secret\":\"hidden\"}");

        var payload = OgcFeaturesQueryHandler.CreateRawFeatureCollectionPayload(
            [feature],
            resource,
            ImmutableArray<Link>.Empty,
            numberMatched: null);

        using var document = JsonDocument.Parse(payload);
        var rawFeature = document.RootElement.GetProperty("features")[0];
        rawFeature.GetProperty("id").GetInt64().Should().Be(123);
        rawFeature.GetProperty("properties").TryGetProperty("id", out _).Should().BeFalse();
        rawFeature.GetProperty("properties").GetProperty("category").GetString().Should().Be("park");
        rawFeature.GetProperty("properties").TryGetProperty("internal_secret", out _).Should().BeFalse();
    }

    [Fact]
    public void CreateRawFeatureCollectionPayload_FallsBackToObjectIdWhenPublicIdAttributeMissing()
    {
        var resource = CreateResource(
            new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.Integer, Nullable = false });
        var feature = RawGeoJsonFeature.Create(
            id: 987,
            geometryGeoJson: "{\"type\":\"Point\",\"coordinates\":[1,2]}",
            propertiesJson: "{\"category\":\"park\"}");

        var payload = OgcFeaturesQueryHandler.CreateRawFeatureCollectionPayload(
            [feature],
            resource,
            ImmutableArray<Link>.Empty,
            numberMatched: null);

        using var document = JsonDocument.Parse(payload);
        var rawFeature = document.RootElement.GetProperty("features")[0];
        rawFeature.GetProperty("id").GetInt64().Should().Be(987);
    }

    [Fact]
    public void CreateRawPointFeatureCollectionPayload_WritesPointGeometryWithoutGeoJsonParsing()
    {
        var resource = CreateResource(
            new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.Integer, Nullable = false });
        var feature = RawGeoServicesFeature.Create(
            id: 987,
            attributesJson: "{\"id\":123,\"category\":\"park\"}",
            x: 1.25,
            y: 2.5);

        var payload = OgcFeaturesQueryHandler.CreateRawPointFeatureCollectionPayload(
            [feature],
            resource,
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
    public void CreateRawPointFeatureCollectionPayload_UsesProjectedRawPublicIdAndFiltersAttributes()
    {
        var resource = CreateResource(
            new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.Integer, Nullable = false });
        var feature = RawGeoServicesFeature.Create(
            id: 987,
            publicIdJson: "123",
            attributesJson: "{\"category\":\"park\",\"internal_secret\":\"hidden\"}",
            x: 1.25,
            y: 2.5);

        var payload = OgcFeaturesQueryHandler.CreateRawPointFeatureCollectionPayload(
            [feature],
            resource,
            ImmutableArray<Link>.Empty,
            numberMatched: null);

        using var document = JsonDocument.Parse(payload);
        var rawFeature = document.RootElement.GetProperty("features")[0];
        rawFeature.GetProperty("id").GetInt64().Should().Be(123);
        rawFeature.GetProperty("properties").TryGetProperty("id", out _).Should().BeFalse();
        rawFeature.GetProperty("properties").GetProperty("category").GetString().Should().Be("park");
        rawFeature.GetProperty("properties").TryGetProperty("internal_secret", out _).Should().BeFalse();
    }

    private static MetadataV2Resource CreateResource(params MetadataV2Field[] fields)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "bench_points", Name = "bench_points" },
            Type = MetadataV2ResourceType.FeatureDataset,
            SchemaFields =
            [
                .. fields,
                new MetadataV2Field { Name = "category", Type = MetadataV2FieldType.String },
            ],
            Spatial = new MetadataV2ResourceSpatial
            {
                GeometryType = MetadataV2GeometryType.Point,
                SpatialReference = new MetadataV2SpatialReference
                {
                    Srid = 4326,
                    Crs = "EPSG:4326",
                    IsGeographic = true,
                },
            },
        };
}
