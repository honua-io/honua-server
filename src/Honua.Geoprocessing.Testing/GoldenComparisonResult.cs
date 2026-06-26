// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.ObjectModel;

namespace Honua.Geoprocessing.Testing;

/// <summary>
/// The outcome of comparing a process's published artifact against its golden file
/// (GP Devkit P6, issue #2127). On a mismatch it carries an ordered list of
/// human-readable <see cref="Differences"/> — what differed and where — so a failing
/// golden test points straight at the offending coordinate/value rather than dumping
/// two opaque blobs.
/// </summary>
public sealed class GoldenComparisonResult
{
    private GoldenComparisonResult(bool matched, IReadOnlyList<string> differences, string? summary)
    {
        Matched = matched;
        Differences = differences;
        Summary = summary;
    }

    /// <summary>Whether the artifact matched the golden within tolerance.</summary>
    public bool Matched { get; }

    /// <summary>
    /// Ordered, human-readable difference lines (empty when <see cref="Matched"/> is true).
    /// Each entry names the location (e.g. feature index + coordinate, or JSON path) and the
    /// actual-vs-golden values that diverged beyond tolerance.
    /// </summary>
    public IReadOnlyList<string> Differences { get; }

    /// <summary>
    /// A one-line summary of the comparison (the comparator used and headline reason on
    /// failure), or <c>null</c> for a bare match.
    /// </summary>
    public string? Summary { get; }

    /// <summary>A successful comparison.</summary>
    public static GoldenComparisonResult Match { get; } =
        new(matched: true, differences: Array.Empty<string>(), summary: null);

    /// <summary>
    /// Builds a failed comparison from a summary and the collected difference lines.
    /// </summary>
    /// <param name="summary">Headline reason for the mismatch.</param>
    /// <param name="differences">The ordered, located difference lines.</param>
    /// <returns>A failed <see cref="GoldenComparisonResult"/>.</returns>
    public static GoldenComparisonResult Mismatch(string summary, IEnumerable<string> differences)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        ArgumentNullException.ThrowIfNull(differences);
        var list = new ReadOnlyCollection<string>(differences.ToList());
        return new GoldenComparisonResult(matched: false, differences: list, summary: summary);
    }

    /// <summary>
    /// Renders the mismatch as a multi-line block suitable for an assertion message:
    /// the summary followed by each located difference (capped so a wholesale mismatch
    /// does not flood the test output).
    /// </summary>
    /// <param name="maxDifferences">Maximum difference lines to render before truncating.</param>
    /// <returns>The formatted diff, or an empty string when matched.</returns>
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
