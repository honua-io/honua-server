// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene.Domain;

/// <summary>
/// Unit tests for the attribute-driven 3D symbology resolver. Verifies that a
/// <see cref="Symbology3D"/> config resolves to the expected per-feature color,
/// opacity, and visibility for sample features.
/// </summary>
public sealed class Symbology3DResolverTests
{
    [UnitTest]
    public void Resolve_NoSymbology_ReturnsOpaqueWhiteVisible()
    {
        var resolved = Symbology3DResolver.Resolve(null, Attributes(("status", "ok")));

        resolved.Color.Should().Be(Symbology3DColor.White);
        resolved.Opacity.Should().Be(1.0);
        resolved.Visible.Should().BeTrue();
    }

    [UnitTest]
    public void Resolve_NoMatchingRule_FallsBackToDefaults()
    {
        var symbology = new Symbology3D
        {
            DefaultColor = new Symbology3DColor(10, 20, 30),
            DefaultOpacity = 0.5,
            Rules =
            [
                new Symbology3DRule
                {
                    Attribute = "kind",
                    Comparison = Symbology3DComparison.Equals,
                    Value = "tree",
                    Color = new Symbology3DColor(0, 255, 0)
                }
            ]
        };

        var resolved = Symbology3DResolver.Resolve(symbology, Attributes(("kind", "building")));

        resolved.Color.Should().Be(new Symbology3DColor(10, 20, 30));
        resolved.Opacity.Should().Be(0.5);
        resolved.Visible.Should().BeTrue();
    }

    [UnitTest]
    public void Resolve_StringEqualityRule_AppliesColor()
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
                    Color = new Symbology3DColor(0, 128, 0),
                    Opacity = 0.8
                }
            ]
        };

        var resolved = Symbology3DResolver.Resolve(symbology, Attributes(("kind", "tree")));

        resolved.Color.Should().Be(new Symbology3DColor(0, 128, 0));
        resolved.Opacity.Should().Be(0.8);
        resolved.Visible.Should().BeTrue();
    }

    [UnitTest]
    public void Resolve_NumericGreaterThanRule_AppliesColor()
    {
        var symbology = new Symbology3D
        {
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

        var tall = Symbology3DResolver.Resolve(symbology, Attributes(("height", 150.0)));
        var shortFeature = Symbology3DResolver.Resolve(symbology, Attributes(("height", 50.0)));

        tall.Color.Should().Be(new Symbology3DColor(255, 0, 0));
        shortFeature.Color.Should().Be(Symbology3DColor.White, "features that miss every rule take the default color.");
    }

    [UnitTest]
    public void Resolve_NumericStringAttribute_ParsesForComparison()
    {
        // Postgres projects JSONB attributes as TEXT, so a numeric attribute can
        // arrive as a string; the resolver must parse it for ordered comparison.
        var symbology = new Symbology3D
        {
            Rules =
            [
                new Symbology3DRule
                {
                    Attribute = "floors",
                    Comparison = Symbology3DComparison.GreaterThanOrEqual,
                    Value = "10",
                    Color = new Symbology3DColor(0, 0, 255)
                }
            ]
        };

        var resolved = Symbology3DResolver.Resolve(symbology, Attributes(("floors", "12")));

        resolved.Color.Should().Be(new Symbology3DColor(0, 0, 255));
    }

    [UnitTest]
    public void Resolve_VisibilityRule_HidesFeature()
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

        var resolved = Symbology3DResolver.Resolve(symbology, Attributes(("demolished", "true")));

        resolved.Visible.Should().BeFalse();
        resolved.Color.Should().Be(Symbology3DColor.White, "a visibility-only rule leaves the color at the default.");
    }

    [UnitTest]
    public void Resolve_FirstMatchingRuleWins()
    {
        var symbology = new Symbology3D
        {
            Rules =
            [
                new Symbology3DRule
                {
                    Attribute = "height",
                    Comparison = Symbology3DComparison.GreaterThan,
                    Value = "50",
                    Color = new Symbology3DColor(255, 0, 0)
                },
                new Symbology3DRule
                {
                    Attribute = "height",
                    Comparison = Symbology3DComparison.GreaterThan,
                    Value = "10",
                    Color = new Symbology3DColor(0, 255, 0)
                }
            ]
        };

        var resolved = Symbology3DResolver.Resolve(symbology, Attributes(("height", 100.0)));

        resolved.Color.Should().Be(new Symbology3DColor(255, 0, 0),
            "the first rule in declaration order that matches must win.");
    }

    [UnitTest]
    public void Resolve_ClampsOpacityIntoUnitRange()
    {
        var symbology = new Symbology3D
        {
            Rules =
            [
                new Symbology3DRule
                {
                    Attribute = "kind",
                    Comparison = Symbology3DComparison.Equals,
                    Value = "glass",
                    Color = new Symbology3DColor(200, 200, 255),
                    Opacity = 2.5
                }
            ]
        };

        var resolved = Symbology3DResolver.Resolve(symbology, Attributes(("kind", "glass")));

        resolved.Opacity.Should().Be(1.0, "out-of-range opacity must clamp into [0, 1].");
    }

    [UnitTest]
    public void Resolve_CaseInsensitiveAttributeLookup()
    {
        var symbology = new Symbology3D
        {
            Rules =
            [
                new Symbology3DRule
                {
                    Attribute = "Status",
                    Comparison = Symbology3DComparison.Equals,
                    Value = "active",
                    Color = new Symbology3DColor(1, 2, 3)
                }
            ]
        };

        // Attribute bag is ordinal-keyed with a different casing.
        var resolved = Symbology3DResolver.Resolve(symbology, Attributes(("status", "active")));

        resolved.Color.Should().Be(new Symbology3DColor(1, 2, 3));
    }

    [UnitTest]
    public void Color_ToHex_ProducesLowercaseCssLiteral()
    {
        new Symbology3DColor(255, 10, 0).ToHex().Should().Be("#ff0a00");
        Symbology3DColor.White.ToHex().Should().Be("#ffffff");
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
}
