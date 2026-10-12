// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Rendering;
using Honua.TestKit.Attributes;
using MapLibreStyleLayer = Honua.Infrastructure.Rendering.MapLibreStyleLayer;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Infrastructure.Rendering;

/// <summary>
/// Tests for MapLibre style to Skia paint translation.
/// </summary>
[Trait("Component", "MapServer")]
public class StyleTranslatorTests
{
    private static readonly ImmutableDictionary<string, object?> _emptyProps =
        ImmutableDictionary<string, object?>.Empty;

    /// <summary>
    /// The zoom passed by every test whose style contains no <c>["zoom"]</c> input; evaluating a
    /// zoom expression against it throws, so these tests passing unchanged shows their styles are
    /// unaffected by zoom support (honua-server#2873).
    /// </summary>
    private static readonly RenderZoom _noZoom =
        RenderZoom.NotDerivable("this test resolves no zoom expression");

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
    public void CollectReferencedFields_ReturnsDistinctFieldsFromFilterPaintAndLayout()
    {
        const string json = """
        [
            {
                "id": "layer1",
                "type": "circle",
                "filter": ["all", ["has", "category"], [">", ["get", "temperature"], 20]],
                "paint": {
                    "circle-color": ["case", ["==", ["get", "category"], "hot"], "#ff0000", "#0000ff"],
                    "circle-radius": ["get", "size"]
                },
                "layout": {
                    "visibility": ["case", ["has", "rank"], "visible", "none"]
                }
            }
        ]
        """;

        var layers = StyleTranslator.ParseStyleLayers(json);
        var fields = StyleTranslator.CollectReferencedFields(layers);

        fields.Should().Equal("category", "temperature", "size", "rank");
    }

    [UnitTest]
    public void ResolveFillStyle_NoPaint_ReturnsDefault()
    {
        var layer = new MapLibreStyleLayer { Type = "fill" };

        var style = StyleTranslator.ResolveFillStyle(layer, _emptyProps, _noZoom);

        style.FillColor.Alpha.Should().BeGreaterThan(0);
    }

    [UnitTest]
    public void ResolveFillStyle_WithPaint_ResolvesColor()
    {
        var layers = StyleTranslator.ParseStyleLayers("""[{"id":"f","type":"fill","paint":{"fill-color":"#ff0000","fill-opacity":1.0}}]""");

        var style = StyleTranslator.ResolveFillStyle(layers[0], _emptyProps, _noZoom);

        style.FillColor.Red.Should().Be(255);
        style.FillColor.Green.Should().Be(0);
        style.FillColor.Blue.Should().Be(0);
    }

    [UnitTest]
    public void ResolveFillStyle_WithColorAlphaAndOpacity_ComposesAlpha()
    {
        var layers = StyleTranslator.ParseStyleLayers("""[{"id":"f","type":"fill","paint":{"fill-color":"rgba(255,0,0,0.5)","fill-opacity":0.5}}]""");

        var style = StyleTranslator.ResolveFillStyle(layers[0], _emptyProps, _noZoom);

        style.FillColor.Alpha.Should().BeInRange((byte)63, (byte)64);
    }

    [UnitTest]
    public void ResolveFillStyle_WithOutlineColor_SetsOutline()
    {
        var layers = StyleTranslator.ParseStyleLayers("""[{"id":"f","type":"fill","paint":{"fill-color":"#ff0000","fill-outline-color":"#00ff00"}}]""");

        var style = StyleTranslator.ResolveFillStyle(layers[0], _emptyProps, _noZoom);

        style.OutlineColor.Should().NotBeNull();
        style.OutlineColor!.Value.Green.Should().Be(255);
    }

    [UnitTest]
    public void ResolveLineStyle_NoPaint_ReturnsDefault()
    {
        var layer = new MapLibreStyleLayer { Type = "line" };

        var style = StyleTranslator.ResolveLineStyle(layer, _emptyProps, _noZoom);

        style.LineWidth.Should().BeGreaterThan(0);
    }

    [UnitTest]
    public void ResolveLineStyle_WithPaint_ResolvesProperties()
    {
        var layers = StyleTranslator.ParseStyleLayers("""[{"id":"l","type":"line","paint":{"line-color":"#0000ff","line-width":3}}]""");

        var style = StyleTranslator.ResolveLineStyle(layers[0], _emptyProps, _noZoom);

        style.LineColor.Blue.Should().Be(255);
        style.LineWidth.Should().Be(3f);
    }

    [UnitTest]
    public void ResolveCircleStyle_NoPaint_ReturnsDefault()
    {
        var layer = new MapLibreStyleLayer { Type = "circle" };

        var style = StyleTranslator.ResolveCircleStyle(layer, _emptyProps, _noZoom);

        style.Radius.Should().BeGreaterThan(0);
    }

