// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Reporting.Domain;

namespace Honua.Core.Features.Reporting.Templates;

internal static class TemplateBuildHelpers
{
    public static IEnumerable<AnalysisReportSection> BuildHeader(AnalysisResultPackage package)
    {
        yield return new HeadingSection { Text = package.Summary.Title, Level = 1 };
        if (!string.IsNullOrWhiteSpace(package.Summary.Description))
        {
            yield return new ParagraphSection { Text = package.Summary.Description };
        }
    }

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
        // Not a simple filter: the Contains check reads `combined` as it grows, so a
        // `.Where(...)` filter evaluated against the list being appended to would be
        // fragile/unclear here; the explicit loop makes the incremental dedupe obvious.
        foreach (var item in (package.Provenance.Assumptions).Where(item => !combined.Contains(item, StringComparer.Ordinal)))
        {
            combined.Add(item);
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

    public static string ResolveProcessFamily(string processId)
    {
        if (string.IsNullOrEmpty(processId))
        {
            return "generic";
        }

        var dotIndex = processId.IndexOf('.', StringComparison.Ordinal);
        return dotIndex <= 0 ? processId : processId[..dotIndex];
    }

    public static string? FirstArtifactMetadata(AnalysisResultPackage package, string key)
    {
        var artifactIndex = 0;
        while (artifactIndex < package.Artifacts.Count)
        {
            if (package.Artifacts[artifactIndex].Metadata.TryGetValue(key, out var value) &&
                !string.IsNullOrEmpty(value))
            {
                return value;
            }

            artifactIndex++;
        }

        return null;
    }

    public static double? TryGetMetadataDouble(AnalysisResultPackage package, string key)
    {
        var raw = FirstArtifactMetadata(package, key);
        if (raw is null)
        {
            return null;
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

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
