// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Reporting.Domain;

namespace Honua.Core.Features.Reporting.Templates;

/// <summary>
/// Template for <c>surface.slope</c> result packages. Surfaces the slope units
/// and z-factor and reports min/mean/max slope when the artifact metadata
/// exposes them.
/// </summary>
internal sealed class SurfaceSlopeReportTemplate : IAnalysisReportTemplate
{
    private const int MaxRows = 200;
    private const string ProcessIdentifier = "surface.slope";

    /// <inheritdoc />
    public string ProcessId => ProcessIdentifier;

    /// <inheritdoc />
    public string TemplateId => "analysis-report.surface-slope";

    /// <inheritdoc />
    public string TemplateVersion => "1.0.0";

    /// <inheritdoc />
    public AnalysisReportDraft Build(AnalysisResultPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var sections = new List<AnalysisReportSection>();
        sections.AddRange(TemplateBuildHelpers.BuildHeader(package));

        var units = TemplateBuildHelpers.FirstArtifactMetadata(package, "units") ?? "degrees";
        var zFactor = TemplateBuildHelpers.TryGetMetadataDouble(package, "zFactor");
        var minSlope = TemplateBuildHelpers.TryGetMetadataDouble(package, "minSlope");
        var meanSlope = TemplateBuildHelpers.TryGetMetadataDouble(package, "meanSlope");
        var maxSlope = TemplateBuildHelpers.TryGetMetadataDouble(package, "maxSlope");
        var spatialReference = TemplateBuildHelpers.FirstArtifactMetadata(package, "spatialReference");

        sections.Add(new HeadingSection { Text = "Slope Parameters", Level = 2 });
        sections.Add(new KeyMetricSection
        {
            Label = "Units",
            Value = units
        });
        if (zFactor is { } z)
        {
            sections.Add(new KeyMetricSection
            {
                Label = "Z-factor",
                Value = TemplateBuildHelpers.FormatNumber(z)
            });
        }
        if (spatialReference is not null)
        {
            sections.Add(new KeyMetricSection
            {
                Label = "Spatial reference",
                Value = spatialReference
            });
        }

        if (minSlope is not null || meanSlope is not null || maxSlope is not null)
        {
            sections.Add(new HeadingSection { Text = "Slope Statistics", Level = 2 });
            if (minSlope is { } min)
            {
                sections.Add(new KeyMetricSection { Label = "Minimum slope", Value = TemplateBuildHelpers.FormatNumber(min), Unit = units });
            }
            if (meanSlope is { } mean)
            {
                sections.Add(new KeyMetricSection { Label = "Mean slope", Value = TemplateBuildHelpers.FormatNumber(mean), Unit = units });
            }
            if (maxSlope is { } max)
            {
                sections.Add(new KeyMetricSection { Label = "Maximum slope", Value = TemplateBuildHelpers.FormatNumber(max), Unit = units });
            }
        }

        sections.AddRange(TemplateBuildHelpers.BuildArtifactsSection(package, MaxRows));
        sections.AddRange(TemplateBuildHelpers.BuildAssumptionsSection(package, MaxRows));

        var narrative = new NarrativeSlot
        {
            SlotId = "summary",
            Heading = "Summary",
            DeterministicText = BuildDeterministicSummary(units, zFactor, meanSlope, maxSlope),
            LlmHint = "Describe the slope analysis: units, z-factor, and what the mean / max slope values imply for the surface."
        };

        return new AnalysisReportDraft
        {
            TemplateId = TemplateId,
            TemplateVersion = TemplateVersion,
            ProcessId = ProcessIdentifier,
            ProcessFamily = "surface",
            Sections = sections,
            NarrativeSlots = new[] { narrative },
            SourcePackage = package
        };
    }

    private static string BuildDeterministicSummary(string units, double? zFactor, double? meanSlope, double? maxSlope)
    {
        var zPhrase = zFactor is { } z ? $" using z-factor {TemplateBuildHelpers.FormatNumber(z)}" : string.Empty;
        var statsPhrase = (meanSlope, maxSlope) switch
        {
            ({ } mean, { } max) => $" Mean slope is {TemplateBuildHelpers.FormatNumber(mean)} {units}; the maximum is {TemplateBuildHelpers.FormatNumber(max)} {units}.",
            ({ } mean, _) => $" Mean slope is {TemplateBuildHelpers.FormatNumber(mean)} {units}.",
            (_, { } max) => $" Maximum slope is {TemplateBuildHelpers.FormatNumber(max)} {units}.",
            _ => string.Empty
        };

        return $"Slope was computed in {units}{zPhrase}.{statsPhrase}";
    }
}
