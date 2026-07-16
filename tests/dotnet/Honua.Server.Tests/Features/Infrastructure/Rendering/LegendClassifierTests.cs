// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Rendering;
using Honua.TestKit.Attributes;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Infrastructure.Rendering;

/// <summary>
/// Tests for projecting data-driven MapLibre style layers onto discrete legend classes.
/// </summary>
[Trait("Component", "MapServer")]
public class LegendClassifierTests
{
    private static MapLibreStyleLayer ParseLayer(string json)
        => StyleTranslator.ParseStyleLayers(json).Single();

    [UnitTest]
    public void Classify_ConstantFillColor_ReturnsSingleClass()
    {
        var layer = ParseLayer("""
            { "id": "lakes", "type": "fill", "paint": { "fill-color": "#ff0000" } }
            """);

        var result = LegendClassifier.Classify(layer);

        result.Classes.Should().ContainSingle();
        result.Classes[0].Label.Should().Be("lakes");
        result.IsDataDriven.Should().BeFalse();
        result.UnrepresentableReason.Should().BeNull();
    }

    [UnitTest]
    public void Classify_MatchExpression_ReturnsOneClassPerLabelPlusFallback()
    {
        var layer = ParseLayer("""
            {
              "id": "zoning", "type": "fill",
              "paint": { "fill-color": [
                "match", ["get", "kind"],
                "residential", "#00ff00",
                "industrial", "#ff0000",
                "#cccccc"
              ] }
            }
            """);

        var result = LegendClassifier.Classify(layer);

        result.Field.Should().Be("kind");
        result.IsDataDriven.Should().BeTrue();
        result.Classes.Select(c => c.Label)
            .Should().Equal("residential", "industrial", "Other");
    }

    [UnitTest]
    public void Classify_MatchExpressionWithGroupedLabels_ExpandsEachLabel()
    {
        var layer = ParseLayer("""
            {
              "id": "roads", "type": "line",
              "paint": { "line-color": [
                "match", ["get", "class"],
                ["motorway", "trunk"], "#ff0000",
                "#888888"
              ] }
            }
            """);

        var result = LegendClassifier.Classify(layer);

        result.Classes.Select(c => c.Label).Should().Equal("motorway", "trunk", "Other");
    }

    [UnitTest]
    public void Classify_MatchExpression_PropertiesSelectTheMatchingBranch()
    {
        var layer = ParseLayer("""
            {
              "id": "zoning", "type": "fill",
              "paint": { "fill-color": [
                "match", ["get", "kind"],
                "residential", "#00ff00",
                "industrial", "#ff0000",
                "#cccccc"
              ] }
            }
            """);

        var result = LegendClassifier.Classify(layer);

        // The classifier only enumerates the domain; the colour each class shows must
        // come from the same StyleTranslator path GetMap paints features with.
        var residential = StyleTranslator.ResolveFillStyle(layer, result.Classes[0].Properties);
        var industrial = StyleTranslator.ResolveFillStyle(layer, result.Classes[1].Properties);
        var other = StyleTranslator.ResolveFillStyle(layer, result.Classes[2].Properties);

        residential.FillColor.Should().Be(new SKColor(0x00, 0xff, 0x00));
        industrial.FillColor.Should().Be(new SKColor(0xff, 0x00, 0x00));
        other.FillColor.Should().Be(new SKColor(0xcc, 0xcc, 0xcc));
    }

    [UnitTest]
    public void Classify_StepExpression_ReturnsRangeClassesSelectingEachBand()
    {
        var layer = ParseLayer("""
            {
              "id": "pop", "type": "fill",
              "paint": { "fill-color": [
                "step", ["get", "population"],
                "#eeeeee",
                100, "#88aa88",
                1000, "#004400"
              ] }
            }
            """);

        var result = LegendClassifier.Classify(layer);

        result.Field.Should().Be("population");
        result.Classes.Select(c => c.Label).Should().Equal("< 100", "100 - 1000", ">= 1000");

        StyleTranslator.ResolveFillStyle(layer, result.Classes[0].Properties)
            .FillColor.Should().Be(new SKColor(0xee, 0xee, 0xee));
        StyleTranslator.ResolveFillStyle(layer, result.Classes[1].Properties)
            .FillColor.Should().Be(new SKColor(0x88, 0xaa, 0x88));
        StyleTranslator.ResolveFillStyle(layer, result.Classes[2].Properties)
            .FillColor.Should().Be(new SKColor(0x00, 0x44, 0x00));
    }

