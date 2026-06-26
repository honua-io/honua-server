// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Geoprocessing.Testing;

/// <summary>
/// Compares a raster/scalar/tabular artifact (e.g. a GDAL convert/CSV output, a raster
/// statistics document, a scalar metric) against its golden with a numeric tolerance
/// rather than byte-equality (GP Devkit P6, issue #2127).
///
/// <para>
/// When both sides parse as JSON, they are diffed structurally: objects must share a key
/// set, arrays must share a length, and number leaves match within
/// <see cref="GoldenTolerance.Numeric"/> (so GDAL's least-significant-digit formatting drift
/// does not fail a stable golden). Otherwise the payloads are diffed as normalized text —
/// line-endings unified and trailing whitespace trimmed — and the first differing line is
/// reported.
/// </para>
/// </summary>
public static class ScalarStructuralGoldenComparer
{
    /// <summary>
    /// Compares <paramref name="actual"/> against <paramref name="golden"/>.
    /// </summary>
    /// <param name="actual">The text the process produced.</param>
    /// <param name="golden">The recorded golden text.</param>
    /// <param name="tolerance">The numeric tolerance to apply to JSON number leaves.</param>
    /// <returns>A matched result, or a mismatch carrying located differences.</returns>
    public static GoldenComparisonResult Compare(string actual, string golden, GoldenTolerance tolerance)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(golden);

        if (TryParse(actual, out var actualJson) && TryParse(golden, out var goldenJson))
        {
            using (actualJson)
            using (goldenJson)
            {
                var differences = new List<string>();
                CompareJson("$", actualJson.RootElement, goldenJson.RootElement, tolerance, differences);
                return differences.Count == 0
                    ? GoldenComparisonResult.Match
                    : GoldenComparisonResult.Mismatch(
                        $"Scalar/structural golden mismatch: {differences.Count} JSON difference(s) "
                            + $"beyond tolerance (numeric={tolerance.Numeric:R}).",
                        differences);
            }
        }

        return CompareText(actual, golden);
    }

    private static void CompareJson(
        string path,
        JsonElement actual,
        JsonElement golden,
        GoldenTolerance tolerance,
        List<string> differences)
    {
        if (actual.ValueKind != golden.ValueKind)
        {
            // A number that round-trips through both kinds is still numeric; only flag a
            // genuine kind change (e.g. object vs array, string vs number).
            differences.Add($"{path}: kind actual={actual.ValueKind}, golden={golden.ValueKind}");
            return;
        }

        switch (golden.ValueKind)
        {
            case JsonValueKind.Object:
                CompareObject(path, actual, golden, tolerance, differences);
                break;
            case JsonValueKind.Array:
                CompareArray(path, actual, golden, tolerance, differences);
                break;
            case JsonValueKind.Number:
                var a = actual.GetDouble();
                var g = golden.GetDouble();
                var delta = Math.Abs(a - g);
                if (delta > tolerance.Numeric)
                {
                    differences.Add($"{path}: actual={a:R}, golden={g:R}, |delta|={delta:R} > tol={tolerance.Numeric:R}");
                }

                break;
            case JsonValueKind.String:
                if (!string.Equals(actual.GetString(), golden.GetString(), StringComparison.Ordinal))
                {
                    differences.Add($"{path}: actual=\"{actual.GetString()}\", golden=\"{golden.GetString()}\"");
                }

                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                break;
            default:
                break;
        }
    }

    private static void CompareObject(
        string path,
        JsonElement actual,
        JsonElement golden,
        GoldenTolerance tolerance,
        List<string> differences)
    {
        foreach (var property in golden.EnumerateObject())
        {
            if (!actual.TryGetProperty(property.Name, out var actualValue))
            {
                differences.Add($"{path}.{property.Name}: missing in actual");
                continue;
            }

            CompareJson($"{path}.{property.Name}", actualValue, property.Value, tolerance, differences);
        }

        foreach (var property in actual.EnumerateObject())
        {
            if (!golden.TryGetProperty(property.Name, out _))
            {
                differences.Add($"{path}.{property.Name}: unexpected in actual");
            }
        }
    }

    private static void CompareArray(
        string path,
        JsonElement actual,
        JsonElement golden,
        GoldenTolerance tolerance,
        List<string> differences)
    {
        var actualLength = actual.GetArrayLength();
        var goldenLength = golden.GetArrayLength();
        if (actualLength != goldenLength)
        {
            differences.Add($"{path}: array length actual={actualLength}, golden={goldenLength}");
            return;
        }

        var index = 0;
        var actualEnumerator = actual.EnumerateArray();
        var goldenEnumerator = golden.EnumerateArray();
        while (actualEnumerator.MoveNext() && goldenEnumerator.MoveNext())
        {
            CompareJson($"{path}[{index}]", actualEnumerator.Current, goldenEnumerator.Current, tolerance, differences);
            index++;
        }
    }

    private static GoldenComparisonResult CompareText(string actual, string golden)
    {
        var actualLines = Normalize(actual);
        var goldenLines = Normalize(golden);

        if (actualLines.Length == goldenLines.Length)
        {
            var differences = new List<string>();
            for (var i = 0; i < goldenLines.Length; i++)
            {
                if (!string.Equals(actualLines[i], goldenLines[i], StringComparison.Ordinal))
                {
                    differences.Add($"line {i + 1}: actual=\"{actualLines[i]}\", golden=\"{goldenLines[i]}\"");
                }
            }

            return differences.Count == 0
                ? GoldenComparisonResult.Match
                : GoldenComparisonResult.Mismatch(
                    $"Scalar/structural golden mismatch: {differences.Count} text line(s) differ.",
                    differences);
        }

        return GoldenComparisonResult.Mismatch(
            $"Scalar/structural golden mismatch: line count actual={actualLines.Length}, golden={goldenLines.Length}.",
            [$"actual has {actualLines.Length} line(s); golden has {goldenLines.Length} line(s)"]);
    }

    private static string[] Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.TrimEnd())
            .ToArray();

    private static bool TryParse(string text, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }
}
