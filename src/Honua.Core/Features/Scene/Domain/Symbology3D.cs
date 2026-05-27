// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Scene.Domain;

/// <summary>
/// Attribute-driven 3D symbology configuration stored with a Metadata v2
/// feature resource and consumed by the 3D Tiles generation pipeline.
/// </summary>
/// <remarks>
/// <para>
/// This is the authoring-side model: an operator supplies a default material
/// (base color + opacity) plus an ordered set of <see cref="Rules"/> that
/// recolor / dim / hide features based on a single attribute condition each.
/// The generator both bakes the resolved per-feature color into the emitted
/// GLB (vertex <c>COLOR_0</c> + a <c>baseColorFactor</c> material) and projects
/// the equivalent declarative expression into the tileset's
/// <see cref="TileStyleSpec"/> so a client runtime (CesiumJS 3D Tiles Styling)
/// can re-evaluate the rules dynamically.
/// </para>
/// <para>
/// Rules are evaluated in declaration order; the FIRST rule whose condition
/// matches a feature wins (last-write-wins is intentionally avoided so the
/// authoring order maps cleanly onto the ordered <c>conditions</c> array of the
/// 3D Tiles Styling expression language). When no rule matches, the
/// <see cref="DefaultColor"/> / <see cref="DefaultOpacity"/> apply.
/// </para>
/// </remarks>
public sealed record Symbology3D
{
    /// <summary>
    /// Default base color applied to features that match no rule. When null,
    /// the generator falls back to opaque white so the tileset is always
    /// renderable.
    /// </summary>
    public Symbology3DColor? DefaultColor { get; init; }

    /// <summary>
    /// Default opacity in [0, 1] applied to features that match no rule and
    /// to rules that omit an explicit opacity. Null is treated as fully opaque.
    /// </summary>
    public double? DefaultOpacity { get; init; }

    /// <summary>
    /// Ordered attribute-driven rules. The first matching rule wins.
    /// </summary>
    public IReadOnlyList<Symbology3DRule> Rules { get; init; } = [];
}

/// <summary>
/// One attribute-driven symbology rule: when the feature's
/// <see cref="Attribute"/> satisfies the <see cref="Comparison"/> against
/// <see cref="Value"/>, the rule contributes its color / opacity / visibility.
/// </summary>
public sealed record Symbology3DRule
{
    /// <summary>
    /// Source attribute (feature field) name the condition is evaluated
    /// against. Matched case-insensitively against the feature's attribute bag.
    /// </summary>
    public required string Attribute { get; init; }

    /// <summary>Comparison operator applied between the attribute and <see cref="Value"/>.</summary>
    public Symbology3DComparison Comparison { get; init; } = Symbology3DComparison.Equals;

    /// <summary>
    /// Right-hand comparison operand. Numeric comparisons parse this as a
    /// number; equality/inequality fall back to ordinal string comparison when
    /// either side is non-numeric.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>Color applied when the rule matches. Null leaves the color unchanged.</summary>
    public Symbology3DColor? Color { get; init; }

    /// <summary>Opacity in [0, 1] applied when the rule matches. Null inherits the default.</summary>
    public double? Opacity { get; init; }

    /// <summary>
    /// Visibility applied when the rule matches. Null means "visible". A false
    /// value hides the feature (maps onto the 3D Tiles Styling <c>show</c>
    /// expression).
    /// </summary>
    public bool? Visible { get; init; }
}

/// <summary>
/// Comparison operators supported by an attribute-driven symbology rule. The
/// set is deliberately a subset of the 3D Tiles Styling expression operators
/// so every rule maps onto a single emittable condition expression.
/// </summary>
public enum Symbology3DComparison
{
    /// <summary>Attribute equals the operand (numeric, else ordinal string).</summary>
    Equals,

    /// <summary>Attribute does not equal the operand.</summary>
    NotEquals,

    /// <summary>Attribute is numerically greater than the operand.</summary>
    GreaterThan,

    /// <summary>Attribute is numerically greater than or equal to the operand.</summary>
    GreaterThanOrEqual,

    /// <summary>Attribute is numerically less than the operand.</summary>
    LessThan,

    /// <summary>Attribute is numerically less than or equal to the operand.</summary>
    LessThanOrEqual
}

/// <summary>
/// An sRGB color with 8-bit channels. Stored as discrete channels (rather than
/// a packed hex string) so the value is unambiguous across the .NET authoring
/// side and the JavaScript runtime, and so it can be baked directly into a glTF
/// normalized <c>COLOR_0</c> attribute without string parsing.
/// </summary>
public readonly record struct Symbology3DColor(byte Red, byte Green, byte Blue)
{
    /// <summary>Opaque white — the renderable fallback when no color is configured.</summary>
    public static Symbology3DColor White => new(255, 255, 255);

    /// <summary>
    /// Emits the CSS-style hex literal (<c>#rrggbb</c>) consumed by the
    /// 3D Tiles Styling <c>color()</c> expression.
    /// </summary>
    public string ToHex() => string.Create(
        7,
        this,
        static (span, color) =>
        {
            span[0] = '#';
            WriteHexByte(span, 1, color.Red);
            WriteHexByte(span, 3, color.Green);
            WriteHexByte(span, 5, color.Blue);
        });

    private static void WriteHexByte(Span<char> span, int index, byte value)
    {
        const string Hex = "0123456789abcdef";
        span[index] = Hex[value >> 4];
        span[index + 1] = Hex[value & 0x0F];
    }
}