    [UnitTest]
    public void ResolveCircleStyle_WithPaint_ResolvesProperties()
    {
        var layers = StyleTranslator.ParseStyleLayers("""[{"id":"c","type":"circle","paint":{"circle-radius":8,"circle-color":"#00ff00","circle-stroke-color":"#000000","circle-stroke-width":2}}]""");

        var style = StyleTranslator.ResolveCircleStyle(layers[0], _emptyProps, _noZoom);

        style.Radius.Should().Be(8f);
        style.FillColor.Green.Should().Be(255);
        style.StrokeColor.Should().NotBeNull();
        style.StrokeWidth.Should().Be(2f);
    }

    [UnitTest]
    public void ResolveCircleStyle_WithColorAlphaAndOpacity_ComposesAlpha()
    {
        var layers = StyleTranslator.ParseStyleLayers("""[{"id":"c","type":"circle","paint":{"circle-color":"rgba(0,255,0,0.5)","circle-opacity":0.5}}]""");

        var style = StyleTranslator.ResolveCircleStyle(layers[0], _emptyProps, _noZoom);

        style.FillColor.Alpha.Should().BeInRange((byte)63, (byte)64);
    }

    [UnitTest]
    public void ShouldRenderLayer_WithVisibilityNone_ReturnsFalse()
    {
        var layers = StyleTranslator.ParseStyleLayers("""[{"id":"c","type":"circle","layout":{"visibility":"none"}}]""");

        StyleTranslator.ShouldRenderLayer(layers[0], RenderZoom.NotDerivable("unit test")).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(4.99, false)]
    [InlineData(5.0, true)]
    [InlineData(9.99, true)]
    [InlineData(10.0, false)]
    public void ShouldRenderLayer_WithZoomContext_AppliesMinInclusiveMaxExclusive(double? zoom, bool expected)
    {
        var layers = StyleTranslator.ParseStyleLayers("""[{"id":"c","type":"circle","minzoom":5,"maxzoom":10}]""");
        var renderZoom = zoom.HasValue ? RenderZoom.At(zoom.Value) : RenderZoom.NotDerivable("unit test");

        StyleTranslator.ShouldRenderLayer(layers[0], renderZoom).Should().Be(expected);
    }

    [Theory]
    [InlineData(4.99, false)]
    [InlineData(5.0, true)]
    [InlineData(9.99, true)]
    [InlineData(10.0, false)]
    public void IsAnyLayerInZoomRange_SingleScopedLayer_MatchesLayerGate(double zoom, bool expected)
    {
        var layers = StyleTranslator.ParseStyleLayers("""[{"id":"c","type":"circle","minzoom":5,"maxzoom":10}]""");

        StyleTranslator.IsAnyLayerInZoomRange(layers, RenderZoom.At(zoom)).Should().Be(expected);
    }

    [UnitTest]
    public void IsAnyLayerInZoomRange_OneLayerInRange_ReturnsTrue()
    {
        var layers = StyleTranslator.ParseStyleLayers(
            """[{"id":"a","type":"circle","minzoom":14},{"id":"b","type":"line","maxzoom":8}]""");

        StyleTranslator.IsAnyLayerInZoomRange(layers, RenderZoom.At(4)).Should().BeTrue();
    }

    [UnitTest]
    public void IsAnyLayerInZoomRange_AllLayersOutOfRange_ReturnsFalse()
    {
        var layers = StyleTranslator.ParseStyleLayers(
            """[{"id":"a","type":"circle","minzoom":14},{"id":"b","type":"line","minzoom":16}]""");

        StyleTranslator.IsAnyLayerInZoomRange(layers, RenderZoom.At(4)).Should().BeFalse();
    }

    [UnitTest]
    public void IsAnyLayerInZoomRange_NoDerivedZoom_ReturnsTrue()
    {
        var layers = StyleTranslator.ParseStyleLayers("""[{"id":"a","type":"circle","minzoom":14}]""");

        StyleTranslator.IsAnyLayerInZoomRange(layers, RenderZoom.NotDerivable("unit test")).Should().BeTrue();
    }

    [UnitTest]
    public void IsAnyLayerInZoomRange_EmptyStyle_ReturnsTrue()
    {
        StyleTranslator.IsAnyLayerInZoomRange([], RenderZoom.At(4)).Should().BeTrue();
    }

