// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Features.Infrastructure.Styling;
using Xunit.Sdk;

namespace Honua.Server.Tests.Features.Infrastructure.Styling;

/// <summary>
/// Tests for Esri to MapLibre style conversion coverage.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Component", "Styling")]
[Trait("Feature", "StyleConversion")]
public class StyleConversionMatrixTests
{
    [Fact]
    public void GeoServicesToMapLibre_SimpleLineDash_MapsDashArray()
    {
        var layer = LayerDefinition.CreateBasic(1, "lines", GeometryType.LineString);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "simple",
            "symbol": {
              "type": "esriSLS",
              "style": "esriSLSDash",
              "color": [255, 0, 0, 255],
              "width": 3
            }
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var styleJson = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        using var styleDoc = JsonDocument.Parse(styleJson);
        var lineLayer = FindLayer(styleDoc.RootElement, "line");
        var paint = lineLayer.GetProperty("paint");

        AssertDashArray(paint, 4d, 2d);
        Assert.Equal(3d, GetNumber(paint.GetProperty("line-width")), 3);
    }

    [Fact]
    public void GeoServicesToMapLibre_SimplePolygonNullFill_MapsZeroOpacity()
    {
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "simple",
            "symbol": {
              "type": "esriSFS",
              "style": "esriSFSNull",
              "color": [0, 128, 0, 255],
              "outline": {
                "type": "esriSLS",
                "style": "esriSLSSolid",
                "color": [0, 0, 0, 255],
                "width": 1
              }
            }
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var styleJson = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        using var styleDoc = JsonDocument.Parse(styleJson);
        var fillLayer = FindLayer(styleDoc.RootElement, "fill");
        var paint = fillLayer.GetProperty("paint");

        Assert.True(paint.TryGetProperty("fill-opacity", out var opacity));
        Assert.Equal(0d, GetNumber(opacity), 3);
    }

    [Fact]
    public void GeoServicesToMapLibre_PictureMarker_MapsSymbolLayerAndMetadata()
    {
        var layer = LayerDefinition.CreateBasic(1, "points", GeometryType.Point);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "simple",
            "symbol": {
              "type": "esriPMS",
              "url": "https://example.com/marker.png",
              "width": 24,
              "height": 24,
              "xoffset": 2,
              "yoffset": -1,
              "angle": 15
            }
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var styleJson = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        using var styleDoc = JsonDocument.Parse(styleJson);
        var symbolLayer = FindLayer(styleDoc.RootElement, "symbol");
        var layout = symbolLayer.GetProperty("layout");

        Assert.Equal("layer-1-pms-0", layout.GetProperty("icon-image").GetString());

        var offset = layout.GetProperty("icon-offset").EnumerateArray().Select(GetNumber).ToArray();
        Assert.Equal(new[] { 2d, -1d }, offset);
        Assert.Equal(15d, GetNumber(layout.GetProperty("icon-rotate")), 3);

        var metadata = styleDoc.RootElement.GetProperty("metadata");
        var images = metadata.GetProperty("honua:images");
        var imageEntry = images.GetProperty("layer-1-pms-0");

        Assert.Equal("https://example.com/marker.png", imageEntry.GetProperty("url").GetString());
        Assert.Equal(24d, GetNumber(imageEntry.GetProperty("width")), 3);
        Assert.Equal(24d, GetNumber(imageEntry.GetProperty("height")), 3);
    }

    [Fact]
    public void GeoServicesToMapLibre_UniqueValuePoint_UsesMatchExpression()
    {
        var layer = LayerDefinition.CreateBasic(1, "points", GeometryType.Point);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "uniqueValue",
            "field1": "status",
            "uniqueValueInfos": [
              { "value": "A", "symbol": { "type": "esriSMS", "style": "esriSMSCircle", "color": [255, 0, 0, 255], "size": 8 } },
              { "value": "B", "symbol": { "type": "esriSMS", "style": "esriSMSCircle", "color": [0, 255, 0, 255], "size": 8 } }
            ],
            "defaultSymbol": { "type": "esriSMS", "style": "esriSMSCircle", "color": [0, 0, 255, 255], "size": 8 }
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var styleJson = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        using var styleDoc = JsonDocument.Parse(styleJson);
        var circleLayer = FindLayer(styleDoc.RootElement, "circle");
        var paint = circleLayer.GetProperty("paint");
        var expression = paint.GetProperty("circle-color");

        AssertMatchExpression(expression, "status");
    }

    [Fact]
    public void MapLibreToGeoServices_LineDash_MapsEsriLineStyle()
    {
        var layer = LayerDefinition.CreateBasic(1, "lines", GeometryType.LineString);
        const string mapLibreJson = """
        {
          "layers": [
            {
              "type": "line",
              "paint": {
                "line-color": "#ff0000",
                "line-width": 2,
                "line-dasharray": [4, 2]
              }
            }
          ]
        }
        """;

        var drawingInfoJson = MapLibreToGeoServicesConverter.Convert(mapLibreJson, layer);
        using var doc = JsonDocument.Parse(drawingInfoJson);
        var renderer = doc.RootElement.GetProperty("renderer");
        var symbol = renderer.GetProperty("symbol");

        Assert.Equal("simple", renderer.GetProperty("type").GetString());
        Assert.Equal("esriSLSDash", symbol.GetProperty("style").GetString());
    }

    [Fact]
    public void MapLibreToGeoServices_NullFill_MapsEsriNullStyle()
    {
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);
        const string mapLibreJson = """
        {
          "layers": [
            {
              "type": "fill",
              "paint": {
                "fill-color": "#00ff00",
                "fill-opacity": 0
              }
            }
          ]
        }
        """;

