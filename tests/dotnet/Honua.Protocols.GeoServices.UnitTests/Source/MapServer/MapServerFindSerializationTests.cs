// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.GeoServices.MapServer.Models;
using Xunit;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

/// <summary>
/// Reproduces honua-server#1771 at the serialization layer: the published/AOT image
/// sets <c>JsonSerializerIsReflectionEnabledByDefault=false</c>, so the source-generated
/// <see cref="MapServerJsonContext"/> must contain a <c>JsonTypeInfo</c> for every runtime
/// CLR type that can land in a <see cref="FindResult.Attributes"/> <c>object</c> slot. The
/// integration tests run with reflection enabled (test default), which masks a missing
/// registration; these tests force source-generated-only metadata so the gap surfaces as a
/// failing test instead of a runtime 500 on the AOT image.
/// </summary>
public sealed class MapServerFindSerializationTests
{
    // Mirrors the published image: only source-generated metadata, no reflection fallback.
    private static readonly JsonSerializerOptions AotOptions = new()
    {
        TypeInfoResolver = MapServerJsonContext.Default,
    };

    [Fact]
    public void FindResponse_WithDecimalAttribute_SerializesWithoutReflectionFallback()
    {
        // A high-precision PostGIS numeric column materializes as System.Decimal
        // (JsonElementConverter.ConvertToScalar -> GetDecimal()).
        var response = BuildResponse(new Dictionary<string, object?>
        {
            ["objectid"] = 1,
            ["measure"] = 12.3456789012345678901234567890m,
        });

        var act = () => JsonSerializer.Serialize(response, MapServerJsonContext.Default.FindResponse);
        act.Should().NotThrow("decimal attribute values must be serializable on the AOT image");
    }

    [Fact]
    public void FindResponse_WithNestedJsonElementAttribute_SerializesWithoutReflectionFallback()
    {
        // A JSONB object/array attribute survives as a raw JsonElement.
        using var doc = JsonDocument.Parse("""{"nested":{"a":1},"list":[1,2,3]}""");
        var response = BuildResponse(new Dictionary<string, object?>
        {
            ["objectid"] = 1,
            ["payload"] = doc.RootElement.Clone(),
        });

        var act = () => JsonSerializer.Serialize(response, MapServerJsonContext.Default.FindResponse);
        act.Should().NotThrow("JsonElement attribute values must be serializable on the AOT image");
    }

    [Fact]
    public void FindResponse_WithScalarAttributes_RoundTrips()
    {
        var response = BuildResponse(new Dictionary<string, object?>
        {
            ["objectid"] = 1L,
            ["name"] = "Test Feature",
            ["ratio"] = 1.5d,
            ["flag"] = 1,
        });

        var json = JsonSerializer.Serialize(response, AotOptions.GetTypeInfo(typeof(FindResponse)));
        json.Should().Contain("Test Feature");
        json.Should().Contain("results");
    }

    private static FindResponse BuildResponse(Dictionary<string, object?> attributes) => new()
    {
        Results =
        [
            new FindResult
            {
                LayerId = 0,
                LayerName = "Layer",
                DisplayFieldName = "name",
                FoundFieldName = "name",
                Value = "Test Feature",
                Attributes = attributes,
                GeometryType = "esriGeometryPoint",
                Geometry = null,
            },
        ],
    };
}
