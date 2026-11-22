// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Xml.Linq;

namespace Honua.Server.Features.Styling.Sld;

internal static class SldExpressionParser
{
    public static SldValueExpression? ParseValueExpression(
        XElement? element,
        SldDiagnosticContext context,
        string parameterName)
    {
        if (element == null)
        {
            return null;
        }

        var children = element.Elements().ToList();
        if (children.Count == 0)
        {
            var text = element.Value?.Trim();
            return string.IsNullOrEmpty(text) ? null : new SldLiteralValue(text);
        }

        var first = children[0];
        return first.Name.LocalName switch
        {
            "Literal" => ParseLiteralChild(first),
            "PropertyName" or "ValueReference" => ParsePropertyChild(first),
            "Recode" => ParseSeRecode(first, context),
            "Categorize" => ParseSeCategorize(first, context),
            "Interpolate" => ParseSeInterpolate(first, context),
            "Function" => SldOgcFunctionParser.ParseFunction(first, context),
            _ => RecordUnsupported(first.Name.LocalName, $"Unsupported expression in {parameterName}.", context)
        };
    }

    internal static SldValueExpression? RecordUnsupported(string construct, string message, SldDiagnosticContext context)
    {
        context.Warn(construct, message);
        return null;
    }

    internal static bool AreStrictlyAscending(IEnumerable<double> values)
    {
        double? previous = null;
        foreach (var value in values)
        {
            if (previous.HasValue && value <= previous.Value)
            {
                return false;
            }

            previous = value;
        }

        return true;
    }

    internal static double? ParseFiniteDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed)
            ? parsed
            : null;
    }

    internal static XElement? ChildLocal(XElement parent, string localName)
        => parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static SldLiteralValue? ParseLiteralChild(XElement literalElement)
    {
        var text = literalElement.Value?.Trim();
        return string.IsNullOrEmpty(text) ? null : new SldLiteralValue(text);
    }

    private static SldPropertyValue? ParsePropertyChild(XElement propertyElement)
    {
        var prop = propertyElement.Value?.Trim();
        return string.IsNullOrEmpty(prop) ? null : new SldPropertyValue(prop);
    }

    private static SldValueExpression? ParseSeRecode(XElement element, SldDiagnosticContext context)
    {
        var lookup = ExtractLookupProperty(ChildLocal(element, "LookupValue"));
        if (string.IsNullOrEmpty(lookup))
        {
            return RecordUnsupported("Recode", "Recode requires a PropertyName LookupValue.", context);
        }

        var entries = element.Elements()
            .Where(e => e.Name.LocalName == "MapItem")
            .Select(item => (Data: ChildLocal(item, "Data")?.Value?.Trim(), Value: ChildLocal(item, "Value")?.Value?.Trim()))
            .Where(pair => !string.IsNullOrEmpty(pair.Data) && !string.IsNullOrEmpty(pair.Value))
            .Select(pair => new SldRecodeEntry(pair.Data!, pair.Value!))
            .ToList();

        if (entries.Count == 0)
        {
            return RecordUnsupported("Recode", "Recode contains no valid MapItem entries.", context);
        }

        var fallback = element.Attribute("fallbackValue")?.Value?.Trim();
        return new SldRecodeExpression(lookup, entries, string.IsNullOrEmpty(fallback) ? null : fallback);
    }

    private static SldValueExpression? ParseSeCategorize(XElement element, SldDiagnosticContext context)
    {
        var lookup = ExtractLookupProperty(ChildLocal(element, "LookupValue"));
        if (string.IsNullOrEmpty(lookup))
        {
            return RecordUnsupported("Categorize", "Categorize requires a PropertyName LookupValue.", context);
        }

        var parsed = CollectCategorizeSteps(element);
        if (parsed == null || !AreStrictlyAscending(parsed.Value.Steps.Select(s => s.Threshold)))
        {
            return RecordUnsupported("Categorize", "Categorize requires an initial value and strictly ascending numeric thresholds.", context);
        }

        var belongTo = element.Attribute("threshholdsBelongTo")?.Value?.Trim()
            ?? element.Attribute("thresholdsBelongTo")?.Value?.Trim();
        return new SldCategorizeExpression
        {
            PropertyName = lookup,
            InitialValue = parsed.Value.InitialValue,
            Steps = parsed.Value.Steps,
            ThresholdsBelongTo = belongTo
        };
    }

    private static (string InitialValue, List<SldCategorizeStep> Steps)? CollectCategorizeSteps(XElement element)
    {
        var items = element.Elements()
            .Where(e => e.Name.LocalName is "Value" or "Threshold")
            .ToList();
        if (items.Count < 3 || items.Count % 2 == 0 || items[0].Name.LocalName != "Value")
        {
            return null;
        }

        var initialValue = items[0].Value?.Trim();
        if (string.IsNullOrEmpty(initialValue))
        {
            return null;
        }

        var steps = new List<SldCategorizeStep>();
        for (var i = 1; i < items.Count; i += 2)
        {
            if (items[i].Name.LocalName != "Threshold"
                || items[i + 1].Name.LocalName != "Value"
                || ParseFiniteDouble(items[i].Value) is not { } threshold)
            {
                return null;
            }

            var value = items[i + 1].Value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            steps.Add(new SldCategorizeStep(threshold, value));
        }

        return (initialValue, steps);
    }

    private static SldValueExpression? ParseSeInterpolate(XElement element, SldDiagnosticContext context)
    {
        var lookup = ExtractLookupProperty(ChildLocal(element, "LookupValue"));
        if (string.IsNullOrEmpty(lookup))
        {
            return RecordUnsupported("Interpolate", "Interpolate requires a PropertyName LookupValue.", context);
        }

        var points = CollectInterpolatePoints(element);
        if (points == null || points.Count < 2 || !AreStrictlyAscending(points.Select(p => p.Data)))
        {
            return RecordUnsupported(
                "Interpolate",
                "Interpolate requires at least two InterpolationPoint entries with strictly ascending Data values.",
                context);
        }

        return new SldInterpolateExpression
        {
            PropertyName = lookup,
            Points = points,
            Mode = element.Attribute("mode")?.Value?.Trim(),
            Method = element.Attribute("method")?.Value?.Trim()
        };
    }

    private static List<SldInterpolatePoint>? CollectInterpolatePoints(XElement element)
    {
        var points = new List<SldInterpolatePoint>();
        foreach (var pt in element.Elements().Where(e => e.Name.LocalName == "InterpolationPoint"))
        {
            var data = ParseFiniteDouble(ChildLocal(pt, "Data")?.Value);
            var valueText = ChildLocal(pt, "Value")?.Value?.Trim();
            if (!data.HasValue || string.IsNullOrEmpty(valueText))
            {
                return null;
            }

            points.Add(new SldInterpolatePoint(data.Value, valueText));
        }

        return points;
    }

    private static string? ExtractLookupProperty(XElement? lookupElement)
        => lookupElement?.Elements()
            .FirstOrDefault(e => e.Name.LocalName is "PropertyName" or "ValueReference")
            ?.Value?.Trim();
}
