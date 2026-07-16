// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;

namespace Honua.Infrastructure.Rendering;

/// <summary>
/// A single discrete legend class: a display label plus the synthetic feature
/// attributes that select this class when the style layer is resolved.
/// </summary>
internal readonly record struct LegendClass(
    string Label,
    ImmutableDictionary<string, object?> Properties);

/// <summary>
/// The discrete classes a style layer resolves to, plus the reason the layer's
/// classifying expression could not be enumerated when it could not.
/// </summary>
internal sealed class LegendClassSet
{
    /// <summary>
    /// Ordered legend classes. Always contains at least one entry.
    /// </summary>
    public required IReadOnlyList<LegendClass> Classes { get; init; }

    /// <summary>
    /// The feature attribute the classes are keyed on, when the layer is data-driven.
    /// </summary>
    public string? Field { get; init; }

    /// <summary>
    /// Populated when the classifying expression is data-driven but cannot be
    /// projected onto discrete legend entries. <see cref="Classes"/> then holds a
    /// single representative entry and callers must surface this reason rather than
    /// present the entry as a complete legend.
    /// </summary>
    public string? UnrepresentableReason { get; init; }

    /// <summary>
    /// Whether the layer resolved to enumerable data-driven classes.
    /// </summary>
    public bool IsDataDriven => Field != null && UnrepresentableReason == null;
}

/// <summary>
/// Projects a MapLibre style layer onto the discrete classes a legend can show.
///
/// The classifier never evaluates paint values itself: it enumerates the *domain*
/// of the layer's classifying expression and emits the synthetic feature attributes
/// that select each branch. Callers feed those attributes back through the same
/// <see cref="StyleTranslator"/>/<see cref="SkiaMapRenderer"/> path that GetMap uses,
/// so a swatch cannot drift from what the map actually draws.
/// </summary>
internal static class LegendClassifier
{
    private const string MatchOperator = "match";
    private const string StepOperator = "step";
    private const string InterpolateOperator = "interpolate";
    private const string GetOperator = "get";

    /// <summary>
    /// Classifies a style layer into the legend entries it can honestly represent.
    /// </summary>
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

    /// <summary>
    /// A layer is only ever classified on its colour property, so an "interpolate"
    /// here is always a continuous colour ramp.
    /// </summary>
    /// <remarks>
    /// The ramp is sampled at its own stops. A stop is the one input an "interpolate" resolves
    /// exactly, for every interpolation type: the evaluator returns that stop's output verbatim
    /// rather than blending toward a neighbour, so "linear", "exponential" and "cubic-bezier"
    /// ramps all sample identically here and the curve between stops never has to be modelled.
    /// Each entry is therefore the colour GetMap paints for a feature carrying that value — the
    /// same guarantee "match" and "step" entries carry — and the sampling is what a continuous
    /// domain permits: exact at the labelled values, continuous between them.
    /// </remarks>
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
