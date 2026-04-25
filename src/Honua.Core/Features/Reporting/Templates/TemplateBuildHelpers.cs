// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Reporting.Domain;

namespace Honua.Core.Features.Reporting.Templates;

/// <summary>
/// Shared section builders used by every analysis report template. Keeps the
/// per-template files focused on the family-specific parts (key metrics, charts,
/// narrative slot wording) and avoids duplicating the boilerplate
/// summary/artifacts/provenance shape.
/// </summary>
internal static class TemplateBuildHelpers
{
    /// <summary>
    /// Builds the title heading + summary description paragraph that every
    /// report leads with.
    /// </summary>
    public static IEnumerable<AnalysisReportSection> BuildHeader(AnalysisResultPackage package)
    {
        yield return new HeadingSection { Text = package.Summary.Title, Level = 1 };
        if (!string.IsNullOrWhiteSpace(package.Summary.Description))
        {
            yield return new ParagraphSection { Text = package.Summary.Description };
        }
    }

    /// <summary>
    /// Builds the artifacts table that lists every <see cref="ArtifactRef"/>
    /// from the source package. Returns an empty enumerable when no artifacts
    /// are present so renderers do not emit an empty table.
    /// </summary>
    public static IEnumerable<AnalysisReportSection> BuildArtifactsSection(
        AnalysisResultPackage package,
        int maxRows)
    {
        if (package.Artifacts.Count == 0)
        {
            yield break;
        }

        yield return new HeadingSection { Text = "Artifacts", Level = 2 };

        var rows = new List<IReadOnlyList<string>>(Math.Min(package.Artifacts.Count, maxRows));
        var truncated = 0;
        foreach (var artifact in package.Artifacts)
        {
            if (rows.Count >= maxRows)
            {
                truncated++;
                continue;
            }

            rows.Add(new[]
            {
                artifact.ArtifactId,
                artifact.Kind.ToString(),
                artifact.Label,
                artifact.Uri ?? "-",
                artifact.ContentType ?? "-"
            });
        }

        yield return new TableSection
        {
            Caption = "Outputs produced by the workflow.",
            Columns = new[] { "Artifact ID", "Kind", "Label", "URI", "Content Type" },
            Rows = rows,
            TruncatedRowCount = truncated
        };
    }

    /// <summary>
    /// Builds the assumptions section. Each assumption renders as a row in a
    /// single-column table so renderers stay consistent across formats.
    /// </summary>
    public static IEnumerable<AnalysisReportSection> BuildAssumptionsSection(
        AnalysisResultPackage package,
        int maxRows)
    {
        if (package.Assumptions.Count == 0 && package.Provenance.Assumptions.Count == 0)
        {
            yield break;
        }

        yield return new HeadingSection { Text = "Assumptions", Level = 2 };

        var combined = new List<string>(package.Assumptions.Count + package.Provenance.Assumptions.Count);
        combined.AddRange(package.Assumptions);
        foreach (var item in package.Provenance.Assumptions)
        {
            if (!combined.Contains(item, StringComparer.Ordinal))
            {
                combined.Add(item);
            }
        }

        var rows = new List<IReadOnlyList<string>>(Math.Min(combined.Count, maxRows));
        var truncated = 0;
        foreach (var assumption in combined)
        {
            if (rows.Count >= maxRows)
            {
                truncated++;
                continue;
            }

            rows.Add(new[] { assumption });
        }

        yield return new TableSection
        {
            Columns = new[] { "Assumption" },
            Rows = rows,
            TruncatedRowCount = truncated
        };
    }

    /// <summary>
    /// Builds the provenance footer that closes every report.
    /// </summary>
    public static AnalysisReportSection BuildProvenanceFooter(
        string jobId,
        AnalysisResultPackage package,
        DateTimeOffset generatedAt)
        => new ProvenanceFooterSection
        {
            JobId = jobId,
            ResultPackageId = package.ResultPackageId,
            ProcessDefinitions = package.Provenance.ProcessDefinitions,
            Sources = package.Provenance.Sources.Select(s => s.SourceId).ToArray(),
            ExecutedAt = package.Provenance.ExecutedAt,
            GeneratedAt = generatedAt
        };

    /// <summary>
    /// Returns the process family slice (segment before the first <c>.</c>).
    /// Falls back to <c>generic</c> for unparseable identifiers.
    /// </summary>
    public static string ResolveProcessFamily(string processId)
    {
        if (string.IsNullOrEmpty(processId))
        {
            return "generic";
        }

        var dotIndex = processId.IndexOf('.', StringComparison.Ordinal);
        return dotIndex <= 0 ? processId : processId[..dotIndex];
    }

    /// <summary>
    /// Picks the first artifact metadata value for the supplied key, in
    /// declaration order. Returns <c>null</c> when no artifact carries the key.
    /// </summary>
    public static string? FirstArtifactMetadata(AnalysisResultPackage package, string key)
    {
        foreach (var artifact in package.Artifacts)
        {
            if (artifact.Metadata.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Formats a numeric metadata value invariantly, returning <c>null</c> when
    /// the key is missing or unparseable.
    /// </summary>
    public static double? TryGetMetadataDouble(AnalysisResultPackage package, string key)
    {
        var raw = FirstArtifactMetadata(package, key);
        if (raw is null)
        {
            return null;
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Formats <paramref name="value"/> with invariant culture and at most
    /// <paramref name="maxFractionDigits"/> fraction digits. Trailing zeros are
    /// trimmed for readability.
    /// </summary>
    public static string FormatNumber(double value, int maxFractionDigits = 3)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        var format = "F" + maxFractionDigits.ToString(CultureInfo.InvariantCulture);
        var rendered = value.ToString(format, CultureInfo.InvariantCulture);
        if (!rendered.Contains('.', StringComparison.Ordinal))
        {
            return rendered;
        }

        return rendered.TrimEnd('0').TrimEnd('.');
    }
}