    // ---------------------------------------------------------------------------------------
    // Zoom-dependent paint resolution (honua-server#2873).
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Expected widths are MapLibre GL JS's own output for this expression
    /// (<c>@maplibre/maplibre-gl-style-spec</c>, <c>createExpression(expr, 'line-width',
    /// latest.paint_line['line-width'])</c>) at each zoom.
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(8, 1f)]
    [InlineData(10, 2.25f)]
    [InlineData(12, 3.5f)]
    [InlineData(14, 4.75f)]
    [InlineData(16, 6f)]
    public void ResolveLineStyle_ZoomDependentWidth_ResolvesAtTheRenderZoom(double zoom, float expected)
    {
        var layers = StyleTranslator.ParseStyleLayers(
            """[{"id":"a","type":"line","paint":{"line-width":["interpolate",["linear"],["zoom"],8,1,16,6]}}]""");

        var style = StyleTranslator.ResolveLineStyle(layers[0], _emptyProps, RenderZoom.At(zoom));

        style.LineWidth.Should().BeApproximately(expected, 1e-5f);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(8, 204, 204, 204)]
    [InlineData(10, 51, 170, 102)]
    [InlineData(14, 0, 136, 51)]
    public void ResolveFillStyle_ZoomDependentColor_ResolvesAtTheRenderZoom(double zoom, byte r, byte g, byte b)
    {
        var layers = StyleTranslator.ParseStyleLayers(
            """[{"id":"a","type":"fill","paint":{"fill-color":["step",["zoom"],"#ccc",10,"#3a6",14,"#083"]}}]""");

        var style = StyleTranslator.ResolveFillStyle(layers[0], _emptyProps, RenderZoom.At(zoom));

        style.FillColor.Should().Be(new SKColor(r, g, b, 255));
    }

    [UnitTest]
    public void ResolveLineStyle_ZoomDependentWidth_WithNotDerivableZoom_Throws()
    {
        var layers = StyleTranslator.ParseStyleLayers(
            """[{"id":"a","type":"line","paint":{"line-width":["interpolate",["linear"],["zoom"],8,1,16,6]}}]""");

        var act = () => StyleTranslator.ResolveLineStyle(layers[0], _emptyProps, _noZoom);

        act.Should().Throw<StyleExpressionEvaluationException>();
    }

    [UnitTest]
    public void UsesZoomExpression_PaintExpressionReadingZoom_ReturnsTrue()
    {
        var layers = StyleTranslator.ParseStyleLayers(
            """[{"id":"a","type":"circle","paint":{"circle-radius":["interpolate",["linear"],["zoom"],5,2,15,20]}}]""");

        StyleTranslator.UsesZoomExpression(layers[0]).Should().BeTrue();
    }

    [UnitTest]
    public void UsesZoomExpression_FilterReadingZoom_ReturnsTrue()
    {
        var layers = StyleTranslator.ParseStyleLayers(
            """[{"id":"a","type":"circle","filter":["<",["zoom"],10]}]""");

        StyleTranslator.UsesZoomExpression(layers[0]).Should().BeTrue();
    }

    [UnitTest]
    public void UsesZoomExpression_LayoutReadingZoom_ReturnsTrue()
    {
        var layers = StyleTranslator.ParseStyleLayers(
            """[{"id":"a","type":"line","layout":{"line-cap":["step",["zoom"],"butt",10,"round"]}}]""");

        StyleTranslator.UsesZoomExpression(layers[0]).Should().BeTrue();
    }

    /// <summary>
    /// A layer whose min/maxzoom scope it to a zoom range is not itself zoom-dependent: the gate is
    /// applied by <see cref="StyleTranslator.ShouldRenderLayer"/>, and the paint it resolves to is
    /// the same at every zoom it draws at. Reporting it as zoom-dependent would needlessly give up
    /// the pre-resolved fast paths.
    /// </summary>
    [UnitTest]
    public void UsesZoomExpression_DataDrivenAndZoomScopedStyles_ReturnFalse()
    {
        var dataDriven = StyleTranslator.ParseStyleLayers(
            """[{"id":"a","type":"circle","minzoom":5,"maxzoom":12,"paint":{"circle-radius":["interpolate",["linear"],["get","pop"],0,2,100,20]}}]""");

        StyleTranslator.UsesZoomExpression(dataDriven[0]).Should().BeFalse();
    }

    [UnitTest]
    public void UsesZoomExpression_StaticStyle_ReturnsFalse()
    {
        var layers = StyleTranslator.ParseStyleLayers(
            """[{"id":"a","type":"circle","paint":{"circle-radius":6,"circle-color":"#f00"}}]""");

        StyleTranslator.UsesZoomExpression(layers[0]).Should().BeFalse();
    }

    [UnitTest]
    public void CreateDefaultPaints_Point_ReturnsBatchedStrokePaint()
    {
        var (fill, stroke) = StyleTranslator.CreateDefaultPaints(MetadataV2GeometryType.Point);

        fill.Should().NotBeNull();
        fill.Style.Should().Be(SKPaintStyle.Stroke);
        fill.StrokeCap.Should().Be(SKStrokeCap.Round);
        fill.StrokeWidth.Should().Be(8f);
        stroke.Should().BeNull();
        fill.Dispose();
    }

    [UnitTest]
    public void CreateDefaultPaints_LineString_ReturnsStrokeOnly()
    {
        var (fill, stroke) = StyleTranslator.CreateDefaultPaints(MetadataV2GeometryType.LineString);

        fill.Should().NotBeNull();
        fill.Style.Should().Be(SKPaintStyle.Stroke);
        stroke.Should().BeNull();
        fill.Dispose();
    }

    [UnitTest]
    public void CreateDefaultPaints_Polygon_ReturnsFillAndStroke()
    {
        var (fill, stroke) = StyleTranslator.CreateDefaultPaints(MetadataV2GeometryType.Polygon);

        fill.Should().NotBeNull();
        fill.Style.Should().Be(SKPaintStyle.Fill);
        stroke.Should().NotBeNull();
        stroke!.Style.Should().Be(SKPaintStyle.Stroke);
        fill.Dispose();
        stroke.Dispose();
    }
}
