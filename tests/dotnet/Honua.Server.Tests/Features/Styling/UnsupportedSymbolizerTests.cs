// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Styling.Domain;
using Honua.Server.Features.Infrastructure.Styling;

namespace Honua.Server.Tests.Features.Styling;

[Trait("Category", "Unit")]
[Trait("Component", "Styling")]
[Trait("Feature", "UnsupportedSymbolizer")]
public class UnsupportedSymbolizerTests
{
    [Fact]
    public void Convert_UnknownRendererType_ReturnsStableUnsupportedCode()
    {
        var layer = LayerDefinition.CreateBasic(1, "points", GeometryType.Point);
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
        var layer = LayerDefinition.CreateBasic(1, "lines", GeometryType.LineString);
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
        var layer = LayerDefinition.CreateBasic(1, "points", GeometryType.Point);
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
}
