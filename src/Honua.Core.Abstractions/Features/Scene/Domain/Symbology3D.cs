// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Honua.Core.Serialization;

namespace Honua.Core.Features.Scene.Domain;

public sealed record Symbology3D : ExtensibleJsonRecord
{
    public Symbology3DColor? DefaultColor { get; init; }

    public double? DefaultOpacity { get; init; }

    public IReadOnlyList<Symbology3DRule> Rules { get; init; } = [];
}

public sealed record Symbology3DRule : ExtensibleJsonRecord
{
    public required string Attribute { get; init; }

    [JsonIgnore]
    public Symbology3DComparison Comparison
    {
        get => ComparisonValue.Value;
        init => ComparisonValue = value;
    }

    [JsonPropertyName("comparison")]
    [JsonConverter(typeof(PreservedNumericEnumJsonConverter<Symbology3DComparison>))]
    public PreservedEnumValue<Symbology3DComparison> ComparisonValue { get; init; } = Symbology3DComparison.Equals;

    public string? Value { get; init; }

    public Symbology3DColor? Color { get; init; }

    public double? Opacity { get; init; }

    public bool? Visible { get; init; }
}

public enum Symbology3DComparison
{
    Equals,

    NotEquals,

    GreaterThan,

    GreaterThanOrEqual,

    LessThan,

    LessThanOrEqual,

    Unknown = -1
}

public readonly record struct Symbology3DColor(byte Red, byte Green, byte Blue)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }

    public bool Equals(Symbology3DColor other)
        => Red == other.Red && Green == other.Green && Blue == other.Blue &&
           JsonExtensionDataEquality.Equals(AdditionalProperties, other.AdditionalProperties);

    public override int GetHashCode()
        => HashCode.Combine(Red, Green, Blue, JsonExtensionDataEquality.PropertyNamesHashCode(AdditionalProperties));

    public static Symbology3DColor White => new(255, 255, 255);

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

public readonly record struct ResolvedSymbology(Symbology3DColor Color, double Opacity, bool Visible)
{
    public static ResolvedSymbology Default => new(Symbology3DColor.White, 1.0, true);
}

public static class Symbology3DResolver
{
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
                Symbology3DComparison.Equals => AreNumericallyEqual(left, right),
                Symbology3DComparison.NotEquals => !AreNumericallyEqual(left, right),
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
        foreach (var pair in attributes.Where(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)))
        {
            return pair.Value;
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

    // Rule values are frequently authored/round-tripped as strings (JSON/YAML
    // style specs) and attribute values may arrive as float/decimal, so exact
    // double equality can miss matches that are numerically identical modulo
    // representation error. Compare with a small absolute epsilon.
    private const double NumericEqualityEpsilon = 1e-9;

    private static bool AreNumericallyEqual(double left, double right)
        => Math.Abs(left - right) <= NumericEqualityEpsilon;

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
