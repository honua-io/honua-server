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
    /// <param name="symbology">The authoring-side symbology to translate.</param>
    /// <param name="attributeSchemas">
    /// Optional metadata-schema entries produced by the publish executor. When
    /// supplied, the emitted <c>${...}</c> property references are rewritten
    /// from each rule's raw authoring attribute name to the sanitized /
    /// de-duplicated <see cref="SceneAttributeSchema.PropertyId"/> that is
    /// actually written into the tile's <c>EXT_structural_metadata</c> schema,
    /// so the runtime expression resolves against a property that exists. When
    /// null (or a rule's attribute has no matching schema entry), the raw
    /// attribute name is emitted unchanged.
    /// </param>
    public static TileStyleSpec Build(
        Symbology3D? symbology,
        IReadOnlyList<SceneAttributeSchema>? attributeSchemas = null)
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

        var propertyIdByFieldName = BuildPropertyIdMap(attributeSchemas);

        var colorConditions = new List<string[]>();
        var showConditions = new List<string[]>();
        var hasColorRule = false;
        var hasShowRule = false;

        foreach (var rule in rules)
        {
            var test = BuildTestExpression(rule, propertyIdByFieldName);

            // Color conditions must replicate Symbology3DResolver's
            // first-match-wins exactly: the resolver stops at the FIRST matching
            // rule regardless of which fields it sets, then keeps the default
            // color for any field that rule leaves unset. So EVERY rule must
            // terminate color evaluation here — emitting the color that rule
            // actually produces — otherwise a colorless rule that matches first
            // (e.g. visibility-only) would be skipped and a later color rule
            // would erroneously recolor the feature, diverging from the baked
            // GLB color the resolver computed.
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
            else
            {
                // Rule sets neither color nor opacity (e.g. visibility-only). It
                // still wins the resolver's first-match, leaving the feature at
                // the default color, so the emitted branch must yield the same
                // default to terminate color evaluation at this rule.
                colorConditions.Add([test, BuildColorExpression(defaultColor, defaultOpacity)]);
            }

            if (rule.Visible is { } ruleVisible)
            {
                hasShowRule = true;
                showConditions.Add([test, ruleVisible ? "true" : "false"]);
            }
        }

        // Only emit a color expression when at least one rule actually changes
        // the color or opacity. When every rule is visibility-only, the resolver
        // always yields the default color, so the placeholder default branches
        // accumulated above are dropped and the client falls back to the
        // default material — matching the resolver without redundant output.
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
    /// Builds a case-insensitive map from a source field name to the sanitized
    /// metadata property id the publish executor emitted for it. Returns null
    /// when no schema mapping is supplied so the caller emits raw names.
    /// </summary>
    private static Dictionary<string, string>? BuildPropertyIdMap(
        IReadOnlyList<SceneAttributeSchema>? attributeSchemas)
    {
        if (attributeSchemas is not { Count: > 0 })
        {
            return null;
        }

        var map = new Dictionary<string, string>(attributeSchemas.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var schema in attributeSchemas)
        {
            // First-writer wins on a casing collision, mirroring the executor's
            // declaration-order de-duplication so the reference stays stable.
            map.TryAdd(schema.FieldName, schema.PropertyId);
        }
        return map;
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

    private static string BuildTestExpression(
        Symbology3DRule rule,
        Dictionary<string, string>? propertyIdByFieldName)
    {
        // 3D Tiles Styling references a feature property as ${propertyId}. The
        // property id is the sanitized/de-duplicated name actually written into
        // the tile metadata schema, which may differ from the raw authoring
        // attribute (e.g. "road-class" -> "road_class"); fall back to the raw
        // name when no schema mapping is available.
        var propertyName = rule.Attribute;
        if (propertyIdByFieldName is not null
            && propertyIdByFieldName.TryGetValue(rule.Attribute, out var mapped))
        {
            propertyName = mapped;
        }

        var lhs = "${" + propertyName + "}";
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
