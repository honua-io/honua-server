// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
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
    public void ApplyTheme_PrintProfile_ForcesExpressionOpacityToOne()
    {
        // Regression: the normalizer accepts opacity expressions, so the print
        // theme must coerce expression-typed opacity values to the scalar 1.0
        // alongside scalar opacity values — otherwise a valid style with an
        // expression-typed fill-opacity remains semi-transparent under print.
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
              "paint": {
                "fill-color": "#aabbcc",
                "fill-opacity": [
                  "case",
                  ["==", ["get", "highlight"], true],
                  0.9,
                  0.3
                ]
              }
            }
          ]
        }
        """;

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Print);

        using var doc = JsonDocument.Parse(result);
        var fill = FindLayer(doc.RootElement, "fill");
        var fillOpacity = fill.GetProperty("paint").GetProperty("fill-opacity");
        Assert.Equal(JsonValueKind.Number, fillOpacity.ValueKind);
        Assert.Equal(1d, fillOpacity.GetDouble(), 5);
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
    public void ApplyTheme_PrintProfile_ForcesExpressionLineColorToBlack()
    {
        // Regression: the normalizer accepts expression-typed line-color, so the
        // print theme must coerce expression-valued line-color to #000000
        // alongside scalar line-color values — otherwise a valid line-color
        // match/step/case expression remains colored under ?theme=print despite
        // the documented "line colors are black" contract.
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
              "paint": {
                "line-color": [
                  "match",
                  ["to-string", ["get", "category"]],
                  "A", "#cc8844",
                  "B", "#3366aa",
                  "#aabbcc"
                ],
                "line-width": 2
              }
            }
          ]
        }
        """;

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Print);

        using var doc = JsonDocument.Parse(result);
        var line = FindLayer(doc.RootElement, "line");
        var lineColor = line.GetProperty("paint").GetProperty("line-color");
        Assert.Equal(JsonValueKind.String, lineColor.ValueKind);
        Assert.Equal("#000000", lineColor.GetString());
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
    public void ApplyTheme_DarkProfile_PreservesGetExpressionFieldName()
    {
        // Regression: the generic walker used to recurse into every nested
        // expression array and rewrite any string that parsed as a color.
        // Because the named-color set includes common words like "red",
        // "white", and "blue", a valid data-driven binding such as
        // ["get", "red"] (read the "red" feature property) had its field
        // name rewritten to a themed hex literal under dark /
        // colorblind-safe themes, breaking the binding.  The walker now
        // skips get / has / feature-state operators entirely.
        const string json = """
        {
          "version": 8,
          "name": "test",
          "sources": {"layer-1": {"type": "vector"}},
          "layers": [{
            "id": "layer-1-fill",
            "type": "fill",
            "source": "layer-1",
            "paint": {"fill-color": ["get", "red"]}
          }]
        }
        """;

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Dark);

        using var doc = JsonDocument.Parse(result);
        var fill = FindLayer(doc.RootElement, "fill");
        var fillColor = fill.GetProperty("paint").GetProperty("fill-color");
        Assert.Equal(JsonValueKind.Array, fillColor.ValueKind);
        var operands = fillColor.EnumerateArray().ToArray();
        Assert.Equal(2, operands.Length);
        Assert.Equal("get", operands[0].GetString());
        Assert.Equal("red", operands[1].GetString());
    }

    [Fact]
    public void ApplyTheme_ColorblindSafeProfile_PreservesHasExpressionFieldName()
    {
        // Regression mirror of the get-field-name guard for the has
        // operator: ["has", "white"] is a valid feature-property predicate
        // and "white" must not be rewritten as a themed palette slot.
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
                ["has", "white"],
                "#cc8844",
                "#aabbcc"
              ]
            }
          }]
        }
        """;

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.ColorblindSafe);

        // Predicate field name is preserved verbatim.
        Assert.Contains("\"white\"", result);
        // Branch outputs are still themed.
        Assert.DoesNotContain("\"#cc8844\"", result);
        Assert.DoesNotContain("\"#aabbcc\"", result);
    }

    [Fact]
    public void ApplyTheme_DarkProfile_PreservesFeatureStateFieldName()
    {
        // Regression mirror for feature-state: state property names share
        // the same skip-the-operands contract as get / has.
        const string json = """
        {
          "version": 8,
          "name": "test",
          "sources": {"layer-1": {"type": "vector"}},
          "layers": [{
            "id": "layer-1-fill",
            "type": "fill",
            "source": "layer-1",
            "paint": {"fill-color": ["feature-state", "blue"]}
          }]
        }
        """;

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Dark);

        using var doc = JsonDocument.Parse(result);
        var fill = FindLayer(doc.RootElement, "fill");
        var fillColor = fill.GetProperty("paint").GetProperty("fill-color");
        Assert.Equal(JsonValueKind.Array, fillColor.ValueKind);
        var operands = fillColor.EnumerateArray().ToArray();
        Assert.Equal(2, operands.Length);
        Assert.Equal("feature-state", operands[0].GetString());
        Assert.Equal("blue", operands[1].GetString());
    }

    [Fact]
    public void ApplyTheme_DarkProfile_PreservesNestedGetInsideInterpolate()
    {
        // Regression: a nested ["get", "red"] inside an interpolate stop-output
        // position must keep its field name even though the surrounding
        // interpolate falls through to the generic walker.
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
                "interpolate",
                ["linear"],
                ["zoom"],
                0, ["get", "red"],
                10, "#aabbcc"
              ]
            }
          }]
        }
        """;

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Dark);

        // Field name "red" inside the nested get expression is preserved.
        Assert.Contains("\"red\"", result);
        Assert.Contains("\"get\"", result);
        // The hex literal at the second stop is themed.
        Assert.DoesNotContain("\"#aabbcc\"", result);
    }

    [Fact]
    public void ApplyTheme_DarkProfile_TransformsCssNamedColorLiterals()
    {
        // Regression: the normalizer accepts CSS / X11 named color literals
        // (e.g. "red", "transparent") via MapLibreStyleNormalizer.
        // TryParseMapLibreColor previously only handled hex and rgb/rgba and
        // therefore left stored named colors unthemed.  Theme transforms must
        // resolve the same set the normalizer accepts.
        const string json = """
        {
          "version": 8,
          "name": "test",
          "sources": {"layer-1": {"type": "vector"}},
          "layers": [{
            "id": "layer-1-fill",
            "type": "fill",
            "source": "layer-1",
            "paint": {"fill-color": "red"}
          }]
        }
        """;

        var logger = new RecordingLogger();
        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Dark, logger, layerId: 7);

        using var doc = JsonDocument.Parse(result);
        var fill = FindLayer(doc.RootElement, "fill");
        var fillColor = fill.GetProperty("paint").GetProperty("fill-color").GetString();
        Assert.NotNull(fillColor);
        Assert.NotEqual("red", fillColor);
        // Resolves to a hex literal after the dark transform; the parser round-trips.
        Assert.True(StyleJsonUtilities.TryParseMapLibreColor(fillColor, out _));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void ApplyTheme_PrintProfile_TransformsCssNamedLineColor()
    {
        const string json = """
        {
          "version": 8,
          "name": "test",
          "sources": {"layer-1": {"type": "vector"}},
          "layers": [{
            "id": "layer-1-line",
            "type": "line",
            "source": "layer-1",
            "paint": {"line-color": "crimson", "line-width": 1}
          }]
        }
        """;

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Print);

        using var doc = JsonDocument.Parse(result);
        var line = FindLayer(doc.RootElement, "line");
        Assert.Equal("#000000", line.GetProperty("paint").GetProperty("line-color").GetString());
    }

    [Theory]
    [InlineData("hsl(120, 100%, 50%)")]
    [InlineData("hsl(120 100% 50%)")]
    [InlineData("hsl(120deg 100% 50%)")]
    [InlineData("hsla(120, 100%, 50%, 0.5)")]
    [InlineData("hsl(120 100% 50% / 50%)")]
    public void TryParseMapLibreColor_HslLiteralForms_ParseToExpectedRgb(string input)
    {
        // Regression: the admin write-time normalizer
        // (MapLibreStyleNormalizer.IsValidColorLiteral) accepts hsl/hsla in
        // both legacy comma syntax and CSS Color Module Level 4 modern
        // space-separated syntax with a `/`-separated alpha.
        // TryParseMapLibreColor previously skipped every hsl/hsla form, so a
        // stored "hsl(120 100% 50%)" was treated as malformed and emitted
        // event 6403 under ?theme=dark|colorblind-safe|print instead of
        // being themed.
        Assert.True(StyleJsonUtilities.TryParseMapLibreColor(input, out var color));
        // hsl(120, 100%, 50%) ≡ pure green (#00ff00).
        Assert.Equal(0, color.R);
        Assert.Equal(255, color.G);
        Assert.Equal(0, color.B);
    }

    [Theory]
    [InlineData("hsl(0deg 100% 50%)")]
    [InlineData("hsl(0grad 100% 50%)")]
    [InlineData("hsl(0rad 100% 50%)")]
    [InlineData("hsl(0turn 100% 50%)")]
    [InlineData("hsl(360deg 100% 50%)")]
    [InlineData("hsl(1turn 100% 50%)")]
    public void TryParseMapLibreColor_HslHueUnits_NormalizeToZeroDegreesRed(string input)
    {
        // 0 in any hue unit = red.  360deg and 1turn wrap around to 0 via
        // NormalizeHueDegrees.  Verifies grad/rad/turn unit parsing and the
        // mod-360 wraparound without leaning on irrational unit conversions.
        Assert.True(StyleJsonUtilities.TryParseMapLibreColor(input, out var color));
        Assert.Equal(255, color.R);
        Assert.Equal(0, color.G);
        Assert.Equal(0, color.B);
    }

    [Theory]
    [InlineData("rgb(255 0 0)", 255, 0, 0, 255)]
    [InlineData("rgb(255 0 0 / 0.5)", 255, 0, 0, 127)]
    [InlineData("rgb(255 0 0 / 50%)", 255, 0, 0, 127)]
    [InlineData("rgba(10 20 30 / 1.0)", 10, 20, 30, 255)]
    public void TryParseMapLibreColor_ModernRgbLiteralForms_AcceptSpaceAndSlashSyntax(
        string input,
        int expectedR,
        int expectedG,
        int expectedB,
        int expectedA)
    {
        // Regression: the admin normalizer accepts rgb()/rgba() in CSS Color
        // Module Level 4 modern syntax (space-separated channels, `/` alpha)
        // via SplitCssFunctionArguments, but TryParseMapLibreColor's old
        // comma-only split rejected those forms — so a stored
        // "rgb(255 0 0 / 0.5)" was treated as malformed under ?theme=...
        Assert.True(StyleJsonUtilities.TryParseMapLibreColor(input, out var color));
        Assert.Equal((byte)expectedR, color.R);
        Assert.Equal((byte)expectedG, color.G);
        Assert.Equal((byte)expectedB, color.B);
        Assert.Equal((byte)expectedA, color.A);
    }

    [Fact]
    public void ApplyTheme_DarkProfile_TransformsHslLiteralWithoutLoggingFailure()
    {
        // Regression mirror of TransformsCssNamedColorLiterals for hsl():
        // a stored hsl color must be themed (not skipped) under ?theme=dark.
        const string json = """
        {
          "version": 8,
          "name": "test",
          "sources": {"layer-1": {"type": "vector"}},
          "layers": [{
            "id": "layer-1-fill",
            "type": "fill",
            "source": "layer-1",
            "paint": {"fill-color": "hsl(120 100% 50%)"}
          }]
        }
        """;

        var logger = new RecordingLogger();
        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Dark, logger, layerId: 11);

        using var doc = JsonDocument.Parse(result);
        var fill = FindLayer(doc.RootElement, "fill");
        var fillColor = fill.GetProperty("paint").GetProperty("fill-color").GetString();
        Assert.NotNull(fillColor);
        Assert.NotEqual("hsl(120 100% 50%)", fillColor);
        Assert.True(StyleJsonUtilities.TryParseMapLibreColor(fillColor, out _));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void ApplyTheme_DarkProfile_PreservesConcatStringOperands()
    {
        // Regression: ["concat", str1, str2, ...] returns a concatenated string.
        // Operands are string-construction inputs, never color outputs.  A
        // generic walker that themed any operand parsing as a color would
        // rewrite "red" inside ["concat", "red", "fish"], producing
        // ["concat", "<themed-hex>", "fish"] and changing the resulting string
        // from "redfish" to "<themed-hex>fish".  The walker now skips the
        // entire concat expression.
        const string json = """
        {
          "version": 8,
          "name": "test",
          "sources": {"layer-1": {"type": "vector"}},
          "layers": [{
            "id": "layer-1-fill",
            "type": "fill",
            "source": "layer-1",
            "paint": {"fill-color": ["concat", "red", "fish"]}
          }]
        }
        """;

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Dark);

        using var doc = JsonDocument.Parse(result);
        var fill = FindLayer(doc.RootElement, "fill");
        var fillColor = fill.GetProperty("paint").GetProperty("fill-color");
        Assert.Equal(JsonValueKind.Array, fillColor.ValueKind);
        var operands = fillColor.EnumerateArray().ToArray();
        Assert.Equal(3, operands.Length);
        Assert.Equal("concat", operands[0].GetString());
        Assert.Equal("red", operands[1].GetString());
        Assert.Equal("fish", operands[2].GetString());
    }

    [Fact]
    public void ApplyTheme_DarkProfile_PreservesLiteralValueVerbatim()
    {
        // Regression: ["literal", value] produces value as a verbatim literal
        // and must not be themed.  Without the allow-list, ["literal", "red"]
        // had its "red" operand rewritten as a themed hex literal, defeating
        // the user's explicit "treat this as a literal" intent.
        const string json = """
        {
          "version": 8,
          "name": "test",
          "sources": {"layer-1": {"type": "vector"}},
          "layers": [{
            "id": "layer-1-fill",
            "type": "fill",
            "source": "layer-1",
            "paint": {"fill-color": ["literal", "red"]}
          }]
        }
        """;

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Dark);

        using var doc = JsonDocument.Parse(result);
        var fill = FindLayer(doc.RootElement, "fill");
        var fillColor = fill.GetProperty("paint").GetProperty("fill-color");
        Assert.Equal(JsonValueKind.Array, fillColor.ValueKind);
        var operands = fillColor.EnumerateArray().ToArray();
        Assert.Equal(2, operands.Length);
        Assert.Equal("literal", operands[0].GetString());
        Assert.Equal("red", operands[1].GetString());
    }

    [Fact]
    public void ApplyTheme_ColorblindSafeProfile_PreservesTopLevelComparatorOperands()
    {
        // Regression: ["==", left, right] is a boolean comparator.  Even when
        // it appears at the top of a color paint property (a malformed but
        // accepted input shape), its operands are predicate values that must
        // not be themed — "red" is being compared as a literal feature value,
        // not used as a color output.
        const string json = """
        {
          "version": 8,
          "name": "test",
          "sources": {"layer-1": {"type": "vector"}},
          "layers": [{
            "id": "layer-1-fill",
            "type": "fill",
            "source": "layer-1",
            "paint": {"fill-color": ["==", ["get", "category"], "red"]}
          }]
        }
        """;

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.ColorblindSafe);

        using var doc = JsonDocument.Parse(result);
        var fill = FindLayer(doc.RootElement, "fill");
        var fillColor = fill.GetProperty("paint").GetProperty("fill-color");
        Assert.Equal(JsonValueKind.Array, fillColor.ValueKind);
        var operands = fillColor.EnumerateArray().ToArray();
        Assert.Equal(3, operands.Length);
        Assert.Equal("==", operands[0].GetString());
        Assert.Equal("red", operands[2].GetString());
    }

    [Fact]
    public void ApplyTheme_DarkProfile_PreservesAllAndAnyOperands()
    {
        // Regression: ["all", expr1, expr2, ...] / ["any", expr1, expr2, ...]
        // are boolean combinators whose operands are predicate expressions.
        // A color-like string nested inside is a feature value or comparison
        // target, not a color output.  Skip the whole expression.
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
                "all",
                ["==", ["get", "category"], "red"],
                ["any", ["==", ["get", "tone"], "blue"], ["==", ["get", "tone"], "green"]]
              ]
            }
          }]
        }
        """;

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Dark);

        // Every nested feature-value comparison literal survives verbatim.
        Assert.Contains("\"red\"", result);
        Assert.Contains("\"blue\"", result);
        Assert.Contains("\"green\"", result);
        Assert.Contains("\"all\"", result);
        Assert.Contains("\"any\"", result);
        Assert.Contains("\"category\"", result);
        Assert.Contains("\"tone\"", result);
    }

    [Fact]
    public void ApplyTheme_ColorblindSafeProfile_TransformsCoalesceColorOutputs()
    {
        // ["coalesce", value1, ..., valueN] returns the first non-null operand,
        // so in a color paint context every operand is a possible color
        // output.  Direct color literals are themed; nested operators dispatch
        // through the operator-aware walker, so a nested ["get", "color"]
        // still has its field name preserved.  Uses the ColorblindSafe profile
        // because its palette swap remaps every input color regardless of
        // input lightness, unlike Dark's HSL-lightness invert which leaves
        // pure primaries (lightness 0.5) unchanged.
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
              "fill-color": ["coalesce", ["get", "color"], "#cc8844", "#aabbcc"]
            }
          }]
        }
        """;

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.ColorblindSafe);

        // Direct color literals are themed.
        Assert.DoesNotContain("\"#cc8844\"", result);
        Assert.DoesNotContain("\"#aabbcc\"", result);
        // Nested get's field name "color" is preserved verbatim.
        Assert.Contains("\"color\"", result);
        Assert.Contains("\"get\"", result);
        Assert.Contains("\"coalesce\"", result);
    }

    [Fact]
    public void ApplyTheme_DarkProfile_TransformsInterpolateOutputsAndPreservesInputs()
    {
        // ["interpolate", interpolation-spec, input, stop, output, ...] —
        // explicit operator-aware handler themes outputs at indices 4, 6, 8,
        // ... while leaving the interpolation spec, input expression, and
        // numeric stops untouched.  Stop colors are intentionally off-pure
        // (not lightness 0.5) so the dark-theme lightness invert produces
        // an observably different output.
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
                "interpolate",
                ["linear"],
                ["zoom"],
                0, "#cc8844",
                10, "#3366aa",
                20, "#aabbcc"
              ]
            }
          }]
        }
        """;

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Dark);

        // Outputs are themed.
        Assert.DoesNotContain("\"#cc8844\"", result);
        Assert.DoesNotContain("\"#3366aa\"", result);
        Assert.DoesNotContain("\"#aabbcc\"", result);
        // Operator tokens and structural inputs survive.
        Assert.Contains("\"interpolate\"", result);
        Assert.Contains("\"linear\"", result);
        Assert.Contains("\"zoom\"", result);
    }

    [Fact]
    public void ApplyTheme_DarkProfile_PreservesNestedConcatInsideCaseOutput()
    {
        // Regression: a case output that is itself a non-color-output
        // expression (e.g. ["concat", "red", "fish"] used as a malformed but
        // accepted color paint) must not have its operands themed when reached
        // via TransformOutputElement → TransformExpressionColors.
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
                ["has", "category"],
                ["concat", "red", "fish"],
                "#aabbcc"
              ]
            }
          }]
        }
        """;

        var result = StyleThemeTransformer.ApplyTheme(json, ThemeProfile.Dark);

        // Concat operands preserved verbatim (the nested expression is skipped
        // entirely once the case walker dispatches into its operator handler).
        Assert.Contains("\"red\"", result);
        Assert.Contains("\"fish\"", result);
        // Case fallback hex literal is themed.
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
