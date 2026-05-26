// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene.Generation;

/// <summary>
/// Unit tests for the style-metadata contract writer that translates a layer's
/// <see cref="Symbology3D"/> into the emitted <see cref="TileStyleSpec"/> and
/// its 3D Tiles Styling expressions.
/// </summary>
public sealed class TileStyleSpecWriterTests
{
    [UnitTest]
    public void Build_NoSymbology_EmitsDefaultMaterialOnlyNoExpressions()
    {
        var spec = TileStyleSpecWriter.Build(null);

        spec.Encoding.Should().Be("3d-tiles-styling");
        spec.Version.Should().Be("1.0");
        spec.DefaultMaterial.Color.Should().Be("#ffffff");
        spec.DefaultMaterial.Opacity.Should().Be(1.0);
        spec.Style.Color.Should().BeNull();
        spec.Style.Show.Should().BeNull();
    }

    [UnitTest]
    public void Build_DefaultColorAndOpacity_FlowIntoMaterial()
    {
        var symbology = new Symbology3D
        {
            DefaultColor = new Symbology3DColor(16, 32, 48),
            DefaultOpacity = 0.25
        };

        var spec = TileStyleSpecWriter.Build(symbology);

        spec.DefaultMaterial.Color.Should().Be("#102030");
        spec.DefaultMaterial.Opacity.Should().Be(0.25);
        spec.Style.Color.Should().BeNull("a symbology with no rules emits no conditions expression.");
    }

    [UnitTest]
    public void Build_ColorRule_EmitsOrderedConditionsWithTrailingFallback()
    {
        var symbology = new Symbology3D
        {
            DefaultColor = new Symbology3DColor(255, 255, 255),
            Rules =
            [
                new Symbology3DRule
                {
                    Attribute = "height",
                    Comparison = Symbology3DComparison.GreaterThan,
                    Value = "100",
                    Color = new Symbology3DColor(255, 0, 0)
                }
            ]
        };

        var spec = TileStyleSpecWriter.Build(symbology);

        spec.Style.Color.Should().NotBeNull();
        var conditions = spec.Style.Color!.Conditions;
        conditions.Should().HaveCount(2, "one rule plus the trailing default fallback.");
        conditions[0][0].Should().Be("${height} > 100");
        conditions[0][1].Should().Be("color('#ff0000', 1)");
        conditions[1][0].Should().Be("true", "the last condition is the catch-all default.");
        conditions[1][1].Should().Be("color('#ffffff', 1)");
    }

    [UnitTest]
    public void Build_StringEqualityRule_QuotesOperand()
    {
        var symbology = new Symbology3D
        {
            Rules =
            [
                new Symbology3DRule
                {
                    Attribute = "kind",
                    Comparison = Symbology3DComparison.Equals,
                    Value = "tree",
                    Color = new Symbology3DColor(0, 128, 0)
                }
            ]
        };

        var spec = TileStyleSpecWriter.Build(symbology);

        spec.Style.Color!.Conditions[0][0].Should().Be("${kind} === 'tree'");
    }

    [UnitTest]
    public void Build_VisibilityRule_EmitsShowConditions()
    {
        var symbology = new Symbology3D
        {
            Rules =
            [
                new Symbology3DRule
                {
                    Attribute = "demolished",
                    Comparison = Symbology3DComparison.Equals,
                    Value = "true",
                    Visible = false
                }
            ]
        };

        var spec = TileStyleSpecWriter.Build(symbology);

        spec.Style.Show.Should().NotBeNull();
        var conditions = spec.Style.Show!.Conditions;
        conditions[0][0].Should().Be("${demolished} === 'true'");
        conditions[0][1].Should().Be("false");
        conditions[^1].Should().Equal("true", "true");
        spec.Style.Color.Should().BeNull("a visibility-only rule emits no color expression.");
    }

    [UnitTest]
    public void Build_EarlyVisibilityOnlyRuleBeforeColorRule_EmittedColorMatchesResolver()
    {
        // An early visibility-only rule matches FIRST in the resolver and wins
        // (leaving the default color), so a later color rule must NOT recolor the
        // feature. The emitted color conditions have to replicate that
        // first-match-wins, otherwise baked GLB color and style.json diverge.
        var symbology = new Symbology3D
        {
            DefaultColor = new Symbology3DColor(10, 20, 30),
            DefaultOpacity = 0.4,
            Rules =
            [
                new Symbology3DRule
                {
                    Attribute = "status",
                    Comparison = Symbology3DComparison.Equals,
                    Value = "hidden",
                    Visible = false
                },
                new Symbology3DRule
                {
                    Attribute = "status",
                    Comparison = Symbology3DComparison.Equals,
                    Value = "hidden",
                    Color = new Symbology3DColor(255, 0, 0)
                }
            ]
        };

        var spec = TileStyleSpecWriter.Build(symbology);

        // A feature matching the early visibility-only rule.
        var attributes = Attributes(("status", "hidden"));
        var resolved = Symbology3DResolver.Resolve(symbology, attributes);

        // Resolver keeps the DEFAULT color because the first matching rule sets
        // no color.
        resolved.Color.Should().Be(new Symbology3DColor(10, 20, 30));

        var emitted = EvaluateColor(spec.Style.Color!, attributes);
        emitted.Should().Be(
            $"color('{resolved.Color.ToHex()}', {resolved.Opacity.ToString("0.###", CultureInfo.InvariantCulture)})",
            "the emitted color expression must yield the same color the resolver/GLB bake produces.");

        // The first condition is the visibility-only rule terminating color
        // evaluation at the resolver's default color, NOT the later red rule.
        spec.Style.Color!.Conditions[0][1].Should().Be("color('#0a141e', 0.4)");
    }

