// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Features.Infrastructure.Styling;
using Xunit.Sdk;

namespace Honua.Server.Tests.Features.Styling;

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

    [Fact]
    public void MapLibreToGeoServices_MatchWithToStringWrapper_MapsUniqueValueRenderer()
    {
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);
        const string mapLibreJson = """
        {
          "layers": [
            {
              "type": "fill",
              "paint": {
                "fill-color": ["match", ["to-string", ["get", "category"]], "A", "#ff0000", "B", "#00ff00", "#0000ff"]
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
    public void MapLibreToGeoServices_StepWithToNumberWrapper_MapsClassBreaksRenderer()
    {
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);
        const string mapLibreJson = """
        {
          "layers": [
            {
              "type": "fill",
              "paint": {
                "fill-color": ["step", ["to-number", ["get", "value"]], "#ff0000", 10, "#00ff00", 20, "#0000ff"]
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

    [Fact]
    public void MapLibreToGeoServices_CaseWrappedStep_MapsClassBreaksRenderer()
    {
        // Round-trip: case+has guard must be unwrapped to extract the inner step expression.
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);
        const string mapLibreJson = """
        {
          "layers": [
            {
              "type": "fill",
              "paint": {
                "fill-color": ["case", ["has", "value"], ["step", ["to-number", ["get", "value"]], "#ff0000", 10, "#00ff00", 20, "#0000ff"], "#cccccc"]
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

    [Fact]
    public void MapLibreToGeoServices_AllGuardedCaseWrappedStep_MapsClassBreaksRenderer()
    {
        // Round-trip: the stronger ["all", ["has", ...], ["!=", ...]] guard must
        // still unwrap correctly because the case expression has 4 elements and
        // items[2] is the step array — same structural invariant as the has-only guard.
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);
        const string mapLibreJson = """
        {
          "layers": [
            {
              "type": "fill",
              "paint": {
                "fill-color": ["case", ["all", ["has", "value"], ["!=", ["typeof", ["get", "value"]], "string"]], ["step", ["to-number", ["get", "value"]], "#ff0000", 10, "#00ff00", 20, "#0000ff"], "#cccccc"]
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

    [Fact]
    public void MapLibreToGeoServices_CustomGuardedCaseWrappedStep_FallsBackToSimpleRenderer()
    {
        // Regression: arbitrary case predicates must not be unwrapped into classBreaks,
        // or the converted renderer would silently drop the outer condition.
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);
        const string mapLibreJson = """
        {
          "layers": [
            {
              "type": "fill",
              "paint": {
                "fill-color": ["case", ["==", ["get", "eligible"], true], ["step", ["to-number", ["get", "value"]], "#ff0000", 10, "#00ff00", 20, "#0000ff"], "#cccccc"]
              }
            }
          ]
        }
        """;

        var drawingInfoJson = MapLibreToGeoServicesConverter.Convert(mapLibreJson, layer);
        using var doc = JsonDocument.Parse(drawingInfoJson);
        var renderer = doc.RootElement.GetProperty("renderer");

        Assert.Equal("simple", renderer.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(GeometryType.Point, "esriSMS", 8d, 217)]
    [InlineData(GeometryType.LineString, "esriSLS", null, 230)]
    [InlineData(GeometryType.Polygon, "esriSFS", null, 102)]
    [InlineData(GeometryType.GeometryCollection, "esriSFS", null, 102)]
    public void DefaultGeoServicesSymbol_MatchesMapLibreDefaults(
        GeometryType geometryType, string expectedType, double? expectedSize, int expectedAlpha)
    {
        var layer = LayerDefinition.CreateBasic(1, "test", geometryType);

        // Build both default representations
        var mapLibreStyle = StyleDefaults.BuildDefaultMapLibreStyle(layer);
        var drawingInfo = StyleDefaults.BuildDefaultDrawingInfo(layer);

        // Verify MapLibre layers exist
        var mapLibreLayers = (List<Dictionary<string, object?>>)mapLibreStyle["layers"]!;
        Assert.True(mapLibreLayers.Count > 0);

        // Verify GeoServices symbol type and alpha (opacity bake-in)
        var drawingInfoJson = StyleJsonUtilities.Serialize(drawingInfo);
        using var doc = JsonDocument.Parse(drawingInfoJson);
        var symbol = doc.RootElement.GetProperty("renderer").GetProperty("symbol");

        Assert.Equal(expectedType, symbol.GetProperty("type").GetString());

        var color = symbol.GetProperty("color");
        Assert.Equal(expectedAlpha, color[3].GetInt32());

        if (expectedSize.HasValue)
        {
            Assert.Equal(expectedSize.Value, GetNumber(symbol.GetProperty("size")), 3);
        }
    }

    [Fact]
    public void GeometryCollection_SuggestionGeneratedDefaultSymbol_EmitsFillSymbol()
    {
        // Regression: GeometryCollection must produce esriSFS (fill) symbols,
        // not fall through to the esriSLS (line) default branch.
        var layer = LayerDefinition.CreateBasic(1, "mixed", GeometryType.GeometryCollection);

        var suggestion = new Core.Features.Styling.Domain.StyleSuggestion
        {
            LayerId = 1,
            GeometryType = GeometryType.GeometryCollection,
            SuggestedField = new Core.Features.Styling.Domain.FieldSuggestion
            {
                Name = "category",
                Type = "String",
                Reason = "test",
                Profile = new Core.Features.Styling.Domain.FieldProfile
                {
                    FieldName = "category",
                    FieldType = "String",
                    TotalCount = 100,
                    NullCount = 0,
                    DistinctCount = 3
                }
            },
            Classification = new Core.Features.Styling.Domain.ClassificationResult
            {
                Method = Core.Features.Styling.Domain.ClassificationMethod.UniqueValue,
                FieldName = "category",
                Categories = ["A", "B"],
                ClassCount = 2
            },
            PaletteName = "CartoBold",
            PaletteColors = ["#E58606", "#5D69B1"],
            Legend = new Core.Features.Styling.Domain.LegendInfo
            {
                Title = "category",
                Entries = []
            },
            Observations = [],
            Edition = Core.Features.Licensing.Domain.HonuaEdition.Pro
        };

        var drawingInfo = StyleSuggestionGenerator.GenerateDrawingInfo(layer, suggestion);
        var json = StyleJsonUtilities.Serialize(drawingInfo);
        using var doc = JsonDocument.Parse(json);
        var renderer = doc.RootElement.GetProperty("renderer");

        // Verify class symbols are esriSFS, not esriSLS
        var uniqueValueInfos = renderer.GetProperty("uniqueValueInfos");
        foreach (var info in uniqueValueInfos.EnumerateArray())
        {
            Assert.Equal("esriSFS", info.GetProperty("symbol").GetProperty("type").GetString());
        }

        // Verify defaultSymbol is also esriSFS
        var defaultSymbol = renderer.GetProperty("defaultSymbol");
        Assert.Equal("esriSFS", defaultSymbol.GetProperty("type").GetString());
    }

    [Fact]
    public void GeometryCollection_UniqueValueSuggestion_SurvivesStyleUpdateRoundTrip()
    {
        // Regression: GeometryCollection suggestions must not be short-circuited to
        // defaults when applied through the PUT style → converter pipeline.
        var layer = LayerDefinition.CreateBasic(1, "mixed", GeometryType.GeometryCollection);

        var suggestion = new Core.Features.Styling.Domain.StyleSuggestion
        {
            LayerId = 1,
            GeometryType = GeometryType.GeometryCollection,
            SuggestedField = new Core.Features.Styling.Domain.FieldSuggestion
            {
                Name = "category",
                Type = "String",
                Reason = "test",
                Profile = new Core.Features.Styling.Domain.FieldProfile
                {
                    FieldName = "category",
                    FieldType = "String",
                    TotalCount = 100,
                    NullCount = 0,
                    DistinctCount = 3
                }
            },
            Classification = new Core.Features.Styling.Domain.ClassificationResult
            {
                Method = Core.Features.Styling.Domain.ClassificationMethod.UniqueValue,
                FieldName = "category",
                Categories = ["A", "B"],
                ClassCount = 2
            },
            PaletteName = "CartoBold",
            PaletteColors = ["#E58606", "#5D69B1"],
            Legend = new Core.Features.Styling.Domain.LegendInfo
            {
                Title = "category",
                Entries = []
            },
            Observations = [],
            Edition = Core.Features.Licensing.Domain.HonuaEdition.Pro
        };

        // Step 1: Generate MapLibre style from suggestion
        var mapLibreStyle = StyleSuggestionGenerator.GenerateMapLibreStyle(layer, suggestion);
        var mapLibreJson = StyleJsonUtilities.Serialize(mapLibreStyle);

        // Step 2: Simulate the PUT style path: MapLibre → GeoServices conversion
        var drawingInfoJson = MapLibreToGeoServicesConverter.Convert(mapLibreJson, layer);
        using var diDoc = JsonDocument.Parse(drawingInfoJson);
        var renderer = diDoc.RootElement.GetProperty("renderer");

        // Must produce uniqueValue, not simple (which would mean the suggestion was lost)
        Assert.Equal("uniqueValue", renderer.GetProperty("type").GetString());
        Assert.Equal("category", renderer.GetProperty("field1").GetString());
        var infos = renderer.GetProperty("uniqueValueInfos");
        Assert.Equal(2, infos.GetArrayLength());

        // Step 3: Simulate the drawingInfo-only path: GeoServices → MapLibre conversion
        var reMapLibreJson = GeoServicesToMapLibreConverter.Convert(diDoc.RootElement, layer);
        using var reDoc = JsonDocument.Parse(reMapLibreJson);
        var fillLayer = FindLayer(reDoc.RootElement, "fill");
        var colorExpr = fillLayer.GetProperty("paint").GetProperty("fill-color");
        AssertMatchExpression(colorExpr, "category");
    }

    [Fact]
    public void GeometryCollection_ClassBreaksSuggestion_SurvivesStyleUpdateRoundTrip()
    {
        // Regression: GeometryCollection class-break suggestions must round-trip
        // through the style update converters without being lost.
        var layer = LayerDefinition.CreateBasic(1, "mixed", GeometryType.GeometryCollection);

        var suggestion = new Core.Features.Styling.Domain.StyleSuggestion
        {
            LayerId = 1,
            GeometryType = GeometryType.GeometryCollection,
            SuggestedField = new Core.Features.Styling.Domain.FieldSuggestion
            {
                Name = "population",
                Type = "Double",
                Reason = "test",
                Profile = new Core.Features.Styling.Domain.FieldProfile
                {
                    FieldName = "population",
                    FieldType = "Double",
                    TotalCount = 100,
                    NullCount = 0,
                    DistinctCount = 50,
                    MinValue = 0.0,
                    MaxValue = 100.0
                }
            },
            Classification = new Core.Features.Styling.Domain.ClassificationResult
            {
                Method = Core.Features.Styling.Domain.ClassificationMethod.EqualInterval,
                FieldName = "population",
                Breaks = [50.0],
                ClassCount = 2
            },
            PaletteName = "Viridis",
            PaletteColors = ["#440154", "#FDE725"],
            Legend = new Core.Features.Styling.Domain.LegendInfo
            {
                Title = "population",
                Entries = []
            },
            Observations = [],
            Edition = Core.Features.Licensing.Domain.HonuaEdition.Pro
        };

        // Step 1: Generate MapLibre style from suggestion
        var mapLibreStyle = StyleSuggestionGenerator.GenerateMapLibreStyle(layer, suggestion);
        var mapLibreJson = StyleJsonUtilities.Serialize(mapLibreStyle);

        // Step 2: MapLibre → GeoServices (PUT style path)
        var drawingInfoJson = MapLibreToGeoServicesConverter.Convert(mapLibreJson, layer);
        using var diDoc = JsonDocument.Parse(drawingInfoJson);
        var renderer = diDoc.RootElement.GetProperty("renderer");

        // Must produce classBreaks, not simple
        Assert.Equal("classBreaks", renderer.GetProperty("type").GetString());
        Assert.Equal("population", renderer.GetProperty("field").GetString());
        Assert.True(renderer.GetProperty("classBreakInfos").GetArrayLength() > 0);

        // Step 3: GeoServices → MapLibre (drawingInfo-only path)
        var reMapLibreJson = GeoServicesToMapLibreConverter.Convert(diDoc.RootElement, layer);
        using var reDoc = JsonDocument.Parse(reMapLibreJson);
        var fillLayer = FindLayer(reDoc.RootElement, "fill");
        var colorExpr = fillLayer.GetProperty("paint").GetProperty("fill-color");

        // Should still be a case-wrapped step, not defaults
        var items = colorExpr.EnumerateArray().ToArray();
        Assert.Equal("case", items[0].GetString());
    }

    [Fact]
    public void GeoServicesToMapLibre_ClassBreaks_EmitsToNumberCoercion()
    {
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "classBreaks",
            "field": "population",
            "classBreakInfos": [
              { "classMinValue": 0, "classMaxValue": 100, "symbol": { "type": "esriSFS", "style": "esriSFSSolid", "color": [255, 0, 0, 255] } },
              { "classMinValue": 100, "classMaxValue": 500, "symbol": { "type": "esriSFS", "style": "esriSFSSolid", "color": [0, 255, 0, 255] } }
            ]
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var styleJson = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);
        using var styleDoc = JsonDocument.Parse(styleJson);
        var fillLayer = FindLayer(styleDoc.RootElement, "fill");
        var expression = fillLayer.GetProperty("paint").GetProperty("fill-color");

        var items = expression.EnumerateArray().ToArray();

        // Expect ["case", ["all", ["has", ...], ["!=", ["typeof", ...], "string"]], ["step", ...], fallback]
        Assert.Equal("case", items[0].GetString());
        var guardExpr = items[1].EnumerateArray().ToArray();
        Assert.Equal("all", guardExpr[0].GetString());
        var hasExpr = guardExpr[1].EnumerateArray().ToArray();
        Assert.Equal("has", hasExpr[0].GetString());
        Assert.Equal("population", hasExpr[1].GetString());

        // Inner step expression with to-number coercion
        var stepItems = items[2].EnumerateArray().ToArray();
        Assert.Equal("step", stepItems[0].GetString());
        Assert.Equal(JsonValueKind.Array, stepItems[1].ValueKind);

        var coercionExpr = stepItems[1].EnumerateArray().ToArray();
        Assert.Equal("to-number", coercionExpr[0].GetString());
        Assert.Equal(JsonValueKind.Array, coercionExpr[1].ValueKind);

        var getExpr = coercionExpr[1].EnumerateArray().ToArray();
        Assert.Equal("get", getExpr[0].GetString());
        Assert.Equal("population", getExpr[1].GetString());

        // Fallback color (last element of case)
        Assert.Equal(JsonValueKind.String, items[3].ValueKind);
    }

    [Fact]
    public void DrawingInfoRoundTrip_UniqueValue_PreservesCoercionWrappers()
    {
        // Simulates the suggest-style → apply drawingInfo → re-read MapLibre flow.
        // The coercion wrappers must survive the GeoServices → MapLibre conversion.
        var layer = LayerDefinition.CreateBasic(1, "points", GeometryType.Point);

        // Step 1: Build a unique-value drawingInfo (as the suggestion endpoint would return)
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "uniqueValue",
            "field1": "category",
            "uniqueValueInfos": [
              { "value": "park", "symbol": { "type": "esriSMS", "style": "esriSMSCircle", "color": [0, 128, 0, 255], "size": 8 } },
              { "value": "school", "symbol": { "type": "esriSMS", "style": "esriSMSCircle", "color": [0, 0, 255, 255], "size": 8 } }
            ]
          }
        }
        """;

        // Step 2: Convert drawingInfo → MapLibre (as LayerStyleService does on drawingInfo-only save)
        using var doc = JsonDocument.Parse(drawingInfoJson);
        var mapLibreJson = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        // Step 3: Verify MapLibre has to-string wrapper
        using var mapLibreDoc = JsonDocument.Parse(mapLibreJson);
        var circleLayer = FindLayer(mapLibreDoc.RootElement, "circle");
        var colorExpr = circleLayer.GetProperty("paint").GetProperty("circle-color");
        AssertMatchExpression(colorExpr, "category");

        // Step 4: Convert back to GeoServices to verify round-trip
        var geoServicesJson = MapLibreToGeoServicesConverter.Convert(mapLibreJson, layer);
        using var gsDoc = JsonDocument.Parse(geoServicesJson);
        var renderer = gsDoc.RootElement.GetProperty("renderer");

        Assert.Equal("uniqueValue", renderer.GetProperty("type").GetString());
        Assert.Equal("category", renderer.GetProperty("field1").GetString());
    }

    [Fact]
    public void GeoServicesToMapLibre_NumericUniqueValues_EmitsStringStops()
    {
        // Regression: numeric/boolean unique-value stop tokens must be emitted as
        // strings so that match(to-string(get(field)), ...) can match them.
        var layer = LayerDefinition.CreateBasic(1, "points", GeometryType.Point);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "uniqueValue",
            "field1": "zone_code",
            "uniqueValueInfos": [
              { "value": 1, "symbol": { "type": "esriSMS", "style": "esriSMSCircle", "color": [255, 0, 0, 255], "size": 8 } },
              { "value": 2, "symbol": { "type": "esriSMS", "style": "esriSMSCircle", "color": [0, 255, 0, 255], "size": 8 } },
              { "value": 3, "symbol": { "type": "esriSMS", "style": "esriSMSCircle", "color": [0, 0, 255, 255], "size": 8 } }
            ]
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var mapLibreJson = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);
        using var mapLibreDoc = JsonDocument.Parse(mapLibreJson);

        var circleLayer = FindLayer(mapLibreDoc.RootElement, "circle");
        var colorExpr = circleLayer.GetProperty("paint").GetProperty("circle-color");
        var outerItems = colorExpr.EnumerateArray().ToArray();

        // Expression is now case-wrapped: ["case", guard, ["match", ...], fallback]
        Assert.Equal("case", outerItems[0].GetString());
        var matchExpr = outerItems[2];
        var items = matchExpr.EnumerateArray().ToArray();

        // Verify coercion wrapper inside the match
        Assert.Equal("match", items[0].GetString());
        Assert.Equal("to-string", items[1].EnumerateArray().First().GetString());

        // Verify stop values are strings, not numbers
        // items layout: ["match", coercion, stop1, color1, stop2, color2, stop3, color3, fallback]
        Assert.Equal(JsonValueKind.String, items[2].ValueKind);
        Assert.Equal("1", items[2].GetString());
        Assert.Equal(JsonValueKind.String, items[4].ValueKind);
        Assert.Equal("2", items[4].GetString());
        Assert.Equal(JsonValueKind.String, items[6].ValueKind);
        Assert.Equal("3", items[6].GetString());

        // Round-trip back to GeoServices preserves the field
        var geoServicesJson = MapLibreToGeoServicesConverter.Convert(mapLibreJson, layer);
        using var gsDoc = JsonDocument.Parse(geoServicesJson);
        var renderer = gsDoc.RootElement.GetProperty("renderer");
        Assert.Equal("uniqueValue", renderer.GetProperty("type").GetString());
        Assert.Equal("zone_code", renderer.GetProperty("field1").GetString());
    }

    [Fact]
    public void GeoServicesToMapLibre_BooleanUniqueValues_EmitsStringStops()
    {
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "uniqueValue",
            "field1": "is_active",
            "uniqueValueInfos": [
              { "value": true, "symbol": { "type": "esriSFS", "style": "esriSFSSolid", "color": [0, 128, 0, 255] } },
              { "value": false, "symbol": { "type": "esriSFS", "style": "esriSFSSolid", "color": [255, 0, 0, 255] } }
            ]
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var mapLibreJson = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);
        using var mapLibreDoc = JsonDocument.Parse(mapLibreJson);

        var fillLayer = FindLayer(mapLibreDoc.RootElement, "fill");
        var colorExpr = fillLayer.GetProperty("paint").GetProperty("fill-color");
        var outerItems = colorExpr.EnumerateArray().ToArray();

        // Expression is now case-wrapped: ["case", guard, ["match", ...], fallback]
        Assert.Equal("case", outerItems[0].GetString());
        var matchItems = outerItems[2].EnumerateArray().ToArray();

        // Stop values should be strings "true"/"false" to match to-string coercion
        Assert.Equal(JsonValueKind.String, matchItems[2].ValueKind);
        Assert.Equal("true", matchItems[2].GetString());
        Assert.Equal(JsonValueKind.String, matchItems[4].ValueKind);
        Assert.Equal("false", matchItems[4].GetString());
    }

    [Fact]
    public void GeoServicesToMapLibre_PictureMarkerNumericUniqueValues_EmitsStringStops()
    {
        var layer = LayerDefinition.CreateBasic(1, "points", GeometryType.Point);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "uniqueValue",
            "field1": "priority",
            "uniqueValueInfos": [
              { "value": 1, "symbol": { "type": "esriPMS", "url": "https://example.com/low.png", "width": 16, "height": 16 } },
              { "value": 2, "symbol": { "type": "esriPMS", "url": "https://example.com/med.png", "width": 16, "height": 16 } },
              { "value": 3, "symbol": { "type": "esriPMS", "url": "https://example.com/high.png", "width": 16, "height": 16 } }
            ]
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var mapLibreJson = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);
        using var mapLibreDoc = JsonDocument.Parse(mapLibreJson);

        var symbolLayer = FindLayer(mapLibreDoc.RootElement, "symbol");
        var iconExpr = symbolLayer.GetProperty("layout").GetProperty("icon-image");
        var outerItems = iconExpr.EnumerateArray().ToArray();

        // Expression is now case-wrapped: ["case", guard, ["match", ...], fallback]
        Assert.Equal("case", outerItems[0].GetString());
        var items = outerItems[2].EnumerateArray().ToArray();

        // Verify coercion wrapper inside the match
        Assert.Equal("match", items[0].GetString());
        Assert.Equal("to-string", items[1].EnumerateArray().First().GetString());

        // Verify stop values are strings, not numbers
        Assert.Equal(JsonValueKind.String, items[2].ValueKind);
        Assert.Equal("1", items[2].GetString());
        Assert.Equal(JsonValueKind.String, items[4].ValueKind);
        Assert.Equal("2", items[4].GetString());
        Assert.Equal(JsonValueKind.String, items[6].ValueKind);
        Assert.Equal("3", items[6].GetString());
    }

    [Fact]
    public void GenerateDrawingInfo_ClassBreaks_UsesActualDataRangeBounds()
    {
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);

        var suggestion = new Core.Features.Styling.Domain.StyleSuggestion
        {
            LayerId = 1,
            GeometryType = GeometryType.Polygon,
            SuggestedField = new Core.Features.Styling.Domain.FieldSuggestion
            {
                Name = "population",
                Type = "Double",
                Reason = "test",
                Profile = new Core.Features.Styling.Domain.FieldProfile
                {
                    FieldName = "population",
                    FieldType = "Double",
                    TotalCount = 100,
                    NullCount = 0,
                    DistinctCount = 50,
                    MinValue = 5.0,
                    MaxValue = 500.0,
                    MeanValue = 200.0,
                    StandardDeviation = 80.0
                }
            },
            Classification = new Core.Features.Styling.Domain.ClassificationResult
            {
                Method = Core.Features.Styling.Domain.ClassificationMethod.EqualInterval,
                FieldName = "population",
                Breaks = [100.0, 200.0, 300.0, 400.0],
                ClassCount = 5
            },
            PaletteName = "Viridis",
            PaletteColors = ["#440154", "#3B528B", "#21918C", "#5EC962", "#FDE725"],
            Legend = new Core.Features.Styling.Domain.LegendInfo
            {
                Title = "population",
                Entries = []
            },
            Observations = [],
            Edition = Core.Features.Licensing.Domain.HonuaEdition.Pro
        };

        var drawingInfo = StyleSuggestionGenerator.GenerateDrawingInfo(layer, suggestion);
        var json = StyleJsonUtilities.Serialize(drawingInfo);
        using var doc = JsonDocument.Parse(json);
        var classBreaks = doc.RootElement.GetProperty("renderer").GetProperty("classBreakInfos");
        var items = classBreaks.EnumerateArray().ToArray();

        Assert.Equal(5, items.Length);

        // First class: classMinValue should be the data min (5.0), not double.MinValue
        Assert.Equal(5.0, GetNumber(items[0].GetProperty("classMinValue")), 3);
        Assert.Equal(100.0, GetNumber(items[0].GetProperty("classMaxValue")), 3);

        // Middle class: uses break values directly
        Assert.Equal(100.0, GetNumber(items[1].GetProperty("classMinValue")), 3);
        Assert.Equal(200.0, GetNumber(items[1].GetProperty("classMaxValue")), 3);

        // Last class: classMaxValue should be the data max (500.0), not double.MaxValue
        Assert.Equal(400.0, GetNumber(items[4].GetProperty("classMinValue")), 3);
        Assert.Equal(500.0, GetNumber(items[4].GetProperty("classMaxValue")), 3);
    }

    [Fact]
    public void GenerateMapLibreStyle_ClassBreaks_GuardsNullsWithCaseHas()
    {
        // Regression: to-number(null) → 0 silently routes features with a
        // missing/null classification field into the first color bucket.
        // The step expression must be wrapped in ["case", ["has", field], step, fallback].
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);

        var suggestion = new Core.Features.Styling.Domain.StyleSuggestion
        {
            LayerId = 1,
            GeometryType = GeometryType.Polygon,
            SuggestedField = new Core.Features.Styling.Domain.FieldSuggestion
            {
                Name = "population",
                Type = "Double",
                Reason = "test",
                Profile = new Core.Features.Styling.Domain.FieldProfile
                {
                    FieldName = "population",
                    FieldType = "Double",
                    TotalCount = 100,
                    NullCount = 10,
                    DistinctCount = 50,
                    MinValue = 0.0,
                    MaxValue = 100.0
                }
            },
            Classification = new Core.Features.Styling.Domain.ClassificationResult
            {
                Method = Core.Features.Styling.Domain.ClassificationMethod.EqualInterval,
                FieldName = "population",
                Breaks = [50.0],
                ClassCount = 2
            },
            PaletteName = "Viridis",
            PaletteColors = ["#440154", "#FDE725"],
            Legend = new Core.Features.Styling.Domain.LegendInfo
            {
                Title = "population",
                Entries = []
            },
            Observations = [],
            Edition = Core.Features.Licensing.Domain.HonuaEdition.Pro
        };

        var mapLibreStyle = StyleSuggestionGenerator.GenerateMapLibreStyle(layer, suggestion);
        var json = StyleJsonUtilities.Serialize(mapLibreStyle);
        using var doc = JsonDocument.Parse(json);

        var fillLayer = FindLayer(doc.RootElement, "fill");
        var colorExpr = fillLayer.GetProperty("paint").GetProperty("fill-color");
        var items = colorExpr.EnumerateArray().ToArray();

        // Outer: ["case", guard, stepExpr, "#CCCCCC"]
        Assert.Equal("case", items[0].GetString());

        // Guard: ["all", ["has", "population"], ["==", ["typeof", ["get", "population"]], "number"]]
        var guardExpr = items[1].EnumerateArray().ToArray();
        Assert.Equal("all", guardExpr[0].GetString());
        var hasExpr = guardExpr[1].EnumerateArray().ToArray();
        Assert.Equal("has", hasExpr[0].GetString());
        Assert.Equal("population", hasExpr[1].GetString());
        var typeCheck = guardExpr[2].EnumerateArray().ToArray();
        Assert.Equal("==", typeCheck[0].GetString());

        // Inner step expression
        var stepItems = items[2].EnumerateArray().ToArray();
        Assert.Equal("step", stepItems[0].GetString());

        // Fallback is the gray color for null/missing values
        Assert.Equal("#CCCCCC", items[3].GetString());
    }

    [Fact]
    public void GeometryOnlySuggestion_LineLayer_MapLibreAndDrawingInfoAreConsistent()
    {
        // Regression: geometry-only suggestion for line layers must return
        // equivalent MapLibre and drawingInfo payloads — no zoom-dependent
        // line-width in MapLibre when GeoServices can only express a static width.
        var layer = LayerDefinition.CreateBasic(1, "roads", GeometryType.LineString);

        var mapLibreStyle = StyleSuggestionGenerator.GenerateEnhancedDefaults(layer);
        var drawingInfo = StyleSuggestionGenerator.GenerateEnhancedDrawingInfo(layer);

        // Parse MapLibre line-width
        var mapLibreJson = StyleJsonUtilities.Serialize(mapLibreStyle);
        using var mlDoc = JsonDocument.Parse(mapLibreJson);
        var lineLayer = FindLayer(mlDoc.RootElement, "line");
        var mlPaint = lineLayer.GetProperty("paint");
        var mlLineWidth = mlPaint.GetProperty("line-width");

        // line-width must be a scalar number, NOT a zoom interpolation array
        Assert.Equal(JsonValueKind.Number, mlLineWidth.ValueKind);
        var mlWidth = mlLineWidth.GetDouble();

        // Parse GeoServices line width from symbol
        var diJson = StyleJsonUtilities.Serialize(drawingInfo);
        using var diDoc = JsonDocument.Parse(diJson);
        var symbol = diDoc.RootElement.GetProperty("renderer").GetProperty("symbol");
        var diWidth = GetNumber(symbol.GetProperty("width"));

        Assert.Equal(diWidth, mlWidth, 3);
    }

    [Theory]
    [InlineData(GeometryType.Point)]
    [InlineData(GeometryType.LineString)]
    [InlineData(GeometryType.Polygon)]
    public void UniqueValueSuggestion_FallbackColor_MatchesBetweenFormats(GeometryType geometryType)
    {
        // Regression: the MapLibre match expression fallback (#CCCCCC) must match
        // the GeoServices defaultSymbol color so unmatched features render identically.
        var layer = LayerDefinition.CreateBasic(1, "test", geometryType);

        var suggestion = new Core.Features.Styling.Domain.StyleSuggestion
        {
            LayerId = 1,
            GeometryType = geometryType,
            SuggestedField = new Core.Features.Styling.Domain.FieldSuggestion
            {
                Name = "category",
                Type = "String",
                Reason = "test",
                Profile = new Core.Features.Styling.Domain.FieldProfile
                {
                    FieldName = "category",
                    FieldType = "String",
                    TotalCount = 100,
                    NullCount = 0,
                    DistinctCount = 3
                }
            },
            Classification = new Core.Features.Styling.Domain.ClassificationResult
            {
                Method = Core.Features.Styling.Domain.ClassificationMethod.UniqueValue,
                FieldName = "category",
                Categories = ["A", "B"],
                ClassCount = 2
            },
            PaletteName = "CartoBold",
            PaletteColors = ["#E58606", "#5D69B1"],
            Legend = new Core.Features.Styling.Domain.LegendInfo
            {
                Title = "category",
                Entries = []
            },
            Observations = [],
            Edition = Core.Features.Licensing.Domain.HonuaEdition.Pro
        };

        // Generate both formats
        var mapLibreStyle = StyleSuggestionGenerator.GenerateMapLibreStyle(layer, suggestion);
        var drawingInfo = StyleSuggestionGenerator.GenerateDrawingInfo(layer, suggestion);

        // Extract MapLibre fallback color (last element of the match expression)
        var mlJson = StyleJsonUtilities.Serialize(mapLibreStyle);
        using var mlDoc = JsonDocument.Parse(mlJson);
        var layers = mlDoc.RootElement.GetProperty("layers");
        var firstLayer = layers.EnumerateArray().First();
        var paint = firstLayer.GetProperty("paint");

        // Find the color expression (circle-color, line-color, or fill-color)
        JsonElement colorExpr;
        if (paint.TryGetProperty("circle-color", out colorExpr) ||
            paint.TryGetProperty("line-color", out colorExpr) ||
            paint.TryGetProperty("fill-color", out colorExpr))
        {
            // ok
        }
        else
        {
            throw new XunitException("No color expression found in paint.");
        }

        var colorItems = colorExpr.EnumerateArray().ToArray();
        var mlFallback = colorItems[^1].GetString(); // last element is the fallback

        // Extract GeoServices defaultSymbol color
        var diJson = StyleJsonUtilities.Serialize(drawingInfo);
        using var diDoc = JsonDocument.Parse(diJson);
        var defaultSymbol = diDoc.RootElement.GetProperty("renderer").GetProperty("defaultSymbol");
        var symbolColor = defaultSymbol.GetProperty("color");
        var r = symbolColor[0].GetInt32();
        var g = symbolColor[1].GetInt32();
        var b = symbolColor[2].GetInt32();
        var gsFallbackHex = $"#{r:X2}{g:X2}{b:X2}";

        Assert.Equal(mlFallback!.ToUpperInvariant(), gsFallbackHex.ToUpperInvariant());
    }

    [Theory]
    [InlineData("N/A")]
    [InlineData("")]
    [InlineData("unknown")]
    public void ServerEvaluator_ClassBreaks_NonCastableText_RendersFallbackColor(string dirtyValue)
    {
        // Regression: features whose numeric-classified field contains non-castable
        // text (e.g. "N/A", "") must render with the fallback color, NOT the first
        // class color.  The typeof guard prevents to-number from coercing to 0.
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);

        var suggestion = new Core.Features.Styling.Domain.StyleSuggestion
        {
            LayerId = 1,
            GeometryType = GeometryType.Polygon,
            SuggestedField = new Core.Features.Styling.Domain.FieldSuggestion
            {
                Name = "population",
                Type = "Double",
                Reason = "test",
                Profile = new Core.Features.Styling.Domain.FieldProfile
                {
                    FieldName = "population",
                    FieldType = "Double",
                    TotalCount = 100,
                    NullCount = 5,
                    DistinctCount = 50,
                    MinValue = 0.0,
                    MaxValue = 100.0
                }
            },
            Classification = new Core.Features.Styling.Domain.ClassificationResult
            {
                Method = Core.Features.Styling.Domain.ClassificationMethod.EqualInterval,
                FieldName = "population",
                Breaks = [50.0],
                ClassCount = 2
            },
            PaletteName = "Viridis",
            PaletteColors = ["#440154", "#FDE725"],
            Legend = new Core.Features.Styling.Domain.LegendInfo
            {
                Title = "population",
                Entries = []
            },
            Observations = [],
            Edition = Core.Features.Licensing.Domain.HonuaEdition.Pro
        };

        // Generate the MapLibre style with the numeric guard
        var mapLibreStyle = StyleSuggestionGenerator.GenerateMapLibreStyle(layer, suggestion);
        var json = StyleJsonUtilities.Serialize(mapLibreStyle);

        // Parse the expression and evaluate against a feature with dirty text
        using var doc = JsonDocument.Parse(json);
        var fillLayer = FindLayer(doc.RootElement, "fill");
        var colorExprJson = fillLayer.GetProperty("paint").GetProperty("fill-color").GetRawText();

        var expr = Honua.Server.Features.Infrastructure.Rendering.MapLibreExpressionParser.Parse(colorExprJson);
        var props = System.Collections.Immutable.ImmutableDictionary<string, object?>.Empty
            .Add("population", dirtyValue);

        var result = Honua.Server.Features.Infrastructure.Rendering.ExpressionEvaluator.Evaluate(expr, props);

        // Must be the gray fallback color (#CCCCCC), NOT the first bucket color (#440154)
        Assert.Equal("#CCCCCC", result?.ToString());
    }

    [Fact]
    public void ServerEvaluator_ClassBreaks_NullValue_RendersFallbackColor()
    {
        // Regression: a feature whose classified field key exists but has a null value
        // must render with the fallback color.  typeof(null) → "null" ≠ "number", so
        // the guard rejects it.  Before the fix, the guard used ["!=", typeof, "string"]
        // which let null through because "null" ≠ "string" was true.
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);

        var suggestion = new Core.Features.Styling.Domain.StyleSuggestion
        {
            LayerId = 1,
            GeometryType = GeometryType.Polygon,
            SuggestedField = new Core.Features.Styling.Domain.FieldSuggestion
            {
                Name = "population",
                Type = "Double",
                Reason = "test",
                Profile = new Core.Features.Styling.Domain.FieldProfile
                {
                    FieldName = "population",
                    FieldType = "Double",
                    TotalCount = 100,
                    NullCount = 5,
                    DistinctCount = 50,
                    MinValue = 0.0,
                    MaxValue = 100.0
                }
            },
            Classification = new Core.Features.Styling.Domain.ClassificationResult
            {
                Method = Core.Features.Styling.Domain.ClassificationMethod.EqualInterval,
                FieldName = "population",
                Breaks = [50.0],
                ClassCount = 2
            },
            PaletteName = "Viridis",
            PaletteColors = ["#440154", "#FDE725"],
            Legend = new Core.Features.Styling.Domain.LegendInfo
            {
                Title = "population",
                Entries = []
            },
            Observations = [],
            Edition = Core.Features.Licensing.Domain.HonuaEdition.Pro
        };

        var mapLibreStyle = StyleSuggestionGenerator.GenerateMapLibreStyle(layer, suggestion);
        var json = StyleJsonUtilities.Serialize(mapLibreStyle);

        using var doc = JsonDocument.Parse(json);
        var fillLayer = FindLayer(doc.RootElement, "fill");
        var colorExprJson = fillLayer.GetProperty("paint").GetProperty("fill-color").GetRawText();

        var expr = Honua.Server.Features.Infrastructure.Rendering.MapLibreExpressionParser.Parse(colorExprJson);
        // Key exists but value is null — simulates JSONB null or explicit null in source data
        var props = System.Collections.Immutable.ImmutableDictionary<string, object?>.Empty
            .Add("population", null);

        var result = Honua.Server.Features.Infrastructure.Rendering.ExpressionEvaluator.Evaluate(expr, props);

        Assert.Equal("#CCCCCC", result?.ToString());
    }

    [Fact]
    public void ServerEvaluator_ClassBreaks_NumericValue_RendersClassColor()
    {
        // Complement to the dirty-text test: verify that native numbers
        // correctly route through the step expression.
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);

        var suggestion = new Core.Features.Styling.Domain.StyleSuggestion
        {
            LayerId = 1,
            GeometryType = GeometryType.Polygon,
            SuggestedField = new Core.Features.Styling.Domain.FieldSuggestion
            {
                Name = "population",
                Type = "Double",
                Reason = "test",
                Profile = new Core.Features.Styling.Domain.FieldProfile
                {
                    FieldName = "population",
                    FieldType = "Double",
                    TotalCount = 100,
                    NullCount = 0,
                    DistinctCount = 50,
                    MinValue = 0.0,
                    MaxValue = 100.0
                }
            },
            Classification = new Core.Features.Styling.Domain.ClassificationResult
            {
                Method = Core.Features.Styling.Domain.ClassificationMethod.EqualInterval,
                FieldName = "population",
                Breaks = [50.0],
                ClassCount = 2
            },
            PaletteName = "Viridis",
            PaletteColors = ["#440154", "#FDE725"],
            Legend = new Core.Features.Styling.Domain.LegendInfo
            {
                Title = "population",
                Entries = []
            },
            Observations = [],
            Edition = Core.Features.Licensing.Domain.HonuaEdition.Pro
        };

        var mapLibreStyle = StyleSuggestionGenerator.GenerateMapLibreStyle(layer, suggestion);
        var json = StyleJsonUtilities.Serialize(mapLibreStyle);

        using var doc = JsonDocument.Parse(json);
        var fillLayer = FindLayer(doc.RootElement, "fill");
        var colorExprJson = fillLayer.GetProperty("paint").GetProperty("fill-color").GetRawText();

        var expr = Honua.Server.Features.Infrastructure.Rendering.MapLibreExpressionParser.Parse(colorExprJson);

        // Value 25.0 < break 50.0 → first bucket color
        var propsLow = System.Collections.Immutable.ImmutableDictionary<string, object?>.Empty
            .Add("population", 25.0);
        var resultLow = Honua.Server.Features.Infrastructure.Rendering.ExpressionEvaluator.Evaluate(expr, propsLow);
        Assert.Equal("#440154", resultLow?.ToString());

        // Value 75.0 >= break 50.0 → second bucket color
        var propsHigh = System.Collections.Immutable.ImmutableDictionary<string, object?>.Empty
            .Add("population", 75.0);
        var resultHigh = Honua.Server.Features.Infrastructure.Rendering.ExpressionEvaluator.Evaluate(expr, propsHigh);
        Assert.Equal("#FDE725", resultHigh?.ToString());
    }

    [Fact]
    public void MapLibreToGeoServices_CaseWrappedStepWithFallback_EmitsDefaultSymbol()
    {
        // Regression: when the MapLibre style has a ["case", guard, step, "#CCCCCC"]
        // expression, the GeoServices classBreaks renderer must include a defaultSymbol
        // whose color matches the case fallback.
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);
        const string mapLibreJson = """
        {
          "layers": [
            {
              "type": "fill",
              "paint": {
                "fill-color": ["case", ["all", ["has", "value"], ["!=", ["typeof", ["get", "value"]], "string"]], ["step", ["to-number", ["get", "value"]], "#ff0000", 10, "#00ff00", 20, "#0000ff"], "#cccccc"]
              }
            }
          ]
        }
        """;

        var drawingInfoJson = MapLibreToGeoServicesConverter.Convert(mapLibreJson, layer);
        using var doc = JsonDocument.Parse(drawingInfoJson);
        var renderer = doc.RootElement.GetProperty("renderer");

        Assert.Equal("classBreaks", renderer.GetProperty("type").GetString());
        Assert.True(renderer.TryGetProperty("defaultSymbol", out var defaultSymbol),
            "classBreaks renderer must include defaultSymbol when case fallback is present");

        // Verify the defaultSymbol color matches #CCCCCC = RGB(204, 204, 204)
        var color = defaultSymbol.GetProperty("color");
        Assert.Equal(204, color[0].GetInt32());
        Assert.Equal(204, color[1].GetInt32());
        Assert.Equal(204, color[2].GetInt32());

        Assert.Equal("Other", renderer.GetProperty("defaultLabel").GetString());
    }

    [Fact]
    public void GeoServicesToMapLibre_ClassBreaksWithDefaultSymbol_UsesFallbackColor()
    {
        // Regression: when a classBreaks renderer includes defaultSymbol, the
        // MapLibre case fallback must use its color, not the first class color.
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "classBreaks",
            "field": "population",
            "defaultSymbol": { "type": "esriSFS", "style": "esriSFSSolid", "color": [204, 204, 204, 255] },
            "defaultLabel": "Other",
            "classBreakInfos": [
              { "classMinValue": 0, "classMaxValue": 100, "symbol": { "type": "esriSFS", "style": "esriSFSSolid", "color": [255, 0, 0, 255] } },
              { "classMinValue": 100, "classMaxValue": 500, "symbol": { "type": "esriSFS", "style": "esriSFSSolid", "color": [0, 255, 0, 255] } }
            ]
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var styleJson = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);
        using var styleDoc = JsonDocument.Parse(styleJson);
        var fillLayer = FindLayer(styleDoc.RootElement, "fill");
        var expression = fillLayer.GetProperty("paint").GetProperty("fill-color");

        var items = expression.EnumerateArray().ToArray();
        Assert.Equal("case", items[0].GetString());

        // Last element (case fallback) must be the defaultSymbol color, NOT the first class color
        var fallbackStr = items[3].GetString();
        Assert.NotNull(fallbackStr);

        // Parse the fallback RGBA string and verify it matches #CCCCCC (not #FF0000)
        Assert.True(StyleJsonUtilities.TryParseMapLibreColor(items[3], out var fallbackColor));
        Assert.Equal(204, fallbackColor.R);
        Assert.Equal(204, fallbackColor.G);
        Assert.Equal(204, fallbackColor.B);
    }

    [Fact]
    public void SuggestionGenerator_ClassBreaks_MapLibreToGeoServicesRoundTrip_PreservesDefaultSymbol()
    {
        // End-to-end: StyleSuggestionGenerator → MapLibre style → MapLibreToGeoServicesConverter
        // → verify the drawingInfo contains a gray defaultSymbol.
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);

        var suggestion = new Core.Features.Styling.Domain.StyleSuggestion
        {
            LayerId = 1,
            GeometryType = GeometryType.Polygon,
            SuggestedField = new Core.Features.Styling.Domain.FieldSuggestion
            {
                Name = "population",
                Type = "Double",
                Reason = "test",
                Profile = new Core.Features.Styling.Domain.FieldProfile
                {
                    FieldName = "population",
                    FieldType = "Double",
                    TotalCount = 100,
                    NullCount = 10,
                    DistinctCount = 50,
                    MinValue = 0.0,
                    MaxValue = 100.0
                }
            },
            Classification = new Core.Features.Styling.Domain.ClassificationResult
            {
                Method = Core.Features.Styling.Domain.ClassificationMethod.EqualInterval,
                FieldName = "population",
                Breaks = [50.0],
                ClassCount = 2
            },
            PaletteName = "Viridis",
            PaletteColors = ["#440154", "#FDE725"],
            Legend = new Core.Features.Styling.Domain.LegendInfo
            {
                Title = "population",
                Entries = []
            },
            Observations = [],
            Edition = Core.Features.Licensing.Domain.HonuaEdition.Pro
        };

        // Step 1: Generate MapLibre style (has case + #CCCCCC fallback)
        var mapLibreStyle = StyleSuggestionGenerator.GenerateMapLibreStyle(layer, suggestion);
        var mapLibreJson = StyleJsonUtilities.Serialize(mapLibreStyle);

        // Step 2: Convert to GeoServices
        var geoServicesJson = MapLibreToGeoServicesConverter.Convert(mapLibreJson, layer);
        using var doc = JsonDocument.Parse(geoServicesJson);
        var renderer = doc.RootElement.GetProperty("renderer");

        Assert.Equal("classBreaks", renderer.GetProperty("type").GetString());
        Assert.True(renderer.TryGetProperty("defaultSymbol", out var defaultSymbol),
            "Round-tripped classBreaks must include defaultSymbol from the case fallback");

        // Verify the defaultSymbol color is gray (#CCCCCC = 204, 204, 204)
        var color = defaultSymbol.GetProperty("color");
        Assert.Equal(204, color[0].GetInt32());
        Assert.Equal(204, color[1].GetInt32());
        Assert.Equal(204, color[2].GetInt32());
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
        var outerItems = expression.EnumerateArray().ToArray();

        // Expression is now case-wrapped: ["case", guard, ["match", ...], fallback]
        Assert.True(outerItems.Length == 4);
        Assert.Equal("case", outerItems[0].GetString());
        Assert.Equal(JsonValueKind.Array, outerItems[2].ValueKind);

        var items = outerItems[2].EnumerateArray().ToArray();
        Assert.True(items.Length >= 4);
        Assert.Equal("match", items[0].GetString());
        Assert.Equal(JsonValueKind.Array, items[1].ValueKind);

        // Expect ["to-string", ["get", field]] coercion wrapper
        var coercionExpr = items[1].EnumerateArray().ToArray();
        Assert.True(coercionExpr.Length >= 2);
        Assert.Equal("to-string", coercionExpr[0].GetString());
        Assert.Equal(JsonValueKind.Array, coercionExpr[1].ValueKind);

        var getExpr = coercionExpr[1].EnumerateArray().ToArray();
        Assert.True(getExpr.Length >= 2);
        Assert.Equal("get", getExpr[0].GetString());
        Assert.Equal(field, getExpr[1].GetString());
    }

    [Fact]
    public void ServerEvaluator_UniqueValue_NullValue_RendersFallbackColor()
    {
        // Regression: a feature whose classified field key exists but has a null value
        // must render with the fallback color, not match an empty-string category.
        // to-string(null) → "" would match a "" stop without the non-null guard.
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);

        var suggestion = new Core.Features.Styling.Domain.StyleSuggestion
        {
            LayerId = 1,
            GeometryType = GeometryType.Polygon,
            SuggestedField = new Core.Features.Styling.Domain.FieldSuggestion
            {
                Name = "category",
                Type = "String",
                Reason = "test",
                Profile = new Core.Features.Styling.Domain.FieldProfile
                {
                    FieldName = "category",
                    FieldType = "String",
                    TotalCount = 100,
                    NullCount = 5,
                    DistinctCount = 3,
                    SampleValues = [new Core.Features.Styling.Domain.SampleValue("", 10), new Core.Features.Styling.Domain.SampleValue("A", 40), new Core.Features.Styling.Domain.SampleValue("B", 45)]
                }
            },
            Classification = new Core.Features.Styling.Domain.ClassificationResult
            {
                Method = Core.Features.Styling.Domain.ClassificationMethod.UniqueValue,
                FieldName = "category",
                Categories = ["", "A", "B"],
                ClassCount = 3
            },
            PaletteName = "CartoBold",
            PaletteColors = ["#E41A1C", "#377EB8", "#4DAF4A"],
            Legend = new Core.Features.Styling.Domain.LegendInfo
            {
                Title = "category",
                Entries = []
            },
            Observations = [],
            Edition = Core.Features.Licensing.Domain.HonuaEdition.Pro
        };

        var mapLibreStyle = StyleSuggestionGenerator.GenerateMapLibreStyle(layer, suggestion);
        var json = StyleJsonUtilities.Serialize(mapLibreStyle);

        using var doc = JsonDocument.Parse(json);
        var fillLayer = FindLayer(doc.RootElement, "fill");
        var colorExprJson = fillLayer.GetProperty("paint").GetProperty("fill-color").GetRawText();

        var expr = Honua.Server.Features.Infrastructure.Rendering.MapLibreExpressionParser.Parse(colorExprJson);

        // Key exists but value is null — must hit fallback, not the "" category
        var propsNull = System.Collections.Immutable.ImmutableDictionary<string, object?>.Empty
            .Add("category", null);
        var resultNull = Honua.Server.Features.Infrastructure.Rendering.ExpressionEvaluator.Evaluate(expr, propsNull);
        Assert.Equal("#CCCCCC", resultNull?.ToString());

        // Key missing entirely — must also hit fallback
        var propsMissing = System.Collections.Immutable.ImmutableDictionary<string, object?>.Empty;
        var resultMissing = Honua.Server.Features.Infrastructure.Rendering.ExpressionEvaluator.Evaluate(expr, propsMissing);
        Assert.Equal("#CCCCCC", resultMissing?.ToString());

        // Non-null value "A" — must match category color
        var propsA = System.Collections.Immutable.ImmutableDictionary<string, object?>.Empty
            .Add("category", "A");
        var resultA = Honua.Server.Features.Infrastructure.Rendering.ExpressionEvaluator.Evaluate(expr, propsA);
        Assert.Equal("#377EB8", resultA?.ToString());
    }

    [Fact]
    public void GeoServicesToMapLibre_UniqueValue_EmitsCaseNullGuard()
    {
        // Regression: converted unique-value renderers must wrap the match expression
        // in a case/non-null guard, matching the class-breaks pattern.
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "uniqueValue",
            "field1": "status",
            "defaultSymbol": { "type": "esriSFS", "style": "esriSFSSolid", "color": [204, 204, 204, 255] },
            "uniqueValueInfos": [
              { "value": "active", "symbol": { "type": "esriSFS", "style": "esriSFSSolid", "color": [0, 255, 0, 255] } },
              { "value": "inactive", "symbol": { "type": "esriSFS", "style": "esriSFSSolid", "color": [255, 0, 0, 255] } }
            ]
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var styleJson = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);
        using var styleDoc = JsonDocument.Parse(styleJson);
        var fillLayer = FindLayer(styleDoc.RootElement, "fill");
        var expression = fillLayer.GetProperty("paint").GetProperty("fill-color");

        var items = expression.EnumerateArray().ToArray();
        Assert.Equal("case", items[0].GetString());
        // items[1] = guard, items[2] = match expression, items[3] = fallback
        Assert.Equal(JsonValueKind.Array, items[2].ValueKind);

        var matchItems = items[2].EnumerateArray().ToArray();
        Assert.Equal("match", matchItems[0].GetString());
    }

    [Fact]
    public void MapLibreToGeoServices_CaseWrappedMatchWithFallback_EmitsDefaultSymbol()
    {
        // Regression: when the MapLibre style has a ["case", guard, match, fallback]
        // expression, the GeoServices uniqueValue renderer must include a defaultSymbol
        // whose color matches the case fallback.
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);
        const string mapLibreJson = """
        {
          "layers": [
            {
              "type": "fill",
              "paint": {
                "fill-color": ["case", ["all", ["has", "status"], ["!=", ["typeof", ["get", "status"]], "null"]], ["match", ["to-string", ["get", "status"]], "active", "rgba(0,255,0,1)", "inactive", "rgba(255,0,0,1)", "rgba(204,204,204,1)"], "rgba(204,204,204,1)"]
              }
            }
          ]
        }
        """;

        var drawingInfoJson = MapLibreToGeoServicesConverter.Convert(mapLibreJson, layer);
        using var doc = JsonDocument.Parse(drawingInfoJson);
        var renderer = doc.RootElement.GetProperty("renderer");

        Assert.Equal("uniqueValue", renderer.GetProperty("type").GetString());
        Assert.True(renderer.TryGetProperty("defaultSymbol", out var defaultSymbol),
            "uniqueValue renderer must include defaultSymbol when case fallback is present");

        // Verify the defaultSymbol color matches RGB(204, 204, 204)
        var color = defaultSymbol.GetProperty("color");
        Assert.Equal(204, color[0].GetInt32());
        Assert.Equal(204, color[1].GetInt32());
        Assert.Equal(204, color[2].GetInt32());

        Assert.Equal("Other", renderer.GetProperty("defaultLabel").GetString());
    }

    [Fact]
    public void MapLibreToGeoServices_CustomGuardedCaseWrappedMatch_FallsBackToSimpleRenderer()
    {
        // Regression: arbitrary case predicates must not be unwrapped into uniqueValue,
        // or the converted renderer would silently drop the outer condition.
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);
        const string mapLibreJson = """
        {
          "layers": [
            {
              "type": "fill",
              "paint": {
                "fill-color": ["case", ["==", ["get", "eligible"], true], ["match", ["to-string", ["get", "status"]], "active", "rgba(0,255,0,1)", "inactive", "rgba(255,0,0,1)", "rgba(204,204,204,1)"], "rgba(204,204,204,1)"]
              }
            }
          ]
        }
        """;

        var drawingInfoJson = MapLibreToGeoServicesConverter.Convert(mapLibreJson, layer);
        using var doc = JsonDocument.Parse(drawingInfoJson);
        var renderer = doc.RootElement.GetProperty("renderer");

        Assert.Equal("simple", renderer.GetProperty("type").GetString());
    }

    [Fact]
    public void DrawingInfoRoundTrip_UniqueValue_PreservesCaseGuard()
    {
        // Roundtrip: GeoServices → MapLibre → GeoServices must preserve
        // the unique-value structure through the case null guard.
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "uniqueValue",
            "field1": "type",
            "defaultSymbol": { "type": "esriSFS", "style": "esriSFSSolid", "color": [128, 128, 128, 255] },
            "uniqueValueInfos": [
              { "value": "residential", "symbol": { "type": "esriSFS", "style": "esriSFSSolid", "color": [0, 128, 0, 255] } },
              { "value": "commercial", "symbol": { "type": "esriSFS", "style": "esriSFSSolid", "color": [0, 0, 255, 255] } }
            ]
          }
        }
        """;

        // Forward: GeoServices → MapLibre
        using var srcDoc = JsonDocument.Parse(drawingInfoJson);
        var mapLibreJson = GeoServicesToMapLibreConverter.Convert(srcDoc.RootElement, layer);

        // Reverse: MapLibre → GeoServices
        var resultJson = MapLibreToGeoServicesConverter.Convert(mapLibreJson, layer);
        using var resultDoc = JsonDocument.Parse(resultJson);
        var renderer = resultDoc.RootElement.GetProperty("renderer");

        Assert.Equal("uniqueValue", renderer.GetProperty("type").GetString());
        Assert.Equal("type", renderer.GetProperty("field1").GetString());

        var infos = renderer.GetProperty("uniqueValueInfos");
        Assert.Equal(2, infos.GetArrayLength());

        Assert.True(renderer.TryGetProperty("defaultSymbol", out _),
            "Roundtripped uniqueValue must preserve defaultSymbol from case fallback");
        Assert.Equal("Other", renderer.GetProperty("defaultLabel").GetString());
    }

    [Theory]
    [InlineData("42", "#CCCCCC")]    // string "42" → typeof "string" → guard rejects → fallback
    [InlineData("1.5", "#CCCCCC")]   // string "1.5" → typeof "string" → guard rejects → fallback
    public void ServerEvaluator_ClassBreaks_StringTypedNumericValue_RendersFallbackColor(string stringValue, string expectedColor)
    {
        // Regression for review finding: JSONB fields that store numeric values as
        // strings (e.g. {"pop": "42"} not {"pop": 42}) have typeof == "string" on
        // the tile.  The numeric guard (typeof == "number") must reject them so they
        // render with the fallback color.  Profiling must likewise only classify
        // native JSONB numbers, not numeric-looking strings — so this scenario
        // should never arise for correctly-typed data.  This test documents the
        // server evaluator's behavior for the edge case of mistyped JSONB data.
        var layer = LayerDefinition.CreateBasic(1, "polygons", GeometryType.Polygon);

        var suggestion = new Core.Features.Styling.Domain.StyleSuggestion
        {
            LayerId = 1,
            GeometryType = GeometryType.Polygon,
            SuggestedField = new Core.Features.Styling.Domain.FieldSuggestion
            {
                Name = "population",
                Type = "Double",
                Reason = "test",
                Profile = new Core.Features.Styling.Domain.FieldProfile
                {
                    FieldName = "population",
                    FieldType = "Double",
                    TotalCount = 100,
                    NullCount = 0,
                    DistinctCount = 50,
                    MinValue = 0.0,
                    MaxValue = 100.0
                }
            },
            Classification = new Core.Features.Styling.Domain.ClassificationResult
            {
                Method = Core.Features.Styling.Domain.ClassificationMethod.EqualInterval,
                FieldName = "population",
                Breaks = [50.0],
                ClassCount = 2
            },
            PaletteName = "Viridis",
            PaletteColors = ["#440154", "#FDE725"],
            Legend = new Core.Features.Styling.Domain.LegendInfo
            {
                Title = "population",
                Entries = []
            },
            Observations = [],
            Edition = Core.Features.Licensing.Domain.HonuaEdition.Pro
        };

        var mapLibreStyle = StyleSuggestionGenerator.GenerateMapLibreStyle(layer, suggestion);
        var json = StyleJsonUtilities.Serialize(mapLibreStyle);

        using var doc = JsonDocument.Parse(json);
        var fillLayer = FindLayer(doc.RootElement, "fill");
        var colorExprJson = fillLayer.GetProperty("paint").GetProperty("fill-color").GetRawText();

        var expr = Honua.Server.Features.Infrastructure.Rendering.MapLibreExpressionParser.Parse(colorExprJson);
        // String-typed numeric value: "42" not 42 — simulates mistyped JSONB
        var props = System.Collections.Immutable.ImmutableDictionary<string, object?>.Empty
            .Add("population", stringValue);

        var result = Honua.Server.Features.Infrastructure.Rendering.ExpressionEvaluator.Evaluate(expr, props);

        Assert.Equal(expectedColor, result?.ToString());
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
