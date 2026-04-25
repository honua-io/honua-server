// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Reporting.Domain;

namespace Honua.Core.Features.Reporting.Templates;

/// <summary>
/// Template for <c>analytics.density</c> result packages. Highlights cell size,
/// bin mode, and produces a small bar chart of the top-N density bins when the
/// artifact metadata exposes <c>topBin*Count</c> entries.
/// </summary>
internal sealed class AnalyticsDensityReportTemplate : IAnalysisReportTemplate
{
    private const int MaxRows = 200;
    private const int TopBinChartLimit = 10;
    private const string ProcessIdentifier = "analytics.density";

    /// <inheritdoc />
    public string ProcessId => ProcessIdentifier;

    /// <inheritdoc />
    public string TemplateId => "analysis-report.analytics-density";

    /// <inheritdoc />
    public string TemplateVersion => "1.0.0";

    /// <inheritdoc />
    public AnalysisReportDraft Build(AnalysisResultPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var sections = new List<AnalysisReportSection>();
        sections.AddRange(TemplateBuildHelpers.BuildHeader(package));

        var mode = TemplateBuildHelpers.FirstArtifactMetadata(package, "mode") ?? "hex";
        var cellSize = TemplateBuildHelpers.TryGetMetadataDouble(package, "cellSizeMeters")
            ?? TemplateBuildHelpers.TryGetMetadataDouble(package, "cellSize");
        var binCount = TemplateBuildHelpers.TryGetMetadataDouble(package, "binCount");
        var maxBinValue = TemplateBuildHelpers.TryGetMetadataDouble(package, "maxBinValue");
        var totalBinValue = TemplateBuildHelpers.TryGetMetadataDouble(package, "totalBinValue");

        sections.Add(new HeadingSection { Text = "Density Parameters", Level = 2 });
        sections.Add(new KeyMetricSection
        {
            Label = "Bin mode",
            Value = mode
        });

        if (cellSize is { } cs)
        {
            sections.Add(new KeyMetricSection
            {
                Label = "Cell size",
                Value = TemplateBuildHelpers.FormatNumber(cs),
                Unit = "m"
            });
        }

        if (binCount is { } bc)
        {
            sections.Add(new KeyMetricSection
            {
                Label = "Bin count",
                Value = bc.ToString("N0", CultureInfo.InvariantCulture)
            });
        }

        if (maxBinValue is { } mb)
        {
            sections.Add(new KeyMetricSection
            {
                Label = "Max bin value",
                Value = TemplateBuildHelpers.FormatNumber(mb)
            });
        }

        if (totalBinValue is { } total)
        {
            sections.Add(new KeyMetricSection
            {
                Label = "Total binned value",
                Value = TemplateBuildHelpers.FormatNumber(total)
            });
        }

        var topBinSeries = BuildTopBinSeries(package);
        if (topBinSeries.Categories.Count > 0)
        {
            sections.Add(new HeadingSection { Text = "Top Bins", Level = 2 });
            sections.Add(new ChartSection
            {
                Caption = $"Top {topBinSeries.Categories.Count} bins by value.",
                ChartKind = ReportChartKind.Bar,
                Categories = topBinSeries.Categories,
                Series = new[]
                {
                    new ChartSeries { Name = "Value", Values = topBinSeries.Values }
                },
                XAxisLabel = "Bin",
                YAxisLabel = "Value"
            });
        }

        sections.AddRange(TemplateBuildHelpers.BuildArtifactsSection(package, MaxRows));
        sections.AddRange(TemplateBuildHelpers.BuildAssumptionsSection(package, MaxRows));

        var narrative = new NarrativeSlot
        {
            SlotId = "summary",
            Heading = "Summary",
            DeterministicText = BuildDeterministicSummary(mode, cellSize, binCount, maxBinValue),
            LlmHint = "Describe the density binning result, the bin mode and cell size, and call out any concentration patterns the top bins reveal."
        };

        return new AnalysisReportDraft
        {
            TemplateId = TemplateId,
            TemplateVersion = TemplateVersion,
            ProcessId = ProcessIdentifier,
            ProcessFamily = "analytics",
            Sections = sections,
            NarrativeSlots = new[] { narrative },
            SourcePackage = package
        };
    }

    private static (IReadOnlyList<string> Categories, IReadOnlyList<double> Values) BuildTopBinSeries(
        AnalysisResultPackage package)
    {
        var categories = new List<string>(TopBinChartLimit);
        var values = new List<double>(TopBinChartLimit);

        for (var index = 0; index < TopBinChartLimit; index++)
        {
            var label = TemplateBuildHelpers.FirstArtifactMetadata(package, $"topBin{index}Label");
            var rawValue = TemplateBuildHelpers.TryGetMetadataDouble(package, $"topBin{index}Value");
            if (label is null || rawValue is null)
            {
                break;
            }

            categories.Add(label);
            values.Add(rawValue.Value);
        }

        return (categories, values);
    }

    private static string BuildDeterministicSummary(string mode, double? cellSize, double? binCount, double? maxBinValue)
    {
        var cellPhrase = cellSize is { } cs ? $"{TemplateBuildHelpers.FormatNumber(cs)} m" : "an unspecified size";
        var binPhrase = binCount is { } bc
            ? $"{bc.ToString("N0", CultureInfo.InvariantCulture)} bin(s)"
            : "an unspecified number of bins";
        var maxPhrase = maxBinValue is { } mb
            ? $" The peak bin holds {TemplateBuildHelpers.FormatNumber(mb)}."
            : string.Empty;

        return $"Density binning ran in {mode} mode with cell size {cellPhrase}, producing {binPhrase}.{maxPhrase}";
    }
}