    [UnitTest]
    public void Classify_InterpolateColourRamp_SamplesStopsThatResolveToGetMapColours()
    {
        var layer = ParseLayer("""
            {
              "id": "heat", "type": "fill",
              "paint": { "fill-color": [
                "interpolate", ["linear"], ["get", "density"],
                0, "#ffffff",
                100, "#ff0000"
              ] }
            }
            """);

        var result = LegendClassifier.Classify(layer);

        // #2870 made the shared evaluator blend colours instead of coercing the stops to 0f,
        // so a ramp is now representable: sampling it at its own stops yields entries whose
        // swatches are exactly the colours GetMap paints for those attribute values.
        result.UnrepresentableReason.Should().BeNull();
        result.IsDataDriven.Should().BeTrue();
        result.Field.Should().Be("density");
        result.Classes.Select(c => c.Label).Should().Equal("0", "100");

        StyleTranslator.ResolveFillStyle(layer, result.Classes[0].Properties)
            .FillColor.Should().Be(new SKColor(0xff, 0xff, 0xff));
        StyleTranslator.ResolveFillStyle(layer, result.Classes[1].Properties)
            .FillColor.Should().Be(new SKColor(0xff, 0x00, 0x00));
    }

    [UnitTest]
    public void Classify_InterpolateColourRamp_SamplesInteriorStopsExactly()
    {
        // An interior stop is the case that would expose a sampler modelling the curve
        // itself: it is approached from both sides, and every interpolation type must
        // still resolve it to its own output verbatim.
        var layer = ParseLayer("""
            {
              "id": "heat", "type": "fill",
              "paint": { "fill-color": [
                "interpolate", ["exponential", 2], ["get", "density"],
                0, "#ffffff",
                50, "#00ff00",
                100, "#ff0000"
              ] }
            }
            """);

        var result = LegendClassifier.Classify(layer);

        result.UnrepresentableReason.Should().BeNull();
        result.Classes.Select(c => c.Label).Should().Equal("0", "50", "100");
        StyleTranslator.ResolveFillStyle(layer, result.Classes[1].Properties)
            .FillColor.Should().Be(new SKColor(0x00, 0xff, 0x00));
    }

    [UnitTest]
    public void Classify_CaseExpression_IsReportedUnrepresentable()
    {
        var layer = ParseLayer("""
            {
              "id": "flags", "type": "fill",
              "paint": { "fill-color": [
                "case",
                [">", ["get", "area"], 10], "#ff0000",
                "#0000ff"
              ] }
            }
            """);

        var result = LegendClassifier.Classify(layer);

        result.UnrepresentableReason.Should().NotBeNull();
        result.UnrepresentableReason.Should().Contain("case");
        result.Classes.Should().ContainSingle();
    }

    [UnitTest]
    public void Classify_MatchOnCoercedAttribute_UnwrapsToTheField()
    {
        var layer = ParseLayer("""
            {
              "id": "zoning", "type": "circle",
              "paint": { "circle-color": [
                "match", ["to-string", ["get", "kind"]],
                "a", "#00ff00",
                "#cccccc"
              ] }
            }
            """);

        var result = LegendClassifier.Classify(layer);

        result.Field.Should().Be("kind");
        result.Classes.Select(c => c.Label).Should().Equal("a", "Other");
    }

    [UnitTest]
    public void Classify_MatchOnNonAttributeInput_FallsBackToSingleClass()
    {
        var layer = ParseLayer("""
            {
              "id": "zoning", "type": "fill",
              "paint": { "fill-color": ["match", ["zoom"], 5, "#00ff00", "#cccccc"] }
            }
            """);

        var result = LegendClassifier.Classify(layer);

        result.Classes.Should().ContainSingle();
        result.Field.Should().BeNull();
    }

    [UnitTest]
    public void Classify_SymbolLayer_FallsBackToSingleClass()
    {
        var layer = ParseLayer("""
            { "id": "labels", "type": "symbol", "layout": { "text-field": ["get", "name"] } }
            """);

        var result = LegendClassifier.Classify(layer);

        result.Classes.Should().ContainSingle();
        result.Field.Should().BeNull();
    }
}