        var drawingInfoJson = MapLibreToGeoServicesConverter.Convert(mapLibreJson, layer);
        using var doc = JsonDocument.Parse(drawingInfoJson);
        var renderer = doc.RootElement.GetProperty("renderer");
        var symbol = renderer.GetProperty("symbol");

        Assert.Equal("esriSFSNull", symbol.GetProperty("style").GetString());
    }

    [Fact]
    public void MapLibreToGeoServices_PictureMarker_UsesMetadata()
    {
        var layer = LayerDefinition.CreateBasic(1, "points", GeometryType.Point);
        const string mapLibreJson = """
        {
          "metadata": {
            "honua:images": {
              "layer-1-pms-0": {
                "url": "https://example.com/marker.png",
                "width": 10,
                "height": 12
              }
            }
          },
          "layers": [
            {
              "type": "symbol",
              "layout": {
                "icon-image": "layer-1-pms-0",
                "icon-size": 2,
                "icon-offset": [1, 2],
                "icon-rotate": 30
              }
            }
          ]
        }
        """;

        var drawingInfoJson = MapLibreToGeoServicesConverter.Convert(mapLibreJson, layer);
        using var doc = JsonDocument.Parse(drawingInfoJson);
        var renderer = doc.RootElement.GetProperty("renderer");
        var symbol = renderer.GetProperty("symbol");

        Assert.Equal("esriPMS", symbol.GetProperty("type").GetString());
        Assert.Equal("https://example.com/marker.png", symbol.GetProperty("url").GetString());
        Assert.Equal(20d, GetNumber(symbol.GetProperty("width")), 3);
        Assert.Equal(24d, GetNumber(symbol.GetProperty("height")), 3);
        Assert.Equal(1d, GetNumber(symbol.GetProperty("xoffset")), 3);
        Assert.Equal(2d, GetNumber(symbol.GetProperty("yoffset")), 3);
        Assert.Equal(30d, GetNumber(symbol.GetProperty("angle")), 3);
    }

    [Fact]
    public void MapLibreToGeoServices_MatchExpression_MapsUniqueValueRenderer()
    {
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);
        const string mapLibreJson = """
        {
          "layers": [
            {
              "type": "fill",
              "paint": {
                "fill-color": ["match", ["get", "category"], "A", "#ff0000", "B", "#00ff00", "#0000ff"]
              }
            }
          ]
        }
        """;

        var drawingInfoJson = MapLibreToGeoServicesConverter.Convert(mapLibreJson, layer);
        using var doc = JsonDocument.Parse(drawingInfoJson);
        var renderer = doc.RootElement.GetProperty("renderer");

        Assert.Equal("uniqueValue", renderer.GetProperty("type").GetString());
        Assert.Equal("category", renderer.GetProperty("field1").GetString());
    }

    [Fact]
    public void MapLibreToGeoServices_StepExpression_MapsClassBreaksRenderer()
    {
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);
        const string mapLibreJson = """
        {
          "layers": [
            {
              "type": "fill",
              "paint": {
                "fill-color": ["step", ["get", "value"], "#ff0000", 10, "#00ff00", 20, "#0000ff"]
              }
            }
          ]
        }
        """;

        var drawingInfoJson = MapLibreToGeoServicesConverter.Convert(mapLibreJson, layer);
        using var doc = JsonDocument.Parse(drawingInfoJson);
        var renderer = doc.RootElement.GetProperty("renderer");
        var classBreaks = renderer.GetProperty("classBreakInfos");

        Assert.Equal("classBreaks", renderer.GetProperty("type").GetString());
        Assert.Equal("value", renderer.GetProperty("field").GetString());
        Assert.True(classBreaks.GetArrayLength() > 0);
        Assert.Equal(10d, GetNumber(classBreaks[0].GetProperty("classMaxValue")), 3);
    }

    private static JsonElement FindLayer(JsonElement style, string type)
    {
        foreach (var layer in style.GetProperty("layers").EnumerateArray())
        {
            if (layer.TryGetProperty("type", out var typeElement)
                && string.Equals(typeElement.GetString(), type, StringComparison.OrdinalIgnoreCase))
            {
                return layer.Clone();
            }
        }

        throw new XunitException($"Layer type '{type}' not found.");
    }

    private static void AssertDashArray(JsonElement paint, params double[] expected)
    {
        Assert.True(paint.TryGetProperty("line-dasharray", out var dashArray));
        Assert.Equal(JsonValueKind.Array, dashArray.ValueKind);

        var values = dashArray.EnumerateArray().Select(GetNumber).ToArray();
        Assert.Equal(expected.Length, values.Length);

        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], values[i], 3);
        }
    }

    private static void AssertMatchExpression(JsonElement expression, string field)
    {
        Assert.Equal(JsonValueKind.Array, expression.ValueKind);
        var items = expression.EnumerateArray().ToArray();

        Assert.True(items.Length >= 4);
        Assert.Equal("match", items[0].GetString());
        Assert.Equal(JsonValueKind.Array, items[1].ValueKind);

        var getExpr = items[1].EnumerateArray().ToArray();
        Assert.True(getExpr.Length >= 2);
        Assert.Equal("get", getExpr[0].GetString());
        Assert.Equal(field, getExpr[1].GetString());
    }

    private static double GetNumber(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value))
        {
            return value;
        }

        if (element.ValueKind == JsonValueKind.String
            && double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw new XunitException($"Expected numeric value but got {element.ValueKind}.");
    }
}
