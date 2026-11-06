// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;

namespace Honua.Infrastructure.Rendering;

internal readonly record struct LegendClass(
    string Label,
    ImmutableDictionary<string, object?> Properties);

internal sealed class LegendClassSet
{
    public required IReadOnlyList<LegendClass> Classes { get; init; }

    public string? Field { get; init; }

    // Callers must surface this reason instead of presenting the representative entry as a complete legend.
    public string? UnrepresentableReason { get; init; }

    public bool IsDataDriven => Field != null && UnrepresentableReason == null;
}

// A swatch is produced by feeding synthetic attributes through the same path the map uses, so the legend cannot drift from the drawing.
internal static class LegendClassifier
{
    private const string MatchOperator = "match";
    private const string StepOperator = "step";
    private const string InterpolateOperator = "interpolate";
    private const string GetOperator = "get";

    internal static LegendClassSet Classify(MapLibreStyleLayer styleLayer)
    {
        var fallback = new LegendClassSet
        {
            Classes = [new LegendClass(DefaultLabel(styleLayer), ImmutableDictionary<string, object?>.Empty)]
        };

        var classifyingProperty = GetClassifyingProperty(styleLayer.Type);
        if (classifyingProperty == null || styleLayer.Paint == null)
        {
            return fallback;
        }

        if (!styleLayer.Paint.TryGetValue(classifyingProperty, out var expression) ||
            expression.Kind != MapLibreExpressionKind.Array ||
            expression.Items is not { Length: > 0 } items ||
            items[0].Kind != MapLibreExpressionKind.String)
        {
            return fallback;
        }

        var op = items[0].StringValue;
        return op switch
        {
            MatchOperator => ClassifyMatch(items) ?? fallback,
            StepOperator => ClassifyStep(items) ?? fallback,
            InterpolateOperator => ClassifyInterpolate(items) ?? fallback,
            _ => new LegendClassSet
            {
                Classes = fallback.Classes,
                Field = TryGetField(items.Length > 1 ? items[1] : default),
                UnrepresentableReason =
                    $"'{classifyingProperty}' uses the '{op}' expression, whose branches are arbitrary "
                    + "predicates rather than a finite set of attribute values, so it cannot be enumerated "
                    + "as discrete legend entries."
            }
        };
    }

    private static LegendClassSet? ClassifyMatch(MapLibreExpression[] items)
    {
        // match, input, label1, output1, ..., fallback
        if (items.Length < 4)
        {
            return null;
        }

        var field = TryGetField(items[1]);
        if (field == null)
        {
            return null;
        }

        var classes = new List<LegendClass>();
        for (var i = 2; i < items.Length - 1; i += 2)
        {
            var label = items[i];
            if (label.Kind == MapLibreExpressionKind.Array && label.Items is { Length: > 0 } grouped)
            {
                foreach (var member in grouped)
                {
                    AddLiteralClass(classes, field, member);
                }

                continue;
            }

            AddLiteralClass(classes, field, label);
        }

        if (classes.Count == 0)
        {
            return null;
        }

        // The trailing fallback arm is reachable for any value outside the labels
        // above; an attribute absent from the synthetic properties selects it.
        classes.Add(new LegendClass("Other", ImmutableDictionary<string, object?>.Empty));

        return new LegendClassSet { Classes = classes, Field = field };
    }