    [UnitTest]
    public void Build_EmittedColorMatchesResolverForEveryFeature_OpacityOnlyEarlyRule()
    {
        // Early opacity-only rule matches first for "warn"; a later color rule on
        // the same attribute must not apply. Verify across several feature values.
        var symbology = new Symbology3D
        {
            DefaultColor = new Symbology3DColor(255, 255, 255),
            DefaultOpacity = 1.0,
            Rules =
            [
                new Symbology3DRule
                {
                    Attribute = "level",
                    Comparison = Symbology3DComparison.Equals,
                    Value = "warn",
                    Opacity = 0.2
                },
                new Symbology3DRule
                {
                    Attribute = "level",
                    Comparison = Symbology3DComparison.Equals,
                    Value = "warn",
                    Color = new Symbology3DColor(0, 0, 255)
                },
                new Symbology3DRule
                {
                    Attribute = "level",
                    Comparison = Symbology3DComparison.Equals,
                    Value = "error",
                    Color = new Symbology3DColor(255, 0, 0)
                }
            ]
        };

        var spec = TileStyleSpecWriter.Build(symbology);

        foreach (var value in new[] { "warn", "error", "info", "" })
        {
            var attributes = Attributes(("level", value));
            var resolved = Symbology3DResolver.Resolve(symbology, attributes);
            var expected =
                $"color('{resolved.Color.ToHex()}', {resolved.Opacity.ToString("0.###", CultureInfo.InvariantCulture)})";

            EvaluateColor(spec.Style.Color!, attributes).Should().Be(
                expected,
                $"emitted color for level='{value}' must match the resolver.");
        }
    }

    [UnitTest]
    public void Build_WithAttributeSchemas_EmitsSanitizedPropertyIds()
    {
        // The publish executor sanitizes "road-class" -> "road_class" before
        // writing it into the tile metadata schema, so the emitted ${...} must
        // reference the sanitized id, not the raw authoring name.
        var symbology = new Symbology3D
        {
            Rules =
            [
                new Symbology3DRule
                {
                    Attribute = "road-class",
                    Comparison = Symbology3DComparison.Equals,
                    Value = "primary",
                    Color = new Symbology3DColor(0, 128, 0)
                }
            ]
        };

        var schemas = new[]
        {
            new SceneAttributeSchema
            {
                PropertyId = "road_class",
                FieldName = "road-class",
                SchemaType = "STRING"
            }
        };

        var spec = TileStyleSpecWriter.Build(symbology, schemas);

        spec.Style.Color!.Conditions[0][0].Should().Be(
            "${road_class} === 'primary'",
            "the style expression must reference the sanitized metadata property id.");
    }

    [UnitTest]
    public void Build_WithAttributeSchemas_FallsBackToRawNameWhenUnmapped()
    {
        var symbology = new Symbology3D
        {
            Rules =
            [
                new Symbology3DRule
                {
                    Attribute = "height",
                    Comparison = Symbology3DComparison.GreaterThan,
                    Value = "10",
                    Color = new Symbology3DColor(0, 0, 0)
                }
            ]
        };

        // Schema mapping that does not cover "height".
        var schemas = new[]
        {
            new SceneAttributeSchema
            {
                PropertyId = "other",
                FieldName = "other",
                SchemaType = "STRING"
            }
        };

        var spec = TileStyleSpecWriter.Build(symbology, schemas);

        spec.Style.Color!.Conditions[0][0].Should().Be("${height} > 10");
    }

