// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.ObjectModel;

namespace Honua.Geoprocessing.Testing;

public sealed class GoldenComparisonResult
{
    private GoldenComparisonResult(bool matched, IReadOnlyList<string> differences, string? summary)
    {
        Matched = matched;
        Differences = differences;
        Summary = summary;
    }

    public bool Matched { get; }

    public IReadOnlyList<string> Differences { get; }

    public string? Summary { get; }

    public static GoldenComparisonResult Match { get; } =
        new(matched: true, differences: Array.Empty<string>(), summary: null);

    public static GoldenComparisonResult Mismatch(string summary, IEnumerable<string> differences)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        ArgumentNullException.ThrowIfNull(differences);
        var list = new ReadOnlyCollection<string>(differences.ToList());
        return new GoldenComparisonResult(matched: false, differences: list, summary: summary);
    }

    public string Format(int maxDifferences = 20)
    {
        if (Matched)
        {
            return string.Empty;
        }

        var lines = new List<string> { Summary ?? "Golden mismatch." };
        var shown = 0;
        foreach (var difference in Differences)
        {
            if (shown >= maxDifferences)
            {
                lines.Add($"  ... and {Differences.Count - shown} more difference(s).");
                break;
            }

            lines.Add("  - " + difference);
            shown++;
        }

        return string.Join(Environment.NewLine, lines);
    }
}
