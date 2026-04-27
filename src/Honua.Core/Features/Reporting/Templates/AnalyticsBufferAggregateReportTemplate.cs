// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Reporting.Domain;

namespace Honua.Core.Features.Reporting.Templates;

/// <summary>
/// Template for <c>analytics.buffer-aggregate</c> result packages. Surfaces
/// the buffer distance and unit when the artifact metadata exposes them and
/// composes a narrative slot keyed off the buffered feature counts.
/// </summary>
internal sealed class AnalyticsBufferAggregateReportTemplate : IAnalysisReportTemplate
{
    private const string ProcessIdentifier = "analytics.buffer-aggregate";

    /// <inheritdoc />
    public string ProcessId => ProcessIdentifier;

    /// <inheritdoc />
    public string TemplateId => "analysis-report.analytics-buffer-aggregate";

    /// <inheritdoc />
    public string TemplateVersion => "1.0.0";

    /// <inheritdoc />
    public AnalysisReportDraft Build(AnalysisResultPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var sections = new List<AnalysisReportSection>();
        sections.AddRange(TemplateBuildHelpers.BuildHeader(package));

        var distance = TemplateBuildHelpers.TryGetMetadataDouble(package, "distance");
        var unit = TemplateBuildHelpers.FirstArtifactMetadata(package, "unit") ?? "meters";
        var bufferedCount = TemplateBuildHelpers.TryGetMetadataDouble(package, "bufferedFeatureCount");
        var dissolvedCount = TemplateBuildHelpers.TryGetMetadataDouble(package, "dissolvedFeatureCount");
        var totalArea = TemplateBuildHelpers.TryGetMetadataDouble(package, "totalAreaSquareMeters");

        sections.Add(new HeadingSection { Text = "Buffer Parameters", Level = 2 });

        if (distance is { } d)
        {
            sections.Add(new KeyMetricSection
            {
                Label = "Buffer distance",
                Value = TemplateBuildHelpers.FormatNumber(d),
                Unit = unit
            });
        }
        else
        {
            sections.Add(new ParagraphSection
            {
                Text = $"Buffer distance was not recorded on the result package (unit declared as {unit})."
            });
        }

        if (bufferedCount is { } bc)
        {
            sections.Add(new KeyMetricSection
            {
                Label = "Buffered features",
                Value = bc.ToString("N0", CultureInfo.InvariantCulture)
            });
        }

        if (dissolvedCount is { } dc)
        {
            sections.Add(new KeyMetricSection
            {
                Label = "Dissolved groups",
                Value = dc.ToString("N0", CultureInfo.InvariantCulture)
            });
        }

        if (totalArea is { } area)
        {
            sections.Add(new KeyMetricSection
            {
                Label = "Total buffered area",
                Value = TemplateBuildHelpers.FormatNumber(area),
                Unit = "m²"
            });
        }

        sections.AddRange(TemplateBuildHelpers.BuildArtifactsSection(package, int.MaxValue));
        sections.AddRange(TemplateBuildHelpers.BuildAssumptionsSection(package, int.MaxValue));

        var narrative = new NarrativeSlot
        {
            SlotId = "summary",
            Heading = "Summary",
            DeterministicText = BuildDeterministicSummary(distance, unit, bufferedCount, dissolvedCount, totalArea),
            LlmHint = "Summarize the buffer distance, count of features buffered, and how the dissolved geometry can be used downstream."
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

    private static string BuildDeterministicSummary(
        double? distance,
        string unit,
        double? bufferedCount,
        double? dissolvedCount,
        double? totalArea)
    {
        var distancePhrase = distance is { } d
            ? $"a buffer of {TemplateBuildHelpers.FormatNumber(d)} {unit}"
            : $"a buffer (distance unrecorded; unit {unit})";

        var bufferedPhrase = bufferedCount is { } bc
            ? $"buffered {bc.ToString("N0", CultureInfo.InvariantCulture)} feature(s)"
            : "buffered the input features";

        var dissolvedPhrase = dissolvedCount is { } dc
            ? $"and dissolved them into {dc.ToString("N0", CultureInfo.InvariantCulture)} group(s)"
            : "without dissolution";

        var areaPhrase = totalArea is { } area
            ? $"covering {TemplateBuildHelpers.FormatNumber(area)} m²"
            : null;

        var sentence = $"This run applied {distancePhrase}, {bufferedPhrase} {dissolvedPhrase}";
        return areaPhrase is null ? sentence + "." : sentence + " " + areaPhrase + ".";
    }
}
