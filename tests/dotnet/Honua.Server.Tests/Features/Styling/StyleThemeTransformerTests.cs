// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Styling.Domain;
using Honua.Server.Features.Infrastructure.Styling;

namespace Honua.Server.Tests.Features.Styling;

[Trait("Category", "Unit")]
[Trait("Component", "Styling")]
[Trait("Feature", "StyleTheme")]
public class StyleThemeTransformerTests
{
    [Fact]
    public void ApplyTheme_DefaultProfile_ReturnsInputUnchanged()
    {
        var json = BuildPolygonStyleJson("#aabbcc");

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Default);

        Assert.Equal(json, result);
    }

    [Fact]
    public void ApplyTheme_DarkProfile_IsDeterministic()
    {
        var json = BuildPolygonStyleJson("#cc8844");

        var first = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Dark);
        var second = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Dark);

        Assert.Equal(first, second);
        Assert.NotEqual(json, first);
    }

    [Fact]
    public void ApplyTheme_DarkProfile_InvertsLightnessOnFillColor()
    {
        var json = BuildPolygonStyleJson("#cc8844");

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Dark);

        using var doc = JsonDocument.Parse(result);
        var fill = FindLayer(doc.RootElement, "fill");
        var fillColor = fill.GetProperty("paint").GetProperty("fill-color").GetString();
        Assert.NotNull(fillColor);
        Assert.NotEqual("#cc8844", fillColor);

        // Verify it parses back as a valid hex color and that lightness inverted.
        Assert.True(StyleJsonUtilities.TryParseMapLibreColor(fillColor, out var transformed));
        Assert.True(StyleJsonUtilities.TryParseMapLibreColor("#cc8844", out var original));
        var originalLightness = (Math.Max(Math.Max(original.R, original.G), original.B)
            + Math.Min(Math.Min(original.R, original.G), original.B)) / 2d;
        var transformedLightness = (Math.Max(Math.Max(transformed.R, transformed.G), transformed.B)
            + Math.Min(Math.Min(transformed.R, transformed.G), transformed.B)) / 2d;
        Assert.True(transformedLightness + originalLightness < 256d * 1.5d);
    }

    [Fact]
    public void ApplyTheme_ColorblindSafeProfile_RemapsColorsOntoPaletteAndIsDeterministic()
    {
        var json = BuildPolygonStyleJson("#ff0000");

        var first = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.ColorblindSafe);
        var second = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.ColorblindSafe);

        Assert.Equal(first, second);

        using var doc = JsonDocument.Parse(first);
        var fill = FindLayer(doc.RootElement, "fill");
        var fillColor = fill.GetProperty("paint").GetProperty("fill-color").GetString();
        Assert.NotNull(fillColor);
        Assert.NotEqual("#ff0000", fillColor);
    }

    [Fact]
    public void ApplyTheme_PrintProfile_ForcesOpacityToOne()
    {
        const string json = """
        {
          "version": 8,
          "name": "test",
          "sources": {"layer-1": {"type": "vector"}},
          "layers": [
            {
              "id": "layer-1-fill",
              "type": "fill",
              "source": "layer-1",
              "paint": {"fill-color": "#aabbcc", "fill-opacity": 0.5}
            }
          ]
        }
        """;

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Print);

        using var doc = JsonDocument.Parse(result);
        var fill = FindLayer(doc.RootElement, "fill");
        Assert.Equal(1d, fill.GetProperty("paint").GetProperty("fill-opacity").GetDouble(), 5);
    }

    [Fact]
    public void ApplyTheme_PrintProfile_ForcesLineColorToBlack()
    {
        const string json = """
        {
          "version": 8,
          "name": "test",
          "sources": {"layer-1": {"type": "vector"}},
          "layers": [
            {
              "id": "layer-1-line",
              "type": "line",
              "source": "layer-1",
              "paint": {"line-color": "#ff0000", "line-width": 2}
            }
          ]
        }
        """;

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Print);

        using var doc = JsonDocument.Parse(result);
        var line = FindLayer(doc.RootElement, "line");
        Assert.Equal("#000000", line.GetProperty("paint").GetProperty("line-color").GetString());
    }

    [Fact]
    public void ApplyTheme_MalformedJson_ReturnsInputUnchanged()
    {
        const string json = "{not-json";

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Dark);

        Assert.Equal(json, result);
    }

    private static string BuildPolygonStyleJson(string fillColor) =>
        "{\"version\":8,\"name\":\"test\","
        + "\"sources\":{\"layer-1\":{\"type\":\"vector\"}},"
        + "\"layers\":[{\"id\":\"layer-1-fill\",\"type\":\"fill\",\"source\":\"layer-1\","
        + "\"paint\":{\"fill-color\":\"" + fillColor + "\"}}]}";

    private static JsonElement FindLayer(JsonElement root, string layerType)
    {
        foreach (var layer in root.GetProperty("layers").EnumerateArray())
        {
            if (layer.GetProperty("type").GetString() == layerType)
            {
                return layer;
            }
        }
        throw new Xunit.Sdk.XunitException($"No layer of type '{layerType}' found.");
    }
}
