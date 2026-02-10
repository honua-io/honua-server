// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Features.MapServer.Rendering;
using Honua.TestKit.Attributes;
using SkiaSharp;

namespace Honua.Server.Tests.Features.MapServer.Rendering;

/// <summary>
/// Tests for MapLibre style to Skia paint translation.
/// </summary>
[Trait("Component", "MapServer")]
public class StyleTranslatorTests
{
    private static readonly ImmutableDictionary<string, object?> _emptyProps =
        ImmutableDictionary<string, object?>.Empty;

    [UnitTest]
    public void ParseStyleLayers_NullJson_ReturnsEmpty()
    {
        var result = StyleTranslator.ParseStyleLayers(null);

        result.Should().BeEmpty();
    }

    [UnitTest]
    public void ParseStyleLayers_EmptyJson_ReturnsEmpty()
    {
        var result = StyleTranslator.ParseStyleLayers("");

        result.Should().BeEmpty();
    }

    [UnitTest]
    public void ParseStyleLayers_InvalidJson_ReturnsEmpty()
    {
        var result = StyleTranslator.ParseStyleLayers("not json");

        result.Should().BeEmpty();
    }

    [UnitTest]
    public void ParseStyleLayers_ArrayOfLayers_ParsesCorrectly()
    {
        const string json = """
        [
            {"id": "fill-layer", "type": "fill", "paint": {"fill-color": "#ff0000"}},
            {"id": "line-layer", "type": "line", "paint": {"line-color": "#0000ff"}}
        ]
        """;

        var result = StyleTranslator.ParseStyleLayers(json);

        result.Should().HaveCount(2);
        result[0].Id.Should().Be("fill-layer");
        result[0].Type.Should().Be("fill");
        result[1].Id.Should().Be("line-layer");
        result[1].Type.Should().Be("line");
    }

    [UnitTest]
    public void ParseStyleLayers_FullStyleDocument_ParsesLayers()
    {
        const string json = """
        {
            "version": 8,
            "name": "test",
            "layers": [
                {"id": "layer1", "type": "fill"}
            ]
        }
        """;

        var result = StyleTranslator.ParseStyleLayers(json);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("layer1");
    }

    [UnitTest]
    public void ParseStyleLayers_SingleLayerObject_ParsesCorrectly()
    {
        const string json = """{"id": "my-layer", "type": "circle"}""";

        var result = StyleTranslator.ParseStyleLayers(json);

        result.Should().HaveCount(1);
        result[0].Type.Should().Be("circle");
    }

    [UnitTest]
    public void ResolveFillStyle_NoPaint_ReturnsDefault()
    {
        var layer = new MapLibreStyleLayer { Type = "fill" };

        var style = StyleTranslator.ResolveFillStyle(layer, _emptyProps);

        style.FillColor.Alpha.Should().BeGreaterThan(0);
    }

    [UnitTest]
    public void ResolveFillStyle_WithPaint_ResolvesColor()
    {
        var layers = StyleTranslator.ParseStyleLayers("""[{"id":"f","type":"fill","paint":{"fill-color":"#ff0000","fill-opacity":1.0}}]""");

        var style = StyleTranslator.ResolveFillStyle(layers[0], _emptyProps);

        style.FillColor.Red.Should().Be(255);
        style.FillColor.Green.Should().Be(0);
        style.FillColor.Blue.Should().Be(0);
    }

    [UnitTest]
    public void ResolveFillStyle_WithOutlineColor_SetsOutline()
    {
        var layers = StyleTranslator.ParseStyleLayers("""[{"id":"f","type":"fill","paint":{"fill-color":"#ff0000","fill-outline-color":"#00ff00"}}]""");

        var style = StyleTranslator.ResolveFillStyle(layers[0], _emptyProps);

        style.OutlineColor.Should().NotBeNull();
        style.OutlineColor!.Value.Green.Should().Be(255);
    }

    [UnitTest]
    public void ResolveLineStyle_NoPaint_ReturnsDefault()
    {
        var layer = new MapLibreStyleLayer { Type = "line" };

        var style = StyleTranslator.ResolveLineStyle(layer, _emptyProps);

        style.LineWidth.Should().BeGreaterThan(0);
    }

    [UnitTest]
    public void ResolveLineStyle_WithPaint_ResolvesProperties()
    {
        var layers = StyleTranslator.ParseStyleLayers("""[{"id":"l","type":"line","paint":{"line-color":"#0000ff","line-width":3}}]""");

        var style = StyleTranslator.ResolveLineStyle(layers[0], _emptyProps);

        style.LineColor.Blue.Should().Be(255);
        style.LineWidth.Should().Be(3f);
    }

    [UnitTest]
    public void ResolveCircleStyle_NoPaint_ReturnsDefault()
    {
        var layer = new MapLibreStyleLayer { Type = "circle" };

        var style = StyleTranslator.ResolveCircleStyle(layer, _emptyProps);

        style.Radius.Should().BeGreaterThan(0);
    }

    [UnitTest]
    public void ResolveCircleStyle_WithPaint_ResolvesProperties()
    {
        var layers = StyleTranslator.ParseStyleLayers("""[{"id":"c","type":"circle","paint":{"circle-radius":8,"circle-color":"#00ff00","circle-stroke-color":"#000000","circle-stroke-width":2}}]""");

        var style = StyleTranslator.ResolveCircleStyle(layers[0], _emptyProps);

        style.Radius.Should().Be(8f);
        style.FillColor.Green.Should().Be(255);
        style.StrokeColor.Should().NotBeNull();
        style.StrokeWidth.Should().Be(2f);
    }

    [UnitTest]
    public void CreateDefaultPaints_Point_ReturnsFillOnly()
    {
        var (fill, stroke) = StyleTranslator.CreateDefaultPaints(GeometryType.Point);

        fill.Should().NotBeNull();
        fill.Style.Should().Be(SKPaintStyle.Fill);
        stroke.Should().BeNull();
        fill.Dispose();
    }

    [UnitTest]
    public void CreateDefaultPaints_LineString_ReturnsStrokeOnly()
    {
        var (fill, stroke) = StyleTranslator.CreateDefaultPaints(GeometryType.LineString);

        fill.Should().NotBeNull();
        fill.Style.Should().Be(SKPaintStyle.Stroke);
        stroke.Should().BeNull();
        fill.Dispose();
    }

    [UnitTest]
    public void CreateDefaultPaints_Polygon_ReturnsFillAndStroke()
    {
        var (fill, stroke) = StyleTranslator.CreateDefaultPaints(GeometryType.Polygon);

        fill.Should().NotBeNull();
        fill.Style.Should().Be(SKPaintStyle.Fill);
        stroke.Should().NotBeNull();
        stroke!.Style.Should().Be(SKPaintStyle.Stroke);
        fill.Dispose();
        stroke.Dispose();
    }
}
