// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.Ogc.Classic;
using Xunit;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wmts;

/// <summary>
/// Verifies feature-info attribute serialization using only the production JSON context.
/// </summary>
public sealed class WmtsFeatureInfoSerializationTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("decimal", "12.345678901234567890123456789")]
    [InlineData("float", "1.25")]
    [InlineData("guid", "\"01234567-89ab-cdef-0123-456789abcdef\"")]
    [InlineData("bytes", "\"AAH+/w==\"")]
    [InlineData("date", "\"2026-09-08\"")]
    [InlineData("datetime", "\"2026-09-08T12:34:56Z\"")]
    [InlineData("offset", "\"2026-09-08T12:34:56-10:00\"")]
    [InlineData("int", "42")]
    [InlineData("long", "9223372036854775807")]
    [InlineData("double", "1.25")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("string", "\"café 東京\"")]
    [InlineData("null", "null")]
    [InlineData("json", "{\"nested\":{\"value\":1},\"list\":[true,null]}")]
    public void GetFeatureInfo_SupportedAttribute_SerializesWithoutReflectionFallback(string attributeType, string expectedJson)
    {
        using var expected = JsonDocument.Parse(expectedJson);
        object? value = attributeType switch
        {
            "decimal" => 12.345678901234567890123456789m,
            "float" => 1.25f,
            "guid" => new Guid("01234567-89ab-cdef-0123-456789abcdef"),
            "bytes" => new byte[] { 0, 1, 254, 255 },
            "date" => new DateOnly(2026, 9, 8),
            "datetime" => new DateTime(2026, 9, 8, 12, 34, 56, DateTimeKind.Utc),
            "offset" => new DateTimeOffset(2026, 9, 8, 12, 34, 56, TimeSpan.FromHours(-10)),
            "int" => 42,
            "long" => long.MaxValue,
            "double" => 1.25d,
            "true" => true,
            "false" => false,
            "string" => "café 東京",
            "null" => null,
            "json" => expected.RootElement.Clone(),
            _ => throw new ArgumentOutOfRangeException(nameof(attributeType))
        };
        var response = new OgcClassicFeatureInfoResponse
        {
            Features =
            [
                new OgcClassicFeatureInfoFeature
                {
                    Layer = "test-layer",
                    Attributes = new Dictionary<string, object?> { ["value"] = value }
                }
            ]
        };

        // Use the exact metadata passed to Results.Json by WMS/WMTS. This context
        // has no reflection resolver, even when the test host enables reflection.
        var json = JsonSerializer.Serialize(response, OgcClassicJsonContext.Default.OgcClassicFeatureInfoResponse);

        using var actual = JsonDocument.Parse(json);
        actual.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        var feature = actual.RootElement.GetProperty("features")[0];
        feature.GetProperty("type").GetString().Should().Be("Feature");
        feature.GetProperty("geometry").ValueKind.Should().Be(JsonValueKind.Null);
        feature.GetProperty("layer").GetString().Should().Be("test-layer");
        var properties = feature.GetProperty("properties");
        JsonElement.DeepEquals(properties.GetProperty("value"), expected.RootElement).Should().BeTrue();
        properties.GetRawText().Should().Be(feature.GetProperty("attributes").GetRawText());
    }
}
