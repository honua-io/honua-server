// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;

namespace Honua.Server.Features.Styling.Sld;

internal static class SldOgcFunctionParser
{
    public static SldValueExpression? ParseFunction(XElement element, SldDiagnosticContext context)
    {
        var name = element.Attribute("name")?.Value?.Trim();
        if (string.Equals(name, "Recode", StringComparison.OrdinalIgnoreCase))
        {
            return ParseRecode(element, context);
        }

        if (string.Equals(name, "Categorize", StringComparison.OrdinalIgnoreCase))
        {
            return ParseCategorize(element, context);
        }

        if (string.Equals(name, "Interpolate", StringComparison.OrdinalIgnoreCase))
        {
            return ParseInterpolate(element, context);
        }

        return SldExpressionParser.RecordUnsupported(
            "OgcFunction",
            $"OGC Function '{name}' is not supported in style parameters and was omitted.",
            context);
    }

    private static SldValueExpression? ParseRecode(XElement element, SldDiagnosticContext context)
    {
        var args = element.Elements().ToList();
        var lookup = args.Count >= 3 && (args.Count - 1) % 2 == 0 ? ExtractPropertyArg(args[0]) : null;
        if (string.IsNullOrEmpty(lookup))
        {
            return SldExpressionParser.RecordUnsupported("Recode", "OGC Function Recode requires a PropertyName and literal key/value pairs.", context);
        }

        var entries = new List<SldRecodeEntry>();
        for (var i = 1; i < args.Count - 1; i += 2)
        {
            var data = ExtractLiteralArg(args[i]);
            var value = ExtractLiteralArg(args[i + 1]);
            if (data == null || value == null)
            {
                return SldExpressionParser.RecordUnsupported("Recode", "OGC Function Recode requires Literal key/value arguments.", context);
            }

            entries.Add(new SldRecodeEntry(data, value));
        }

        var fallback = element.Attribute("fallbackValue")?.Value?.Trim();
        return new SldRecodeExpression(lookup, entries, string.IsNullOrEmpty(fallback) ? null : fallback);
    }

    private static SldValueExpression? ParseCategorize(XElement element, SldDiagnosticContext context)
    {
        var args = element.Elements().ToList();
        var lookup = args.Count >= 4 ? ExtractPropertyArg(args[0]) : null;
        if (string.IsNullOrEmpty(lookup))
        {
            return SldExpressionParser.RecordUnsupported("Categorize", "OGC Function Categorize requires a PropertyName, initial value, and threshold/value pairs.", context);
        }

        var (endIndex, belongTo) = StripCategorizeBelongTo(args);
        var initialValue = ExtractLiteralArg(args[1]);
        if (endIndex < 4 || (endIndex - 2) % 2 != 0 || initialValue == null)
        {
            return SldExpressionParser.RecordUnsupported("Categorize", "OGC Function Categorize has mismatched or non-literal arguments.", context);
        }

        var steps = CollectFunctionCategorizeSteps(args, endIndex);
        if (steps == null || !SldExpressionParser.AreStrictlyAscending(steps.Select(s => s.Threshold)))
        {
            return SldExpressionParser.RecordUnsupported("Categorize", "OGC Function Categorize requires strictly ascending literal numeric thresholds.", context);
        }

        return new SldCategorizeExpression
        {
            PropertyName = lookup,
            InitialValue = initialValue,
            Steps = steps,
            ThresholdsBelongTo = belongTo
        };
    }

    private static (int EndIndex, string? BelongTo) StripCategorizeBelongTo(List<XElement> args)
    {
        var lastText = ExtractLiteralArg(args[^1]);
        if (string.Equals(lastText, "succeeding", StringComparison.OrdinalIgnoreCase)
            || string.Equals(lastText, "preceding", StringComparison.OrdinalIgnoreCase))
        {
            return (args.Count - 1, lastText);
        }

        return (args.Count, null);
    }

    private static List<SldCategorizeStep>? CollectFunctionCategorizeSteps(List<XElement> args, int endIndex)
    {
        var steps = new List<SldCategorizeStep>();
        for (var i = 2; i < endIndex - 1; i += 2)
        {
            var threshold = SldExpressionParser.ParseFiniteDouble(ExtractLiteralArg(args[i]));
            var value = ExtractLiteralArg(args[i + 1]);
            if (!threshold.HasValue || value == null)
            {
                return null;
            }

            steps.Add(new SldCategorizeStep(threshold.Value, value));
        }

        return steps.Count > 0 ? steps : null;
    }

    private static SldValueExpression? ParseInterpolate(XElement element, SldDiagnosticContext context)
    {
        var args = element.Elements().ToList();
        var lookup = args.Count >= 5 ? ExtractPropertyArg(args[0]) : null;
        if (string.IsNullOrEmpty(lookup))
        {
            return SldExpressionParser.RecordUnsupported("Interpolate", "OGC Function Interpolate requires a PropertyName and stop/value pairs.", context);
        }

        var options = ParseInterpolateTrailingOptions(args);
        if (options.EndIndex < 5 || (options.EndIndex - 1) % 2 != 0)
        {
            return SldExpressionParser.RecordUnsupported("Interpolate", "OGC Function Interpolate has mismatched stop/value arguments.", context);
        }

        var points = CollectFunctionInterpolatePoints(args, options.EndIndex);
        if (points == null || points.Count < 2 || !SldExpressionParser.AreStrictlyAscending(points.Select(p => p.Data)))
        {
            return SldExpressionParser.RecordUnsupported("Interpolate", "OGC Function Interpolate requires strictly ascending literal numeric stops.", context);
        }

        return new SldInterpolateExpression
        {
            PropertyName = lookup,
            Points = points,
            Mode = options.Mode,
            Method = options.Method
        };
    }

    private static (int EndIndex, string? Mode, string? Method) ParseInterpolateTrailingOptions(List<XElement> args)
    {
        var endIndex = args.Count;
        string? mode = null;
        string? method = null;

        for (var pass = 0; pass < 2 && endIndex > 3; pass++)
        {
            var candidate = ExtractLiteralArg(args[endIndex - 1]);
            if (mode == null && IsInterpolateMode(candidate))
            {
                mode = candidate;
                endIndex--;
            }
            else if (method == null && IsInterpolateMethod(candidate))
            {
                method = candidate;
                endIndex--;
            }
        }

        return (endIndex, mode, method);
    }

    private static List<SldInterpolatePoint>? CollectFunctionInterpolatePoints(List<XElement> args, int endIndex)
    {
        var points = new List<SldInterpolatePoint>();
        for (var i = 1; i < endIndex - 1; i += 2)
        {
            var data = SldExpressionParser.ParseFiniteDouble(ExtractLiteralArg(args[i]));
            var value = ExtractLiteralArg(args[i + 1]);
            if (!data.HasValue || value == null)
            {
                return null;
            }

            points.Add(new SldInterpolatePoint(data.Value, value));
        }

        return points;
    }

    private static bool IsInterpolateMode(string? candidate)
        => string.Equals(candidate, "linear", StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate, "cosine", StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate, "cubic", StringComparison.OrdinalIgnoreCase);

    private static bool IsInterpolateMethod(string? candidate)
        => string.Equals(candidate, "numeric", StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate, "color", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractPropertyArg(XElement arg)
        => arg.Name.LocalName is "PropertyName" or "ValueReference" ? arg.Value?.Trim() : null;

    private static string? ExtractLiteralArg(XElement arg)
    {
        if (arg.Name.LocalName != "Literal" || arg.HasElements)
        {
            return null;
        }

        var text = arg.Value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
