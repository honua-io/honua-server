// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Generation;

/// <summary>
/// Translates a layer's authoring-side <see cref="Symbology3D"/> into the
/// emitted <see cref="TileStyleSpec"/> style-metadata contract, mapping each
/// rule onto a 3D Tiles Styling <c>conditions</c> expression.
/// </summary>
/// <remarks>
/// The translation is deterministic and order-preserving: rules become ordered
/// condition pairs in authoring order, with a trailing <c>true</c> fallback
/// that yields the default material so the client expression is total (every
/// feature resolves to a value). This mirrors <see cref="Symbology3DResolver"/>'s
/// first-match-wins semantics exactly, so a client that evaluates the emitted
/// expression sees the same result the server baked into the GLB.
/// </remarks>
public static class TileStyleSpecWriter
{
    /// <summary>
    /// Builds the style-metadata contract for a layer's symbology. Returns a
    /// spec whose default material is always populated; the <c>color</c> /
    /// <c>show</c> expressions are present only when the symbology declares
    /// rules that affect them.
    /// </summary>
    public static TileStyleSpec Build(Symbology3D? symbology)
    {
        var defaultColor = symbology?.DefaultColor ?? Symbology3DColor.White;
        var defaultOpacity = ClampOpacity(symbology?.DefaultOpacity ?? 1.0);

        var spec = new TileStyleSpec
        {
            DefaultMaterial = new TileStyleMaterial
            {
                Color = defaultColor.ToHex(),
                Opacity = defaultOpacity
            }
        };

        var rules = symbology?.Rules;
        if (rules is not { Count: > 0 })
        {
            return spec;
        }

        var colorConditions = new List<string[]>();
        var showConditions = new List<string[]>();
        var hasColorRule = false;
        var hasShowRule = false;

        foreach (var rule in rules)
        {
            var test = BuildTestExpression(rule);

            if (rule.Color is { } ruleColor)
            {
                hasColorRule = true;
                var opacity = ClampOpacity(rule.Opacity ?? defaultOpacity);
                colorConditions.Add([test, BuildColorExpression(ruleColor, opacity)]);
            }
            else if (rule.Opacity is { } ruleOpacity)
            {
                // Opacity-only rule still recolors via the default RGB with the
                // rule's alpha so the emitted expression matches the baked GLB.
                hasColorRule = true;
                colorConditions.Add([test, BuildColorExpression(defaultColor, ClampOpacity(ruleOpacity))]);
            }

            if (rule.Visible is { } ruleVisible)
            {
                hasShowRule = true;
                showConditions.Add([test, ruleVisible ? "true" : "false"]);
            }
        }

        if (hasColorRule)
        {
            colorConditions.Add(["true", BuildColorExpression(defaultColor, defaultOpacity)]);
            spec.Style.Color = new TileStyleConditions { Conditions = colorConditions };
        }

        if (hasShowRule)
        {
            showConditions.Add(["true", "true"]);
            spec.Style.Show = new TileStyleConditions { Conditions = showConditions };
        }

        return spec;
    }

    /// <summary>
    /// Serializes the style-metadata contract to a deterministic UTF-8 byte
    /// sequence using the source-generated context.
    /// </summary>
    public static byte[] Serialize(TileStyleSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return JsonSerializer.SerializeToUtf8Bytes(spec, TileStyleSpecJsonContext.Default.TileStyleSpec);
    }

    private static string BuildTestExpression(Symbology3DRule rule)
    {
        // 3D Tiles Styling references a feature property as ${propertyName}.
        var lhs = "${" + rule.Attribute + "}";
        var op = rule.Comparison switch
        {
            Symbology3DComparison.Equals => "===",
            Symbology3DComparison.NotEquals => "!==",
            Symbology3DComparison.GreaterThan => ">",
            Symbology3DComparison.GreaterThanOrEqual => ">=",
            Symbology3DComparison.LessThan => "<",
            Symbology3DComparison.LessThanOrEqual => "<=",
            _ => "==="
        };

        return string.Concat(lhs, " ", op, " ", BuildOperand(rule));
    }

    private static string BuildOperand(Symbology3DRule rule)
    {
        // Numeric operands are emitted bare; non-numeric operands are quoted as
        // string literals. Ordered comparisons only make sense numerically, so
        // they always emit the numeric form (mirroring the resolver, which only
        // matches an ordered comparison when both sides are numeric).
        if (double.TryParse(rule.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
        {
            return numeric.ToString(CultureInfo.InvariantCulture);
        }

        return QuoteStringLiteral(rule.Value ?? string.Empty);
    }

    private static string BuildColorExpression(Symbology3DColor color, double opacity)
    {
        // color('#rrggbb', alpha) is the 3D Tiles Styling form for an
        // alpha-blended constant color.
        return string.Concat(
            "color('",
            color.ToHex(),
            "', ",
            opacity.ToString("0.###", CultureInfo.InvariantCulture),
            ")");
    }

    private static string QuoteStringLiteral(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('\'');
        foreach (var c in value)
        {
            if (c is '\'' or '\\')
            {
                builder.Append('\\');
            }
            builder.Append(c);
        }
        builder.Append('\'');
        return builder.ToString();
    }

    private static double ClampOpacity(double value)
    {
        if (double.IsNaN(value))
        {
            return 1.0;
        }
        return Math.Clamp(value, 0.0, 1.0);
    }
}
