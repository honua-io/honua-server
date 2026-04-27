// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Reporting.Domain;

namespace Honua.Core.Features.Reporting.Templates;

/// <summary>
/// Fallback template used when no process-specific template is registered.
/// Renders the canonical summary, artifacts, assumptions, and provenance
/// blocks plus a single deterministic narrative slot derived from the result
/// summary.
/// </summary>
internal sealed class GenericAnalysisReportTemplate : IAnalysisReportTemplate
{
    private static readonly string[] _errorTableColumns = ["Kind", "Message"];

    /// <inheritdoc />
    public string ProcessId => "*";

    /// <inheritdoc />
    public string TemplateId => ReportingConstants.GenericTemplateId;

    /// <inheritdoc />
    public string TemplateVersion => "1.0.0";

    /// <inheritdoc />
    public AnalysisReportDraft Build(AnalysisResultPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var processId = package.Provenance.ProcessDefinitions.Count > 0
            ? package.Provenance.ProcessDefinitions[0]
            : "unknown";
        var family = TemplateBuildHelpers.ResolveProcessFamily(processId);

        var sections = new List<AnalysisReportSection>();
        sections.AddRange(TemplateBuildHelpers.BuildHeader(package));

        if (package.Status == GeoprocessingWorkflowStatus.Failed && package.Errors.Count > 0)
        {
            sections.Add(new HeadingSection { Text = "Errors", Level = 2 });
            var rows = package.Errors
                .Select(e => (IReadOnlyList<string>)new[] { e.Kind.ToString(), e.Message })
                .ToList();
            sections.Add(new TableSection
            {
                Columns = _errorTableColumns,
                Rows = rows
            });
        }

        sections.AddRange(TemplateBuildHelpers.BuildArtifactsSection(package, int.MaxValue));
        sections.AddRange(TemplateBuildHelpers.BuildAssumptionsSection(package, int.MaxValue));

        var narrative = new NarrativeSlot
        {
            SlotId = "summary",
            Heading = "Summary",
            DeterministicText = BuildDeterministicSummary(package, processId),
            LlmHint = "One short paragraph that summarizes what this analysis produced and how to read the artifacts."
        };

        return new AnalysisReportDraft
        {
            TemplateId = TemplateId,
            TemplateVersion = TemplateVersion,
            ProcessId = processId,
            ProcessFamily = family,
            Sections = sections,
            NarrativeSlots = new[] { narrative },
            SourcePackage = package
        };
    }

    private static string BuildDeterministicSummary(AnalysisResultPackage package, string processId)
    {
        var artifactCount = package.Artifacts.Count;
        var summaryDescription = string.IsNullOrWhiteSpace(package.Summary.Description)
            ? package.Summary.Title
            : package.Summary.Description;

        var artifactPhrase = artifactCount switch
        {
            0 => "no output artifacts",
            1 => "1 output artifact",
            _ => $"{artifactCount} output artifacts"
        };

        return $"Process '{processId}' completed with status {package.Status} and produced {artifactPhrase}. {summaryDescription}".Trim();
    }
}
