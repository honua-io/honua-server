// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Caching;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class OgcFeaturesRawGeoJsonTests
{
    [Fact]
    public void FeatureCollectionTimeStamp_UsesFixedWidthRfc3339UtcSerialization()
    {
        var value = new DateTimeOffset(2026, 8, 30, 12, 34, 56, TimeSpan.FromHours(-10))
            .AddTicks(1_230_000);
        var collection = new FeatureCollection
        {
            Features = [],
            NumberReturned = 0,
            TimeStamp = value
        };

        using var document = JsonDocument.Parse(
            JsonSerializer.SerializeToUtf8Bytes(collection, OgcJsonContext.Default.FeatureCollection));

        document.RootElement.GetProperty("timeStamp").GetString()
            .Should().Be("2026-08-30T22:34:56.1230000Z");
    }

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

    [UnitTest]
    public void CreateRawFeatureCollectionPayloadWithHashLength_StablePrefixExcludesVolatileTimeStamp()
    {
        var etagService = new ETagService();
        var resource = CreateResource(
            new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.Integer, Nullable = false });
        var feature = RawGeoJsonFeature.Create(
            id: 987,
            geometryGeoJson: "{\"type\":\"Point\",\"coordinates\":[1,2]}",
            propertiesJson: "{\"category\":\"park\"}");
        var context = new OgcFeaturesQueryHandler.RawPayloadContext(resource, ImmutableArray<Link>.Empty, 1);

        var first = OgcFeaturesQueryHandler.CreateRawFeatureCollectionPayloadWithHashLength([feature], context);
        Thread.Sleep(5);
        var second = OgcFeaturesQueryHandler.CreateRawFeatureCollectionPayloadWithHashLength([feature], context);

        var firstTag = OgcFeaturesQueryHandler.ComputeWeakETag(first.Bytes.Span[..first.StableHashLength], etagService);
        var secondTag = OgcFeaturesQueryHandler.ComputeWeakETag(second.Bytes.Span[..second.StableHashLength], etagService);

        first.Bytes.Span.SequenceEqual(second.Bytes.Span).Should().BeFalse();
        firstTag.Should().StartWith("W/\"");
        secondTag.Should().Be(firstTag);

        var mutated = RawGeoJsonFeature.Create(
            id: 987,
            geometryGeoJson: "{\"type\":\"Point\",\"coordinates\":[1,2]}",
            propertiesJson: "{\"category\":\"plaza\"}");
        var third = OgcFeaturesQueryHandler.CreateRawFeatureCollectionPayloadWithHashLength([mutated], context);
        var thirdTag = OgcFeaturesQueryHandler.ComputeWeakETag(third.Bytes.Span[..third.StableHashLength], etagService);
        thirdTag.Should().NotBe(firstTag);
    }

    [UnitTest]
    public void CreateRawPointFeatureCollectionPayloadWithHashLength_StablePrefixExcludesVolatileTimeStamp()
    {
        var etagService = new ETagService();
        var resource = CreateResource(
            new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.Integer, Nullable = false });
        var feature = RawGeoServicesFeature.Create(
            id: 987,
            attributesJson: "{\"category\":\"park\"}",
            x: 1.25,
            y: 2.5);
        var context = new OgcFeaturesQueryHandler.RawPayloadContext(resource, ImmutableArray<Link>.Empty, 1);

        var first = OgcFeaturesQueryHandler.CreateRawPointFeatureCollectionPayloadWithHashLength([feature], context);
        Thread.Sleep(5);
        var second = OgcFeaturesQueryHandler.CreateRawPointFeatureCollectionPayloadWithHashLength([feature], context);

        var firstTag = OgcFeaturesQueryHandler.ComputeWeakETag(first.Bytes.Span[..first.StableHashLength], etagService);
        var secondTag = OgcFeaturesQueryHandler.ComputeWeakETag(second.Bytes.Span[..second.StableHashLength], etagService);

        first.Bytes.Span.SequenceEqual(second.Bytes.Span).Should().BeFalse();
        firstTag.Should().StartWith("W/\"");
        secondTag.Should().Be(firstTag);

        var mutated = RawGeoServicesFeature.Create(
            id: 987,
            attributesJson: "{\"category\":\"plaza\"}",
            x: 1.25,
            y: 2.5);
        var third = OgcFeaturesQueryHandler.CreateRawPointFeatureCollectionPayloadWithHashLength([mutated], context);
        var thirdTag = OgcFeaturesQueryHandler.ComputeWeakETag(third.Bytes.Span[..third.StableHashLength], etagService);
        thirdTag.Should().NotBe(firstTag);
    }

    [UnitTest]
    public void ComputeStableFeatureCollectionETag_IgnoresTimeStampAndChangesOnMutation()
    {
        var etagService = new ETagService();
        var featureA = new GeoJsonFeature
        {
            Id = 1,
            Properties = new Dictionary<string, object?> { ["category"] = "park" },
        };
        var featureB = new GeoJsonFeature
        {
            Id = 1,
            Properties = new Dictionary<string, object?> { ["category"] = "plaza" },
        };

        var collection1 = new FeatureCollection
        {
            Features = [featureA],
            NumberMatched = 1,
            NumberReturned = 1,
            TimeStamp = new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero),
        };
        var collection2 = collection1 with
        {
            TimeStamp = new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero),
        };
        var collection3 = collection1 with
        {
            Features = [featureB],
        };

        var tag1 = OgcFeaturesQueryHandler.ComputeStableFeatureCollectionETag(collection1, etagService);
        var tag2 = OgcFeaturesQueryHandler.ComputeStableFeatureCollectionETag(collection2, etagService);
        var tag3 = OgcFeaturesQueryHandler.ComputeStableFeatureCollectionETag(collection3, etagService);

        tag1.Should().StartWith("W/\"");
        tag2.Should().Be(tag1);
        tag3.Should().NotBe(tag1);
    }

    private static MetadataV2Object CreateResource(params MetadataV2Field[] fields)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "bench_points", Name = "bench_points" },
            Type = MetadataV2ObjectType.FeatureDataset,
            SchemaFields =
            [
                .. fields,
                new MetadataV2Field { Name = "category", Type = MetadataV2FieldType.String },
            ],
            Spatial = new MetadataV2ObjectSpatial
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
