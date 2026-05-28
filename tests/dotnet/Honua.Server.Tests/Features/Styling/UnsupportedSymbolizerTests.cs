// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Domain;
using Honua.Server.Features.Styling;

namespace Honua.Server.Tests.Features.Styling;

[Trait("Category", "Unit")]
[Trait("Component", "Styling")]
[Trait("Feature", "UnsupportedSymbolizer")]
public class UnsupportedSymbolizerTests
{
    [Fact]
    public void Convert_UnknownRendererType_ReturnsStableUnsupportedCode()
    {
        var layer = new StyleLayerDescriptor(1, "points", MetadataV2GeometryType.Point);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "heatmap",
            "field": "magnitude"
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var result = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.MapLibreStyleJson));
        Assert.Single(result.Unsupported);

        var entry = result.Unsupported[0];
        Assert.Equal(StyleErrorCodes.RendererTypeUnsupported, entry.Code);
        Assert.Equal("heatmap", entry.SymbolizerType);
        Assert.False(string.IsNullOrWhiteSpace(entry.Guidance));
    }

    [Fact]
    public void Convert_MissingRenderer_ReturnsPayloadIncompleteCode()
    {
        var layer = new StyleLayerDescriptor(1, "lines", MetadataV2GeometryType.LineString);
        const string drawingInfoJson = """
        {
          "transparency": 0
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var result = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        Assert.Single(result.Unsupported);
        Assert.Equal(StyleErrorCodes.RendererPayloadIncomplete, result.Unsupported[0].Code);
        Assert.False(string.IsNullOrEmpty(result.MapLibreStyleJson));
    }

    [Fact]
    public void Convert_SimpleRenderer_ReportsNoUnsupportedSymbolizers()
    {
        var layer = new StyleLayerDescriptor(1, "points", MetadataV2GeometryType.Point);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "simple",
            "symbol": {
              "type": "esriSMS",
              "color": [255, 0, 0, 255],
              "size": 12
            }
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var result = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        Assert.Empty(result.Unsupported);
    }

    [Fact]
    public void Convert_UnknownRendererType_StableCodeStringMatchesContract()
    {
        // Codes are part of the public API; this guard prevents accidental rename.
        Assert.Equal("RENDERER_TYPE_UNSUPPORTED", StyleErrorCodes.RendererTypeUnsupported);
        Assert.Equal("SYMBOL_TYPE_UNSUPPORTED", StyleErrorCodes.SymbolTypeUnsupported);
        Assert.Equal("PICTURE_MARKER_PARTIAL", StyleErrorCodes.PictureMarkerPartial);
        Assert.Equal("RENDERER_PAYLOAD_INCOMPLETE", StyleErrorCodes.RendererPayloadIncomplete);
    }

    [Fact]
    public void Convert_SimpleRenderer_UnsupportedSymbolType_ReportsSymbolTypeUnsupported()
    {
        var layer = new StyleLayerDescriptor(1, "points", MetadataV2GeometryType.Point);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "simple",
            "symbol": {
              "type": "esriTS",
              "color": [255, 0, 0, 255]
            }
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var result = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        var entry = Assert.Single(result.Unsupported);
        Assert.Equal(StyleErrorCodes.SymbolTypeUnsupported, entry.Code);
        Assert.Equal("esriTS", entry.SymbolizerType);
        Assert.False(string.IsNullOrEmpty(result.MapLibreStyleJson));
    }

    [Fact]
    public void Convert_UniqueValueRenderer_NonUniformPictureMarkers_ReportsPictureMarkerPartial()
    {
        var layer = new StyleLayerDescriptor(1, "points", MetadataV2GeometryType.Point);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "uniqueValue",
            "field1": "category",
            "uniqueValueInfos": [
              {
                "value": "A",
                "symbol": {
                  "type": "esriPMS",
                  "url": "https://example.invalid/icon-a.png",
                  "imageData": "QQ==",
                  "contentType": "image/png",
                  "xoffset": 0,
                  "yoffset": 0,
                  "angle": 0
                }
              },
              {
                "value": "B",
                "symbol": {
                  "type": "esriPMS",
                  "url": "https://example.invalid/icon-b.png",
                  "imageData": "Qg==",
                  "contentType": "image/png",
                  "xoffset": 12,
                  "yoffset": -4,
                  "angle": 45
                }
              }
            ]
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var result = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        Assert.Contains(result.Unsupported, info => info.Code == StyleErrorCodes.PictureMarkerPartial);
        Assert.False(string.IsNullOrEmpty(result.MapLibreStyleJson));
    }

    [Fact]
    public void Convert_UniqueValueRenderer_DivergentDefaultSymbolLayout_ReportsPictureMarkerPartial()
    {
        // Regression: stops are uniform (all zero offsets/angle) so the partial
        // diagnostic on stops alone would miss the layout drop.  The defaultSymbol
        // carries a divergent xoffset/yoffset/angle and the layout uniformity
        // check evaluates all images including defaultSymbol, so icon-offset /
        // icon-rotate would silently fail to be emitted unless the partial check
        // also sees the defaultSymbol payload.
        var layer = new StyleLayerDescriptor(1, "points", MetadataV2GeometryType.Point);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "uniqueValue",
            "field1": "category",
            "uniqueValueInfos": [
              {
                "value": "A",
                "symbol": {
                  "type": "esriPMS",
                  "url": "https://example.invalid/icon-a.png",
                  "imageData": "QQ==",
                  "contentType": "image/png",
                  "xoffset": 0,
                  "yoffset": 0,
                  "angle": 0
                }
              },
              {
                "value": "B",
                "symbol": {
                  "type": "esriPMS",
                  "url": "https://example.invalid/icon-b.png",
                  "imageData": "Qg==",
                  "contentType": "image/png",
                  "xoffset": 0,
                  "yoffset": 0,
                  "angle": 0
                }
              }
            ],
            "defaultSymbol": {
              "type": "esriPMS",
              "url": "https://example.invalid/icon-default.png",
              "imageData": "RA==",
              "contentType": "image/png",
              "xoffset": 5,
              "yoffset": -7,
              "angle": 30
            }
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var result = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        Assert.Contains(result.Unsupported, info => info.Code == StyleErrorCodes.PictureMarkerPartial);
    }

    [Fact]
    public void Convert_ClassBreaksRenderer_DivergentDefaultSymbolLayout_ReportsPictureMarkerPartial()
    {
        // Regression mirror of the uniqueValue case for picture-marker classBreaks:
        // stops are uniform but defaultSymbol carries divergent layout hints.
        var layer = new StyleLayerDescriptor(1, "points", MetadataV2GeometryType.Point);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "classBreaks",
            "field": "magnitude",
            "classBreakInfos": [
              {
                "classMaxValue": 5,
                "symbol": {
                  "type": "esriPMS",
                  "url": "https://example.invalid/icon-low.png",
                  "imageData": "QQ==",
                  "contentType": "image/png",
                  "xoffset": 0,
                  "yoffset": 0,
                  "angle": 0
                }
              },
              {
                "classMaxValue": 10,
                "symbol": {
                  "type": "esriPMS",
                  "url": "https://example.invalid/icon-high.png",
                  "imageData": "Qg==",
                  "contentType": "image/png",
                  "xoffset": 0,
                  "yoffset": 0,
                  "angle": 0
                }
              }
            ],
            "defaultSymbol": {
              "type": "esriPMS",
              "url": "https://example.invalid/icon-default.png",
              "imageData": "RA==",
              "contentType": "image/png",
              "xoffset": 9,
              "yoffset": 4,
              "angle": -15
            }
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var result = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        Assert.Contains(result.Unsupported, info => info.Code == StyleErrorCodes.PictureMarkerPartial);
    }

    [Fact]
    public void Convert_UniqueValueRenderer_MixedPictureAndColorSymbols_ReportsPictureMarkerPartial()
    {
        // Regression: a uniqueValue renderer with both esriPMS (image) and
        // esriSMS (color) stops previously caused TryGetPictureMarkerPayload to
        // return false on the first non-esriPMS stop.  The dispatcher then fell
        // through to the generic color path, which considers esriPMS supported
        // and silently dropped the image metadata.  The fix records
        // PICTURE_MARKER_PARTIAL when at least one esriPMS payload is present
        // but cannot be emitted as a clean picture-marker style, so the
        // no-silent-drop contract holds end-to-end.
        var layer = new StyleLayerDescriptor(1, "points", MetadataV2GeometryType.Point);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "uniqueValue",
            "field1": "category",
            "uniqueValueInfos": [
              {
                "value": "A",
                "symbol": {
                  "type": "esriPMS",
                  "url": "https://example.invalid/icon-a.png",
                  "imageData": "QQ==",
                  "contentType": "image/png"
                }
              },
              {
                "value": "B",
                "symbol": {
                  "type": "esriSMS",
                  "style": "esriSMSCircle",
                  "color": [10, 20, 30, 255]
                }
              }
            ]
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var result = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        var entry = Assert.Single(
            result.Unsupported,
            info => info.Code == StyleErrorCodes.PictureMarkerPartial);
        Assert.Equal("esriPMS", entry.SymbolizerType);
        Assert.Contains("uniqueValue", entry.Guidance, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(result.MapLibreStyleJson));
    }

    [Fact]
    public void Convert_ClassBreaksRenderer_MixedPictureAndColorSymbols_ReportsPictureMarkerPartial()
    {
        // Regression mirror of the uniqueValue mixed-symbol case for
        // picture-marker classBreaks.
        var layer = new StyleLayerDescriptor(1, "points", MetadataV2GeometryType.Point);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "classBreaks",
            "field": "magnitude",
            "classBreakInfos": [
              {
                "classMaxValue": 5,
                "symbol": {
                  "type": "esriPMS",
                  "url": "https://example.invalid/icon-low.png",
                  "imageData": "QQ==",
                  "contentType": "image/png"
                }
              },
              {
                "classMaxValue": 10,
                "symbol": {
                  "type": "esriSMS",
                  "style": "esriSMSCircle",
                  "color": [200, 30, 30, 255]
                }
              }
            ]
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var result = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        var entry = Assert.Single(
            result.Unsupported,
            info => info.Code == StyleErrorCodes.PictureMarkerPartial);
        Assert.Equal("esriPMS", entry.SymbolizerType);
        Assert.Contains("classBreaks", entry.Guidance, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(result.MapLibreStyleJson));
    }

    [Fact]
    public void Convert_UniqueValueRenderer_UniformStopsAndDefaultSymbol_DoesNotReportPictureMarkerPartial()
    {
        // Sanity counterpart: when stops AND defaultSymbol all share the same
        // (zero) layout hints the converter must NOT emit PICTURE_MARKER_PARTIAL.
        var layer = new StyleLayerDescriptor(1, "points", MetadataV2GeometryType.Point);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "uniqueValue",
            "field1": "category",
            "uniqueValueInfos": [
              {
                "value": "A",
                "symbol": {
                  "type": "esriPMS",
                  "url": "https://example.invalid/icon-a.png",
                  "imageData": "QQ==",
                  "contentType": "image/png",
                  "xoffset": 0,
                  "yoffset": 0,
                  "angle": 0
                }
              }
            ],
            "defaultSymbol": {
              "type": "esriPMS",
              "url": "https://example.invalid/icon-default.png",
              "imageData": "RA==",
              "contentType": "image/png",
              "xoffset": 0,
              "yoffset": 0,
              "angle": 0
            }
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var result = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        Assert.DoesNotContain(result.Unsupported, info => info.Code == StyleErrorCodes.PictureMarkerPartial);
    }

    [Fact]
    public void Convert_UniqueValueRenderer_UnsupportedNestedSymbolType_ReportsSymbolTypeUnsupported()
    {
        var layer = new StyleLayerDescriptor(1, "lines", MetadataV2GeometryType.LineString);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "uniqueValue",
            "field1": "category",
            "uniqueValueInfos": [
              {
                "value": "A",
                "symbol": {
                  "type": "esriCustomLine",
                  "color": [10, 20, 30, 255]
                }
              }
            ]
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var result = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        Assert.Contains(result.Unsupported, info =>
            info.Code == StyleErrorCodes.SymbolTypeUnsupported && info.SymbolizerType == "esriCustomLine");
    }

    [Fact]
    public void Convert_SimpleRenderer_MissingSymbol_ReportsPayloadIncomplete()
    {
        var layer = new StyleLayerDescriptor(1, "polys", MetadataV2GeometryType.Polygon);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "simple"
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var result = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        var entry = Assert.Single(result.Unsupported);
        Assert.Equal(StyleErrorCodes.RendererPayloadIncomplete, entry.Code);
        Assert.Equal("simple", entry.SymbolizerType);
        Assert.False(string.IsNullOrEmpty(result.MapLibreStyleJson));
    }

    [Fact]
    public void Convert_UniqueValueRenderer_MissingField_ReportsPayloadIncomplete()
    {
        var layer = new StyleLayerDescriptor(1, "polys", MetadataV2GeometryType.Polygon);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "uniqueValue",
            "uniqueValueInfos": []
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var result = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        var entry = Assert.Single(result.Unsupported);
        Assert.Equal(StyleErrorCodes.RendererPayloadIncomplete, entry.Code);
        Assert.Equal("uniqueValue", entry.SymbolizerType);
    }

    [Fact]
    public void Convert_UniqueValueRenderer_MissingInfos_ReportsPayloadIncomplete()
    {
        var layer = new StyleLayerDescriptor(1, "polys", MetadataV2GeometryType.Polygon);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "uniqueValue",
            "field1": "category"
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var result = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        var entry = Assert.Single(result.Unsupported);
        Assert.Equal(StyleErrorCodes.RendererPayloadIncomplete, entry.Code);
        Assert.Equal("uniqueValue", entry.SymbolizerType);
    }

    [Fact]
    public void Convert_UniqueValueRenderer_NoParseableEntries_ReportsPayloadIncomplete()
    {
        var layer = new StyleLayerDescriptor(1, "polys", MetadataV2GeometryType.Polygon);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "uniqueValue",
            "field1": "category",
            "uniqueValueInfos": [
              { "value": "A" }
            ]
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var result = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        var entry = Assert.Single(result.Unsupported);
        Assert.Equal(StyleErrorCodes.RendererPayloadIncomplete, entry.Code);
        Assert.Equal("uniqueValue", entry.SymbolizerType);
    }

    [Fact]
    public void Convert_ClassBreaksRenderer_MissingField_ReportsPayloadIncomplete()
    {
        var layer = new StyleLayerDescriptor(1, "polys", MetadataV2GeometryType.Polygon);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "classBreaks",
            "classBreakInfos": []
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var result = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        var entry = Assert.Single(result.Unsupported);
        Assert.Equal(StyleErrorCodes.RendererPayloadIncomplete, entry.Code);
        Assert.Equal("classBreaks", entry.SymbolizerType);
    }

    [Fact]
    public void Convert_ClassBreaksRenderer_MissingInfos_ReportsPayloadIncomplete()
    {
        var layer = new StyleLayerDescriptor(1, "polys", MetadataV2GeometryType.Polygon);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "classBreaks",
            "field": "magnitude"
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var result = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        var entry = Assert.Single(result.Unsupported);
        Assert.Equal(StyleErrorCodes.RendererPayloadIncomplete, entry.Code);
        Assert.Equal("classBreaks", entry.SymbolizerType);
    }

    [Fact]
    public void Convert_ClassBreaksRenderer_NoParseableEntries_ReportsPayloadIncomplete()
    {
        var layer = new StyleLayerDescriptor(1, "polys", MetadataV2GeometryType.Polygon);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "classBreaks",
            "field": "magnitude",
            "classBreakInfos": [
              { "classMaxValue": "not-a-number" }
            ]
          }
        }
        """;

        using var doc = JsonDocument.Parse(drawingInfoJson);
        var result = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        var entry = Assert.Single(result.Unsupported);
        Assert.Equal(StyleErrorCodes.RendererPayloadIncomplete, entry.Code);
        Assert.Equal("classBreaks", entry.SymbolizerType);
    }
}