    private static LegendClassSet? ClassifyStep(MapLibreExpression[] items)
    {
        // step, input, default, stop1, output1, stop2, output2, ...
        if (items.Length < 4)
        {
            return null;
        }

        var field = TryGetField(items[1]);
        if (field == null)
        {
            return null;
        }

        var stops = new List<double>();
        for (var i = 3; i < items.Length - 1; i += 2)
        {
            if (items[i].Kind != MapLibreExpressionKind.Number)
            {
                return null;
            }

            stops.Add(items[i].NumberValue);
        }

        if (stops.Count == 0)
        {
            return null;
        }

        var classes = new List<LegendClass>
        {
            // Below the first stop the "step" default arm applies. Any value strictly
            // below stop1 selects it; the evaluator compares with >=, so stop1 - 1 is safe.
            new(
                $"< {FormatNumber(stops[0])}",
                PropertiesFor(field, stops[0] - 1))
        };

        for (var i = 0; i < stops.Count; i++)
        {
            var label = i == stops.Count - 1
                ? $">= {FormatNumber(stops[i])}"
                : $"{FormatNumber(stops[i])} - {FormatNumber(stops[i + 1])}";
            classes.Add(new LegendClass(label, PropertiesFor(field, stops[i])));
        }

        return new LegendClassSet { Classes = classes, Field = field };
    }

    // An interpolate ramp is sampled at its stops because those inputs resolve exactly and the curve between them is not a legend class.
    private static LegendClassSet? ClassifyInterpolate(MapLibreExpression[] items)
    {
        // interpolate, ["linear"], input, stop1, output1, stop2, output2, ...
        if (items.Length < 5)
        {
            return null;
        }

        var field = TryGetField(items[2]);
        if (field == null)
        {
            return null;
        }

        var classes = new List<LegendClass>();
        for (var i = 3; i < items.Length - 1; i += 2)
        {
            if (items[i].Kind != MapLibreExpressionKind.Number)
            {
                // MapLibre requires numeric interpolate stops; anything else is a style we
                // cannot enumerate rather than one we can sample.
                return null;
            }

            var stop = items[i].NumberValue;
            classes.Add(new LegendClass(FormatNumber(stop), PropertiesFor(field, stop)));
        }

        return classes.Count == 0
            ? null
            : new LegendClassSet { Classes = classes, Field = field };
    }

    private static void AddLiteralClass(List<LegendClass> classes, string field, MapLibreExpression label)
    {
        switch (label.Kind)
        {
            case MapLibreExpressionKind.String when label.StringValue != null:
                classes.Add(new LegendClass(label.StringValue, PropertiesFor(field, label.StringValue)));
                break;
            case MapLibreExpressionKind.Number:
                classes.Add(new LegendClass(FormatNumber(label.NumberValue), PropertiesFor(field, label.NumberValue)));
                break;
            case MapLibreExpressionKind.Boolean:
                classes.Add(new LegendClass(
                    label.BoolValue ? "true" : "false",
                    PropertiesFor(field, label.BoolValue)));
                break;
        }
    }

    private static ImmutableDictionary<string, object?> PropertiesFor(string field, object? value)
        => ImmutableDictionary<string, object?>.Empty.Add(field, value);

    private static string? GetClassifyingProperty(string? layerType) => layerType switch
    {
        "fill" => "fill-color",
        "line" => "line-color",
        "circle" => "circle-color",
        _ => null
    };

    private static string? TryGetField(MapLibreExpression expression)
    {
        if (expression.Kind != MapLibreExpressionKind.Array || expression.Items is not { Length: > 0 } items)
        {
            return null;
        }

        if (items[0].Kind != MapLibreExpressionKind.String)
        {
            return null;
        }

        if (string.Equals(items[0].StringValue, GetOperator, StringComparison.Ordinal))
        {
            return items.Length > 1 && items[1].Kind == MapLibreExpressionKind.String
                ? items[1].StringValue
                : null;
        }

        // Unwrap the coercion wrappers Studio emits around a plain attribute read
        // (["to-string", ["get", "kind"]]); anything else is not a bare attribute.
        if (items.Length == 2 &&
            (string.Equals(items[0].StringValue, "to-string", StringComparison.Ordinal) ||
             string.Equals(items[0].StringValue, "to-number", StringComparison.Ordinal)))
        {
            return TryGetField(items[1]);
        }

        return null;
    }

    private static string DefaultLabel(MapLibreStyleLayer styleLayer)
        => styleLayer.Id ?? styleLayer.Type ?? "Default";

    private static string FormatNumber(double value)
        => value.ToString("0.####", CultureInfo.InvariantCulture);
}