    [UnitTest]
    public void Serialize_RoundTripsContract()
    {
        var symbology = new Symbology3D
        {
            DefaultColor = new Symbology3DColor(10, 20, 30),
            DefaultOpacity = 0.5,
            Rules =
            [
                new Symbology3DRule
                {
                    Attribute = "height",
                    Comparison = Symbology3DComparison.GreaterThanOrEqual,
                    Value = "50",
                    Color = new Symbology3DColor(200, 100, 50),
                    Opacity = 0.9
                },
                new Symbology3DRule
                {
                    Attribute = "hidden",
                    Comparison = Symbology3DComparison.Equals,
                    Value = "yes",
                    Visible = false
                }
            ]
        };

        var spec = TileStyleSpecWriter.Build(symbology);
        var bytes = TileStyleSpecWriter.Serialize(spec);

        var roundTripped = JsonSerializer.Deserialize(
            bytes, TileStyleSpecJsonContext.Default.TileStyleSpec);

        roundTripped.Should().NotBeNull();
        roundTripped!.Encoding.Should().Be("3d-tiles-styling");
        roundTripped.DefaultMaterial.Color.Should().Be("#0a141e");
        roundTripped.DefaultMaterial.Opacity.Should().Be(0.5);
        roundTripped.Style.Color!.Conditions[0][1].Should().Be("color('#c86432', 0.9)");
        roundTripped.Style.Show!.Conditions[0][0].Should().Be("${hidden} === 'yes'");
    }

    [UnitTest]
    public void Serialize_IsByteIdenticalAcrossRuns()
    {
        var symbology = new Symbology3D
        {
            DefaultColor = new Symbology3DColor(1, 2, 3),
            Rules =
            [
                new Symbology3DRule
                {
                    Attribute = "x",
                    Comparison = Symbology3DComparison.LessThan,
                    Value = "5",
                    Color = new Symbology3DColor(9, 9, 9)
                }
            ]
        };

        var bytes1 = TileStyleSpecWriter.Serialize(TileStyleSpecWriter.Build(symbology));
        var bytes2 = TileStyleSpecWriter.Serialize(TileStyleSpecWriter.Build(symbology));

        bytes1.Should().Equal(bytes2);
    }

    [UnitTest]
    public void Serialize_ProducesExpectedJsonShape()
    {
        var symbology = new Symbology3D
        {
            DefaultColor = new Symbology3DColor(255, 255, 255),
            Rules =
            [
                new Symbology3DRule
                {
                    Attribute = "height",
                    Comparison = Symbology3DComparison.GreaterThan,
                    Value = "100",
                    Color = new Symbology3DColor(255, 0, 0)
                }
            ]
        };

        var bytes = TileStyleSpecWriter.Serialize(TileStyleSpecWriter.Build(symbology));

        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
        var root = json.RootElement;
        root.GetProperty("encoding").GetString().Should().Be("3d-tiles-styling");
        root.GetProperty("defaultMaterial").GetProperty("color").GetString().Should().Be("#ffffff");
        var conditions = root.GetProperty("style").GetProperty("color").GetProperty("conditions");
        conditions.GetArrayLength().Should().Be(2);
        conditions[0][0].GetString().Should().Be("${height} > 100");
    }

    private static Dictionary<string, object?> Attributes(params (string Key, object? Value)[] pairs)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            dict[key] = value;
        }
        return dict;
    }

    /// <summary>
    /// Evaluates an emitted 3D Tiles Styling <c>conditions</c> array against a
    /// feature's attributes using first-match-wins semantics (exactly how a
    /// client runtime applies it), returning the value expression of the first
    /// matching branch. Mirrors the styling expression grammar the writer emits.
    /// </summary>
    private static string EvaluateColor(
        TileStyleConditions conditions,
        IReadOnlyDictionary<string, object?> attributes)
    {
        foreach (var pair in conditions.Conditions)
        {
            if (EvaluateTest(pair[0], attributes))
            {
                return pair[1];
            }
        }

        throw new InvalidOperationException("conditions array is not total (no trailing true branch).");
    }

    private static bool EvaluateTest(string test, IReadOnlyDictionary<string, object?> attributes)
    {
        if (test == "true")
        {
            return true;
        }

        // Parse "${prop} OP operand"; the operand is a quoted string or a bare number.
        var propStart = test.IndexOf("${", StringComparison.Ordinal) + 2;
        var propEnd = test.IndexOf('}', propStart);
        var propName = test[propStart..propEnd];

        var rest = test[(propEnd + 1)..].Trim();
        var spaceIdx = rest.IndexOf(' ', StringComparison.Ordinal);
        var op = rest[..spaceIdx];
        var operand = rest[(spaceIdx + 1)..].Trim();

        attributes.TryGetValue(propName, out var raw);

        if (operand.StartsWith('\''))
        {
            // String operand: unquote and compare ordinally for === / !==.
            var literal = operand.Trim('\'').Replace("\\'", "'", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
            var leftText = raw?.ToString() ?? string.Empty;
            var equal = string.Equals(leftText, literal, StringComparison.Ordinal);
            return op == "===" ? equal : !equal;
        }

        var right = double.Parse(operand, CultureInfo.InvariantCulture);
        var left = raw switch
        {
            double d => d,
            int i => i,
            long l => l,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => v,
            _ => double.NaN
        };
        if (double.IsNaN(left))
        {
            return false;
        }

        return op switch
        {
            "===" => left == right,
            "!==" => left != right,
            ">" => left > right,
            ">=" => left >= right,
            "<" => left < right,
            "<=" => left <= right,
            _ => false
        };
    }
}
