// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
}
