// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Reporting.Domain;

namespace Honua.Core.Features.Reporting.Templates;

/// <summary>
/// Template for <c>generalization.dissolve</c> result packages. Reports the
/// before/after feature counts and lists the group-by fields the operation
/// applied.
/// </summary>
internal sealed class GeneralizationDissolveReportTemplate : IAnalysisReportTemplate
{
    private const int MaxRows = 200;
    private const string ProcessIdentifier = "generalization.dissolve";

    /// <inheritdoc />
    public string ProcessId => ProcessIdentifier;

    /// <inheritdoc />
    public string TemplateId => "analysis-report.generalization-dissolve";

    /// <inheritdoc />
    public string TemplateVersion => "1.0.0";

    /// <inheritdoc />
    public AnalysisReportDraft Build(AnalysisResultPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var sections = new List<AnalysisReportSection>();
        sections.AddRange(TemplateBuildHelpers.BuildHeader(package));

        var inputCount = TemplateBuildHelpers.TryGetMetadataDouble(package, "inputFeatureCount");
        var outputCount = TemplateBuildHelpers.TryGetMetadataDouble(package, "outputFeatureCount");
        var groupByFields = TemplateBuildHelpers.FirstArtifactMetadata(package, "groupByFields");
        var dissolved = TemplateBuildHelpers.FirstArtifactMetadata(package, "dissolve") ?? "true";

        sections.Add(new HeadingSection { Text = "Dissolve Parameters", Level = 2 });
        sections.Add(new KeyMetricSection
        {
            Label = "Geometry union",
            Value = bool.TryParse(dissolved, out var dissolvedFlag) && dissolvedFlag ? "Enabled" : "Disabled"
        });

        if (!string.IsNullOrEmpty(groupByFields))
        {
            sections.Add(new KeyMetricSection
            {
                Label = "Group-by fields",
                Value = groupByFields
            });
        }

        if (inputCount is not null || outputCount is not null)
        {
            sections.Add(new HeadingSection { Text = "Feature Counts", Level = 2 });
            if (inputCount is { } ic)
            {
                sections.Add(new KeyMetricSection { Label = "Input features", Value = ic.ToString("N0", CultureInfo.InvariantCulture) });
            }
            if (outputCount is { } oc)
            {
                sections.Add(new KeyMetricSection { Label = "Output features", Value = oc.ToString("N0", CultureInfo.InvariantCulture) });
            }
        }

        sections.AddRange(TemplateBuildHelpers.BuildArtifactsSection(package, MaxRows));
        sections.AddRange(TemplateBuildHelpers.BuildAssumptionsSection(package, MaxRows));

        var narrative = new NarrativeSlot
        {
            SlotId = "summary",
            Heading = "Summary",
            DeterministicText = BuildDeterministicSummary(inputCount, outputCount, groupByFields),
            LlmHint = "Summarize the dissolve operation, the grouping fields, and the reduction in feature count."
        };

        return new AnalysisReportDraft
        {
            TemplateId = TemplateId,
            TemplateVersion = TemplateVersion,
            ProcessId = ProcessIdentifier,
            ProcessFamily = "generalization",
            Sections = sections,
            NarrativeSlots = new[] { narrative },
            SourcePackage = package
        };
    }

    private static string BuildDeterministicSummary(double? inputCount, double? outputCount, string? groupByFields)
    {
        var groupPhrase = string.IsNullOrEmpty(groupByFields)
            ? "without explicit grouping"
            : $"grouped by {groupByFields}";

        return (inputCount, outputCount) switch
        {
            ({ } ic, { } oc) => $"Dissolve {groupPhrase} reduced {ic.ToString("N0", CultureInfo.InvariantCulture)} input feature(s) to {oc.ToString("N0", CultureInfo.InvariantCulture)} output feature(s).",
            (null, { } oc) => $"Dissolve {groupPhrase} produced {oc.ToString("N0", CultureInfo.InvariantCulture)} output feature(s).",
            ({ } ic, null) => $"Dissolve {groupPhrase} consumed {ic.ToString("N0", CultureInfo.InvariantCulture)} input feature(s); the output count was not recorded.",
            _ => $"Dissolve completed {groupPhrase}; feature counts were not recorded on the result package."
        };
    }
}
