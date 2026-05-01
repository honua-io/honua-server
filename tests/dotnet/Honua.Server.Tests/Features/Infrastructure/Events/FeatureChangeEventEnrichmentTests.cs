// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Infrastructure.Events;
using Honua.TestKit.Attributes;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Infrastructure.Events;

public sealed class FeatureChangeEventEnrichmentTests
{
    [UnitTest]
    public void FromFeature_WithNestedJsonAttributes_SerializesPropertiesJson()
    {
        var attributes = ImmutableDictionary<string, object?>.Empty
            .Add(
                "metadata",
                new Dictionary<string, object?>
                {
                    ["priority"] = 2L,
                    ["tags"] = new object?[] { "alpha", 7L }
                });

        var feature = Feature.Create(42, geometry: null, attributes);

        var (_, propertiesJson) = FeatureChangeEventEnrichment.FromFeature(feature);

        Assert.NotNull(propertiesJson);

        using var document = JsonDocument.Parse(propertiesJson!);
        var metadata = document.RootElement.GetProperty("metadata");
        Assert.Equal(2L, metadata.GetProperty("priority").GetInt64());
        Assert.Equal("alpha", metadata.GetProperty("tags")[0].GetString());
        Assert.Equal(7L, metadata.GetProperty("tags")[1].GetInt64());
    }

    [UnitTest]
    public void FromFeature_WithSupportedRuntimeAttributeTypes_SerializesPropertiesJson()
    {
        var identifier = Guid.Parse("5f9a6f86-89af-4f5a-8a09-5216265fc056");
        var payload = new byte[] { 1, 2, 3, 4 };
        var attributes = ImmutableDictionary<string, object?>.Empty
            .Add("featureId", identifier)
            .Add("effectiveDate", new DateOnly(2026, 3, 31))
            .Add("windowStart", new TimeOnly(9, 30, 0))
            .Add("duration", TimeSpan.FromMinutes(15))
            .Add("payload", payload);

        var feature = Feature.Create(99, geometry: null, attributes);

        var (_, propertiesJson) = FeatureChangeEventEnrichment.FromFeature(feature);

        Assert.NotNull(propertiesJson);

        using var document = JsonDocument.Parse(propertiesJson!);
        var root = document.RootElement;

        Assert.Equal(identifier.ToString(), root.GetProperty("featureId").GetString());
        Assert.Equal("2026-03-31", root.GetProperty("effectiveDate").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("windowStart").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("duration").ValueKind);
        Assert.Equal(Convert.ToBase64String(payload), root.GetProperty("payload").GetString());
    }

    [UnitTest]
    public void FromFeatureSnapshot_WhenWkbHasSrid_EmitsGeometryJsonAndSrid()
    {
        var point = new Point(-157.8583, 21.3069) { SRID = 4326 };
        var wkb = new WKBWriter(ByteOrder.LittleEndian, handleSRID: true).Write(point);
        var feature = Feature.Create(7, geometry: wkb, attributes: ImmutableDictionary<string, object?>.Empty);

        var enrichment = FeatureChangeEventEnrichment.FromFeatureSnapshot(feature);

        Assert.NotNull(enrichment.GeometryEnvelope);
        Assert.NotNull(enrichment.GeometryJson);
        Assert.Equal(4326, enrichment.GeometrySrid);
    }

    [UnitTest]
    public void FromFeatureSnapshot_WhenWkbHasNoSrid_OmitsGeometryJsonToPreserveCrsInvariant()
    {
        // WKB without SRID metadata (handleSRID: false). Geometry coordinates
        // alone are ambiguous to clients, so the enrichment must drop the JSON
        // while keeping the envelope for broadcast-time bbox filter evaluation.
        var point = new Point(-157.8583, 21.3069);
        var wkb = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false).Write(point);
        var feature = Feature.Create(8, geometry: wkb, attributes: ImmutableDictionary<string, object?>.Empty);

        var enrichment = FeatureChangeEventEnrichment.FromFeatureSnapshot(feature);

        Assert.NotNull(enrichment.GeometryEnvelope);
        Assert.Null(enrichment.GeometryJson);
        Assert.Null(enrichment.GeometrySrid);
    }
}
