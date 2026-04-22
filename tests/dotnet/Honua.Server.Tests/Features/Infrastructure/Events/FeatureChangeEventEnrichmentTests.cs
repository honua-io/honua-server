// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Infrastructure.Events;
using Honua.TestKit.Attributes;

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
}