/// <summary>
/// The resolved symbology for a single feature after evaluating a
/// <see cref="Symbology3D"/> against that feature's attributes.
/// </summary>
public readonly record struct ResolvedSymbology(Symbology3DColor Color, double Opacity, bool Visible)
{
    /// <summary>
    /// The default resolved symbology (opaque white, visible) used when no
    /// symbology is configured for a layer.
    /// </summary>
    public static ResolvedSymbology Default => new(Symbology3DColor.White, 1.0, true);
}

/// <summary>
/// Pure, DB-free resolver that evaluates a <see cref="Symbology3D"/> against a
/// feature's attributes. Shared by the generator (to bake per-feature color)
/// and by tests; carries no I/O so it is freely unit-testable.
/// </summary>
public static class Symbology3DResolver
{
    /// <summary>
    /// Resolves the color / opacity / visibility for a single feature. The
    /// first matching rule wins; unmatched features take the configured
    /// defaults (falling back to opaque white when no default is set).
    /// </summary>
    public static ResolvedSymbology Resolve(
        Symbology3D? symbology,
        IReadOnlyDictionary<string, object?> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        var color = symbology?.DefaultColor ?? Symbology3DColor.White;
        var opacity = ClampOpacity(symbology?.DefaultOpacity ?? 1.0);
        var visible = true;

        if (symbology?.Rules is { Count: > 0 } rules)
        {
            foreach (var rule in rules)
            {
                if (!Matches(rule, attributes))
                {
                    continue;
                }

                if (rule.Color is { } ruleColor)
                {
                    color = ruleColor;
                }
                if (rule.Opacity is { } ruleOpacity)
                {
                    opacity = ClampOpacity(ruleOpacity);
                }
                if (rule.Visible is { } ruleVisible)
                {
                    visible = ruleVisible;
                }

                // First match wins so the resolved state aligns with the
                // ordered conditions array emitted into the style spec.
                break;
            }
        }

        return new ResolvedSymbology(color, opacity, visible);
    }

    /// <summary>
    /// Evaluates a single rule's condition against a feature's attributes.
    /// </summary>
    public static bool Matches(Symbology3DRule rule, IReadOnlyDictionary<string, object?> attributes)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(attributes);

        var raw = LookupAttribute(attributes, rule.Attribute);

        var leftIsNumber = TryToDouble(raw, out var left);
        var rightIsNumber = TryParseDouble(rule.Value, out var right);

        // Numeric path when both operands are numeric — covers all ordered
        // comparisons and numeric (in)equality.
        if (leftIsNumber && rightIsNumber)
        {
            return rule.Comparison switch
            {
                Symbology3DComparison.Equals => left == right,
                Symbology3DComparison.NotEquals => left != right,
                Symbology3DComparison.GreaterThan => left > right,
                Symbology3DComparison.GreaterThanOrEqual => left >= right,
                Symbology3DComparison.LessThan => left < right,
                Symbology3DComparison.LessThanOrEqual => left <= right,
                _ => false
            };
        }

        // Ordered comparisons require numeric operands; a non-numeric side
        // can never satisfy them.
        if (rule.Comparison is not (Symbology3DComparison.Equals or Symbology3DComparison.NotEquals))
        {
            return false;
        }

        // String (in)equality fallback. A null attribute compares equal only
        // to a null/empty operand.
        var leftText = raw?.ToString();
        var equal = string.Equals(leftText, rule.Value, StringComparison.Ordinal)
            || (string.IsNullOrEmpty(leftText) && string.IsNullOrEmpty(rule.Value));
        return rule.Comparison == Symbology3DComparison.Equals ? equal : !equal;
    }

    private static object? LookupAttribute(IReadOnlyDictionary<string, object?> attributes, string name)
    {
        if (attributes.TryGetValue(name, out var value))
        {
            return value;
        }

        // Feature attribute bags are typically ordinal-keyed; fall back to a
        // case-insensitive scan so authoring-side casing does not silently
        // drop a rule.
        foreach (var pair in attributes)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static double ClampOpacity(double value)
    {
        if (double.IsNaN(value))
        {
            return 1.0;
        }
        return Math.Clamp(value, 0.0, 1.0);
    }

    private static bool TryToDouble(object? value, out double result)
    {
        switch (value)
        {
            case null:
                result = 0;
                return false;
            case double d:
                result = d;
                return true;
            case float f:
                result = f;
                return true;
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            case short s:
                result = s;
                return true;
            case decimal m:
                result = (double)m;
                return true;
            case string str:
                return TryParseDouble(str, out result);
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryParseDouble(string? text, out double result)
    {
        if (!string.IsNullOrEmpty(text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
        {
            return true;
        }
        result = 0;
        return false;
    }
}
