// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Styling;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

[Trait("Component", "Admin")]
public sealed class MapLibreStyleNormalizerTests
{
    private static readonly StyleLayerDescriptor _layer = new StyleLayerDescriptor(7, "Test Layer", MetadataV2GeometryType.Point);

    [UnitTest]
    public void TryNormalize_WithMinimalLayer_AddsHonuaSourceAndSourceLayer()
    {
        var style = ParseJson("""
        {
            "version": 8,
            "layers": [
                {
                    "id": "custom-points",
                    "type": "circle",
                    "paint": {
                        "circle-color": "#ff0000",
                        "circle-radius": 6
                    }
                }
            ]
        }
        """);

        var normalized = MapLibreStyleNormalizer.TryNormalize(style, _layer, out var normalizedJson, out var error);

        normalized.Should().BeTrue(error);
        using var document = JsonDocument.Parse(normalizedJson);
        var root = document.RootElement;
        var sourceId = StyleDefaults.GetSourceId(_layer);
        root.GetProperty("sources").GetProperty(sourceId).GetProperty("type").GetString().Should().Be("vector");
        var layer = root.GetProperty("layers")[0];
        layer.GetProperty("source").GetString().Should().Be(sourceId);
        layer.GetProperty("source-layer").GetString().Should().Be(StyleDefaults.SourceLayerName);
    }

    [UnitTest]
    public void TryNormalize_WithSupportedMatchExpression_ReturnsTrue()
    {
        var style = ParseJson("""
        {
            "version": 8,
            "layers": [
                {
                    "id": "categorized-points",
                    "type": "circle",
                    "paint": {
                        "circle-color": ["match", ["to-string", ["get", "kind"]], "park", "#00ff00", "road", "#ff0000", "#000000"],
                        "circle-radius": ["case", ["has", "rank"], 8, 4]
                    }
                }
            ]
        }
        """);

        var normalized = MapLibreStyleNormalizer.TryNormalize(style, _layer, out _, out var error);

        normalized.Should().BeTrue(error);
    }

    [Theory]
    [InlineData("""
    {
        "version": 8,
        "layers": [
            {
                "type": "circle",
                "paint": { "circle-color": "#ff0000" }
            }
        ]
    }
    """)]
    [InlineData("""
    {
        "version": 8,
        "layers": [
            {
                "id": "missing-type",
                "paint": { "circle-color": "#ff0000" }
            }
        ]
    }
    """)]
    [InlineData("""
    {
        "version": 8,
        "layers": [
            {
                "id": "bad-source-layer",
                "type": "circle",
                "source": "layer-7",
                "source-layer": "wrong-layer",
                "paint": { "circle-color": "#ff0000" }
            }
        ]
    }
    """)]
    [InlineData("""
    {
        "version": 8,
        "layers": [
            {
                "id": "bad-paint-property",
                "type": "circle",
                "paint": { "circle-colour": "#ff0000" }
            }
        ]
    }
    """)]
    [InlineData("""
    {
        "version": 8,
        "layers": [
            {
                "id": "bad-expression",
                "type": "circle",
                "paint": { "circle-color": ["not-an-operator", ["get", "kind"], "#ff0000", "#000000"] }
            }
        ]
    }
    """)]
    [InlineData("""
    {
        "version": 8,
        "layers": [
            {
                "id": "bad-opacity",
                "type": "circle",
                "paint": { "circle-opacity": 2 }
            }
        ]
    }
    """)]
    public void TryNormalize_WithInvalidStyle_ReturnsFalse(string styleJson)
    {
        var style = ParseJson(styleJson);

        var normalized = MapLibreStyleNormalizer.TryNormalize(style, _layer, out _, out var error);

        normalized.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    public void TryNormalize_WithVectorSourceLayerMissingSourceLayer_ReturnsFalse()
    {
        var style = ParseJson("""
        {
            "version": 8,
            "sources": {
                "external": {
                    "type": "vector",
                    "tiles": ["/tiles/external/{z}/{x}/{y}.mvt"]
                }
            },
            "layers": [
                {
                    "id": "external-layer",
                    "type": "circle",
                    "source": "external",
                    "paint": { "circle-color": "#ff0000" }
                },
                {
                    "id": "honua-layer",
                    "type": "circle",
                    "source": "layer-7",
                    "paint": { "circle-color": "#00ff00" }
                }
            ]
        }
        """);

        var normalized = MapLibreStyleNormalizer.TryNormalize(style, _layer, out _, out var error);

        normalized.Should().BeFalse();
        error.Should().Contain("source-layer");
    }

    private static JsonElement ParseJson(string json)
        => JsonSerializer.Deserialize<JsonElement>(json);
}
