// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Infrastructure.Rendering;

namespace Honua.Server.Features.Styling.Sld;

internal enum SldValueTargetKind
{
    Color,
    Number,
    HalfNumber,
    Opacity,
    String
}

internal static class SldExpressionConverter
{
    public static MapLibreExpression? ConvertValue(
        SldValueExpression? value,
        SldValueTargetKind targetKind,
        SldDiagnosticContext context)
    {
        return value switch
        {
            null => null,
            SldLiteralValue literal => ConvertLiteral(literal.Value, targetKind),
            SldPropertyValue property => ConvertProperty(property.PropertyName, targetKind),
            SldRecodeExpression recode => ConvertRecode(recode, targetKind, context),
            SldCategorizeExpression categorize => ConvertCategorize(categorize, targetKind, context),
            SldInterpolateExpression interpolate => ConvertInterpolate(interpolate, targetKind, context),
            _ => null
        };
    }

    public static string NormalizeColor(string color, double? opacity)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return color;
        }

        if (color.Length == 9 && color[0] == '#'
            && ParseHexByte(color.AsSpan(1, 2)) is { } a
            && ParseHexByte(color.AsSpan(3, 2)) is { } r
            && ParseHexByte(color.AsSpan(5, 2)) is { } g
            && ParseHexByte(color.AsSpan(7, 2)) is { } b)
        {
            var alpha = a / 255d;
            return string.Create(CultureInfo.InvariantCulture, $"rgba({r},{g},{b},{alpha:0.###})");
        }

        if (opacity.HasValue && opacity.Value < 1d && opacity.Value >= 0d
            && color.Length == 7 && color[0] == '#'
            && ParseHexByte(color.AsSpan(1, 2)) is { } rr
            && ParseHexByte(color.AsSpan(3, 2)) is { } gg
            && ParseHexByte(color.AsSpan(5, 2)) is { } bb)
        {
            return string.Create(CultureInfo.InvariantCulture, $"rgba({rr},{gg},{bb},{opacity.Value:0.###})");
        }

        return color;
    }

    public static MapLibreExpression ParseLiteralExpression(string literal)
    {
        if (string.Equals(literal, "true", StringComparison.OrdinalIgnoreCase))
        {
            return new MapLibreExpression(true);
        }

        if (string.Equals(literal, "false", StringComparison.OrdinalIgnoreCase))
        {
            return new MapLibreExpression(false);
        }

        if (SldExpressionParser.ParseFiniteDouble(literal) is { } number)
        {
            return new MapLibreExpression(number);
        }

        return new MapLibreExpression(literal);
    }

    internal static MapLibreExpression Expr(string op, params MapLibreExpression[] args)
    {
        var items = new MapLibreExpression[args.Length + 1];
        items[0] = new MapLibreExpression(op);
        Array.Copy(args, 0, items, 1, args.Length);
        return new MapLibreExpression(items);
    }

    private static MapLibreExpression? ConvertLiteral(string raw, SldValueTargetKind targetKind)
    {
        return targetKind switch
        {
            SldValueTargetKind.Color => new MapLibreExpression(NormalizeColor(raw, null)),
            SldValueTargetKind.Number => SldExpressionParser.ParseFiniteDouble(raw) is { } n ? new MapLibreExpression(n) : null,
            SldValueTargetKind.HalfNumber => SldExpressionParser.ParseFiniteDouble(raw) is { } d ? new MapLibreExpression(d / 2d) : null,
            SldValueTargetKind.Opacity => SldExpressionParser.ParseFiniteDouble(raw) is { } o ? new MapLibreExpression(o) : null,
            SldValueTargetKind.String => new MapLibreExpression(raw),
            _ => null
        };
    }

    private static MapLibreExpression ConvertProperty(string propertyName, SldValueTargetKind targetKind)
    {
        var get = Expr("get", new MapLibreExpression(propertyName));
        return targetKind == SldValueTargetKind.HalfNumber
            ? Expr("/", get, new MapLibreExpression(2d))
            : get;
    }

    private static MapLibreExpression? ConvertRecode(
        SldRecodeExpression recode,
        SldValueTargetKind targetKind,
        SldDiagnosticContext context)
    {
        var allNumeric = recode.Entries.All(e => IsCanonicalNumericLabel(e.Data));
        var getProp = Expr("get", new MapLibreExpression(recode.PropertyName));
        var items = new List<MapLibreExpression>
        {
            new("match"),
            allNumeric ? getProp : Expr("to-string", getProp)
        };

        var added = allNumeric
            ? AppendNumericRecodeEntries(recode.Entries, targetKind, new RecodeAppendTarget(items, context))
            : AppendStringRecodeEntries(recode.Entries, targetKind, new RecodeAppendTarget(items, context));
        if (added == 0)
        {
            return null;
        }

        var fallback = recode.FallbackValue != null
            ? ConvertLiteral(recode.FallbackValue, targetKind)
            : DefaultFallback(targetKind);
        if (!fallback.HasValue)
        {
            context.Warn("Recode", $"Recode fallback '{recode.FallbackValue}' is not valid for {targetKind}; property omitted.");
            return null;
        }

        items.Add(fallback.Value);
        return new MapLibreExpression(items.ToArray());
    }

    private static int AppendNumericRecodeEntries(
        IReadOnlyList<SldRecodeEntry> entries,
        SldValueTargetKind targetKind,
        RecodeAppendTarget target)
    {
        var seen = new HashSet<double>();
        var count = 0;
        foreach (var entry in entries)
        {
            var number = SldExpressionParser.ParseFiniteDouble(entry.Data)!.Value;
            if (!seen.Add(number))
            {
                target.Context.Warn("Recode", $"Duplicate Recode key '{entry.Data}' was ignored.");
                continue;
            }

            var output = ConvertLiteral(entry.Value, targetKind);
            if (!output.HasValue)
            {
                target.Context.Warn("Recode", $"Recode value '{entry.Value}' is not valid for {targetKind}; property omitted.");
                return 0;
            }

            target.Items.Add(new MapLibreExpression(number));
            target.Items.Add(output.Value);
            count++;
        }

        return count;
    }

    private static int AppendStringRecodeEntries(
        IReadOnlyList<SldRecodeEntry> entries,
        SldValueTargetKind targetKind,
        RecodeAppendTarget target)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var entry in entries)
        {
            if (!seen.Add(entry.Data))
            {
                target.Context.Warn("Recode", $"Duplicate Recode key '{entry.Data}' was ignored.");
                continue;
            }

            var output = ConvertLiteral(entry.Value, targetKind);
            if (!output.HasValue)
            {
                target.Context.Warn("Recode", $"Recode value '{entry.Value}' is not valid for {targetKind}; property omitted.");
                return 0;
            }

            target.Items.Add(new MapLibreExpression(entry.Data));
            target.Items.Add(output.Value);
            count++;
        }

        return count;
    }

    private static bool IsCanonicalNumericLabel(string data)
    {
        if (SldExpressionParser.ParseFiniteDouble(data) is null)
        {
            return false;
        }

        var trimmed = data.Trim();
        var body = trimmed.StartsWith('-') || trimmed.StartsWith('+') ? trimmed[1..] : trimmed;
        return !(body.Length > 1 && body[0] == '0' && char.IsDigit(body[1]));
    }

    private static MapLibreExpression? ConvertCategorize(
        SldCategorizeExpression categorize,
        SldValueTargetKind targetKind,
        SldDiagnosticContext context)
    {
        if (string.Equals(categorize.ThresholdsBelongTo, "preceding", StringComparison.OrdinalIgnoreCase))
        {
            context.Warn("Categorize", "Categorize thresholdsBelongTo='preceding' was mapped to MapLibre step, which uses succeeding threshold boundaries.");
        }

        var initial = ConvertLiteral(categorize.InitialValue, targetKind);
        if (!initial.HasValue)
        {
            context.Warn("Categorize", $"Categorize value '{categorize.InitialValue}' is not valid for {targetKind}; property omitted.");
            return null;
        }

        var items = new List<MapLibreExpression>
        {
            new("step"),
            Expr("get", new MapLibreExpression(categorize.PropertyName)),
            initial.Value
        };

        foreach (var step in categorize.Steps)
        {
            var output = ConvertLiteral(step.Value, targetKind);
            if (!output.HasValue)
            {
                context.Warn("Categorize", $"Categorize value '{step.Value}' is not valid for {targetKind}; property omitted.");
                return null;
            }

            items.Add(new MapLibreExpression(step.Threshold));
            items.Add(output.Value);
        }

        return new MapLibreExpression(items.ToArray());
    }

    private static MapLibreExpression? ConvertInterpolate(
        SldInterpolateExpression interpolate,
        SldValueTargetKind targetKind,
        SldDiagnosticContext context)
    {
        if (!string.IsNullOrEmpty(interpolate.Mode)
            && !string.Equals(interpolate.Mode, "linear", StringComparison.OrdinalIgnoreCase))
        {
            context.Warn("Interpolate", $"Interpolate mode '{interpolate.Mode}' was mapped to linear interpolation in MapLibre.");
        }

        var items = new List<MapLibreExpression>
        {
            new("interpolate"),
            new(new[] { new MapLibreExpression("linear") }),
            Expr("get", new MapLibreExpression(interpolate.PropertyName))
        };

        foreach (var point in interpolate.Points)
        {
            var output = ConvertLiteral(point.Value, targetKind);
            if (!output.HasValue)
            {
                context.Warn("Interpolate", $"Interpolate value '{point.Value}' is not valid for {targetKind}; property omitted.");
                return null;
            }

            items.Add(new MapLibreExpression(point.Data));
            items.Add(output.Value);
        }

        return new MapLibreExpression(items.ToArray());
    }

    private static MapLibreExpression DefaultFallback(SldValueTargetKind targetKind)
    {
        return targetKind switch
        {
            SldValueTargetKind.Color => new MapLibreExpression("rgba(0,0,0,0)"),
            SldValueTargetKind.Number or SldValueTargetKind.HalfNumber => new MapLibreExpression(0d),
            SldValueTargetKind.Opacity => new MapLibreExpression(0d),
            _ => new MapLibreExpression(string.Empty)
        };
    }

    private static int? ParseHexByte(ReadOnlySpan<char> span)
        => int.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private readonly record struct RecodeAppendTarget(
        List<MapLibreExpression> Items,
        SldDiagnosticContext Context);
}
