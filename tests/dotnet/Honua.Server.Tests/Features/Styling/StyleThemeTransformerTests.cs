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
    public void ApplyTheme_ColorblindSafeProfile_PreservesInputAlpha()
    {
        // Regression: GeoServices conversion emits rgba(...) strings that can carry
        // sub-1.0 alpha (default polygon fill is rgba(45,105,165,0.4)).  The palette
        // swap must preserve the input alpha rather than forcing every paint
        // property to opaque.
        const string json = """
        {
          "version": 8,
          "name": "test",
          "sources": {"layer-1": {"type": "vector"}},
          "layers": [{
            "id": "layer-1-fill",
            "type": "fill",
            "source": "layer-1",
            "paint": {"fill-color": "rgba(255,0,0,0.4)"}
          }]
        }
        """;

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.ColorblindSafe);

        using var doc = JsonDocument.Parse(result);
        var fill = FindLayer(doc.RootElement, "fill");
        var fillColor = fill.GetProperty("paint").GetProperty("fill-color").GetString();
        Assert.NotNull(fillColor);
        Assert.True(StyleJsonUtilities.TryParseMapLibreColor(fillColor, out var transformed));
        // Input alpha 0.4 → byte 102; preserve within rounding.
        Assert.InRange((int)transformed.A, 100, 104);
    }

    [Fact]
    public void ApplyTheme_ColorblindSafeProfile_MapsIdenticalInputColorsToSamePaletteSlot()
    {
        // Regression: the colorblind-safe walker used to advance the palette index
        // for every visited color, so a classBreaks first-class color and a case
        // fallback that share an input color got mapped to different palette
        // slots.  Equal input colors must map to equal output colors within a
        // single ApplyTheme call.
        const string json = """
        {
          "version": 8,
          "name": "test",
          "sources": {"layer-1": {"type": "vector"}},
          "layers": [{
            "id": "layer-1-fill",
            "type": "fill",
            "source": "layer-1",
            "paint": {
              "fill-color": [
                "case",
                ["==", ["typeof", ["get", "magnitude"]], "number"],
                ["step", ["to-number", ["get", "magnitude"]], "rgba(204,136,68,0.4)", 5, "rgba(51,102,170,0.4)", 10, "rgba(170,187,204,0.4)"],
                "rgba(204,136,68,0.4)"
              ]
            }
          }]
        }
        """;

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.ColorblindSafe);

        // The first step output and the case fallback share rgba(204,136,68,0.4).
        // After the transform they must remain equal — find them in the output JSON
        // by parsing the emitted expression and comparing the two corresponding
        // positions.
        using var doc = JsonDocument.Parse(result);
        var fill = FindLayer(doc.RootElement, "fill");
        var caseExpr = fill.GetProperty("paint").GetProperty("fill-color");
        Assert.Equal(JsonValueKind.Array, caseExpr.ValueKind);

        // Layout: ["case", predicate, ["step", input, output0, stop, output1, ...], fallback]
        var stepExpr = caseExpr[2];
        Assert.Equal(JsonValueKind.Array, stepExpr.ValueKind);
        var firstStepOutput = stepExpr[2].GetString();
        var caseFallback = caseExpr[3].GetString();

        Assert.NotNull(firstStepOutput);
        Assert.NotNull(caseFallback);
        Assert.Equal(firstStepOutput, caseFallback);
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

    [Fact]
    public void ApplyTheme_DarkProfile_TransformsColorsInsideExpressionArrays()
    {
        const string json = """
        {
          "version": 8,
          "name": "test",
          "sources": {"layer-1": {"type": "vector"}},
          "layers": [{
            "id": "layer-1-fill",
            "type": "fill",
            "source": "layer-1",
            "paint": {
              "fill-color": [
                "case",
                ["!=", ["get", "category"], null],
                ["match", ["to-string", ["get", "category"]], "A", "#cc8844", "B", "#3366aa", "#aabbcc"],
                "#aabbcc"
              ]
            }
          }]
        }
        """;

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Dark);

        Assert.NotEqual(json, result);
        // Each original literal should have been replaced; the originals must not appear.
        Assert.DoesNotContain("\"#cc8844\"", result);
        Assert.DoesNotContain("\"#3366aa\"", result);
        Assert.DoesNotContain("\"#aabbcc\"", result);
        // Operator tokens like "case" and "match" must survive untouched.
        Assert.Contains("\"case\"", result);
        Assert.Contains("\"match\"", result);
        Assert.Contains("\"to-string\"", result);
        Assert.Contains("\"category\"", result);
    }

    [Fact]
    public void ApplyTheme_DarkProfile_ExpressionArrayTransformIsDeterministic()
    {
        const string json = """
        {
          "version": 8,
          "name": "test",
          "sources": {"layer-1": {"type": "vector"}},
          "layers": [{
            "id": "layer-1-circle",
            "type": "circle",
            "source": "layer-1",
            "paint": {
              "circle-color": ["case", ["!=", ["get", "level"], null], ["step", ["to-number", ["get", "level"]], "#aaaaaa", 5, "#cc4444"], "#aaaaaa"]
            }
          }]
        }
        """;

        var first = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Dark);
        var second = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Dark);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ApplyTheme_DarkProfile_PreservesColorLikeMatchInputLabels()
    {
        // Regression: a uniqueValue category whose feature value is itself a
        // color-like string (e.g. "#ff0000") must NOT be rewritten by the theme
        // walker — match input labels are feature values, not output colors.
        // Only the second element of each pair (the output color) and the
        // trailing fallback should be transformed.
        const string json = """
        {
          "version": 8,
          "name": "test",
          "sources": {"layer-1": {"type": "vector"}},
          "layers": [{
            "id": "layer-1-fill",
            "type": "fill",
            "source": "layer-1",
            "paint": {
              "fill-color": [
                "case",
                ["!=", ["get", "category"], null],
                ["match", ["to-string", ["get", "category"]], "#ff0000", "#cc8844", "#00ff00", "#3366aa", "#aabbcc"],
                "#aabbcc"
              ]
            }
          }]
        }
        """;

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Dark);

        // Input labels survive verbatim.
        Assert.Contains("\"#ff0000\"", result);
        Assert.Contains("\"#00ff00\"", result);

        // Output arms and fallback are transformed.
        Assert.DoesNotContain("\"#cc8844\"", result);
        Assert.DoesNotContain("\"#3366aa\"", result);
        Assert.DoesNotContain("\"#aabbcc\"", result);
    }

    [Fact]
    public void ApplyTheme_DarkProfile_PreservesNumericStepStops()
    {
        // Regression: numeric stops in `step` are not strings, but if a future
        // generator ever emits string-typed stops the operator-aware walker
        // still skips them (only outputs at indices 2, 4, 6, ... are visited).
        // This style mirrors the structure emitted by the classBreaks converter.
        const string json = """
        {
          "version": 8,
          "name": "test",
          "sources": {"layer-1": {"type": "vector"}},
          "layers": [{
            "id": "layer-1-fill",
            "type": "fill",
            "source": "layer-1",
            "paint": {
              "fill-color": [
                "case",
                ["==", ["typeof", ["get", "magnitude"]], "number"],
                ["step", ["to-number", ["get", "magnitude"]], "#cc8844", 5, "#3366aa", 10, "#aabbcc"],
                "#000000"
              ]
            }
          }]
        }
        """;

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Dark);

        Assert.DoesNotContain("\"#cc8844\"", result);
        Assert.DoesNotContain("\"#3366aa\"", result);
        Assert.DoesNotContain("\"#aabbcc\"", result);
        // Numeric stops survive (still 5 and 10 in the output).
        Assert.Contains("5", result);
        Assert.Contains("10", result);
        // Operator tokens survive.
        Assert.Contains("\"step\"", result);
        Assert.Contains("\"to-number\"", result);
        Assert.Contains("\"magnitude\"", result);
    }

    [Fact]
    public void ApplyTheme_DarkProfile_DoesNotRewriteCasePredicateColorLiterals()
    {
        // Regression: a case predicate that compares against a color-like
        // literal must keep the comparison value untouched — predicates are
        // skipped entirely by the operator-aware walker.
        const string json = """
        {
          "version": 8,
          "name": "test",
          "sources": {"layer-1": {"type": "vector"}},
          "layers": [{
            "id": "layer-1-fill",
            "type": "fill",
            "source": "layer-1",
            "paint": {
              "fill-color": [
                "case",
                ["==", ["get", "color_field"], "#ff0000"],
                "#cc8844",
                "#aabbcc"
              ]
            }
          }]
        }
        """;

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Dark);

        // Predicate comparison value preserved.
        Assert.Contains("\"#ff0000\"", result);
        // Outputs and fallback transformed.
        Assert.DoesNotContain("\"#cc8844\"", result);
        Assert.DoesNotContain("\"#aabbcc\"", result);
    }

    [Fact]
    public void ApplyTheme_DarkProfile_MalformedColorEmitsParseFailureLog()
    {
        const string json = """
        {
          "version": 8,
          "name": "test",
          "sources": {"layer-1": {"type": "vector"}},
          "layers": [{
            "id": "layer-1-fill",
            "type": "fill",
            "source": "layer-1",
            "paint": {"fill-color": "not-a-color"}
          }]
        }
        """;

        var logger = new RecordingLogger();
        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Dark, logger, layerId: 42);

        Assert.Contains("not-a-color", result);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(6403, entry.EventId);
        Assert.Contains("fill-color", entry.Message);
        Assert.Contains("not-a-color", entry.Message);
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(eventId.Id, formatter(state, exception)));
        }

        public sealed record LogEntry(int EventId, string Message);
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
