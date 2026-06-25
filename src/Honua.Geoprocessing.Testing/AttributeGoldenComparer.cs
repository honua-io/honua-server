// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Testing;

/// <summary>
/// Diffs the non-geometry attributes of two GeoJSON features (GP Devkit P6, issue #2127):
/// the same key set, with numeric values compared within
/// <see cref="GoldenTolerance.Numeric"/> and everything else compared by invariant string
/// form. Differences are appended to the caller's list with a located path so a failing
/// golden points at the exact property.
/// </summary>
internal static class AttributeGoldenComparer
{
    public static void Compare(
        string path,
        IAttributesTable? actual,
        IAttributesTable? golden,
        GoldenTolerance tolerance,
        List<string> differences)
    {
        var actualNames = actual?.GetNames() ?? [];
        var goldenNames = golden?.GetNames() ?? [];

        foreach (var name in goldenNames)
        {
            if (actual is null || !actual.Exists(name))
            {
                differences.Add($"{path}.{name}: missing in actual (golden={Stringify(golden?.GetOptionalValue(name))})");
                continue;
            }

            CompareValue($"{path}.{name}", actual.GetOptionalValue(name), golden!.GetOptionalValue(name), tolerance, differences);
        }

        foreach (var name in actualNames)
        {
            if (golden is null || !golden.Exists(name))
            {
                differences.Add($"{path}.{name}: unexpected in actual (actual={Stringify(actual?.GetOptionalValue(name))})");
            }
        }
    }

    private static void CompareValue(
        string path,
        object? actual,
        object? golden,
        GoldenTolerance tolerance,
        List<string> differences)
    {
        if (TryToDouble(actual, out var actualNumber) && TryToDouble(golden, out var goldenNumber))
        {
            var delta = Math.Abs(actualNumber - goldenNumber);
            if (delta > tolerance.Numeric)
            {
                differences.Add(
                    $"{path}: actual={actualNumber:R}, golden={goldenNumber:R}, |delta|={delta:R} > tol={tolerance.Numeric:R}");
            }

            return;
        }

        var actualText = Stringify(actual);
        var goldenText = Stringify(golden);
        if (!string.Equals(actualText, goldenText, StringComparison.Ordinal))
        {
            differences.Add($"{path}: actual={actualText}, golden={goldenText}");
        }
    }

    private static bool TryToDouble(object? value, out double number)
    {
        switch (value)
        {
            case null:
                number = 0;
                return false;
            case double d:
                number = d;
                return true;
            case float f:
                number = f;
                return true;
            case decimal m:
                number = (double)m;
                return true;
            case long l:
                number = l;
                return true;
            case int i:
                number = i;
                return true;
            case short s:
                number = s;
                return true;
            default:
                return double.TryParse(
                    value.ToString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out number);
        }
    }

    private static string Stringify(object? value) => value switch
    {
        null => "<null>",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "<null>",
    };
}
