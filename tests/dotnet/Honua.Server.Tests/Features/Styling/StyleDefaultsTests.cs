// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Infrastructure.Styling;

namespace Honua.Server.Tests.Features.Styling;

[Trait("Category", "Unit")]
[Trait("Component", "Styling")]
[Trait("Feature", "StyleDefaults")]
public class StyleDefaultsTests
{
    [Theory]
    [InlineData(MetadataV2GeometryType.Point, "circle")]
    [InlineData(MetadataV2GeometryType.LineString, "line")]
    [InlineData(MetadataV2GeometryType.Polygon, "fill")]
    public void BuildDefaultMapLibreStyle_ProducesValidV8Document(MetadataV2GeometryType geometryType, string expectedLayerType)
    {
        var layer = new StyleLayerDescriptor(42, "sample", geometryType);

        var style = StyleDefaults.BuildDefaultMapLibreStyle(layer);
        var json = StyleJsonUtilities.Serialize(style);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(8, root.GetProperty("version").GetInt32());

        var layers = root.GetProperty("layers");
        Assert.True(layers.GetArrayLength() >= 1);

        var hasExpectedType = false;
        foreach (var entry in layers.EnumerateArray())
        {
            if (entry.GetProperty("type").GetString() == expectedLayerType)
            {
                hasExpectedType = true;
                break;
            }
        }
        Assert.True(hasExpectedType, $"Expected at least one layer of type '{expectedLayerType}'.");

        var sources = root.GetProperty("sources");
        Assert.Equal(JsonValueKind.Object, sources.ValueKind);
        Assert.Equal(JsonValueKind.Object, sources.EnumerateObject().Single().Value.ValueKind);
    }

    [Fact]
    public void GeoServicesSimpleRenderer_RoundTripsToValidV8Document()
    {
        var layer = new StyleLayerDescriptor(7, "districts", MetadataV2GeometryType.Polygon);
        const string drawingInfoJson = """
        {
          "renderer": {
            "type": "simple",
            "symbol": {
              "type": "esriSFS",
              "color": [120, 50, 200, 200],
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
        var conversion = GeoServicesToMapLibreConverter.Convert(doc.RootElement, layer);

        Assert.Empty(conversion.Unsupported);
        using var styleDoc = JsonDocument.Parse(conversion.MapLibreStyleJson);
        var root = styleDoc.RootElement;

        Assert.Equal(8, root.GetProperty("version").GetInt32());
        var layers = root.GetProperty("layers");

        var hasFill = false;
        foreach (var entry in layers.EnumerateArray())
        {
            if (entry.GetProperty("type").GetString() == "fill")
            {
                hasFill = true;
                break;
            }
        }
        Assert.True(hasFill);
    }
}
