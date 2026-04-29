// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Reporting.Domain;
using Honua.Core.Features.Reporting.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Eval;

namespace Honua.Server.Tests.Features.Reporting;

/// <summary>
/// Covers the "#734 eval harness consumes this pipeline" acceptance criterion.
/// Uses the same scenario the end-to-end harness loads
/// (<c>analysis-buffer-places</c>) and asserts that a result package matching
/// its expected artifact shape round-trips through the reporting pipeline.
/// The existing eval runner explicitly skips <c>GetJobResult</c> pending the
/// execution engine; this test is the thin-slice replacement that gates the
/// reporting pipeline against the scenario contract until that stage is wired.
/// </summary>
[Protocol(TestProtocols.OperatorEval)]
public sealed class AnalysisReportEvalPipelineTests
{
    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task AnalysisBufferPlacesScenario_ProducesReportArtifactFromResultPackage()
    {
        var scenario = EvalScenarioLoader.LoadById("analysis-buffer-places");
        scenario.Intent.RequestedOutputs.Should().Contain(ArtifactKind.Report,
            "the scenario declares that a Report output is expected.");
        scenario.PrecompiledPlan.Outputs.Should().Contain(ArtifactKind.Report);

        var package = SynthesizeResultPackageFromScenario(scenario);
        var builder = ReportingFixtures.CreateBuilder();

        var report = await builder.BuildAsync(
            jobId: "eval-analysis-buffer-places",
            package,
            CancellationToken.None);

        report.ReportContractVersion.Should().Be(ReportingConstants.ContractVersionV1);
        report.ProcessId.Should().Be("analytics.buffer-aggregate");
        report.TemplateId.Should().Be("analysis-report.analytics-buffer-aggregate");
        report.Sections.Should().NotBeEmpty();

        var markdown = new MarkdownReportRenderer().Render(report);
        markdown.Should().Contain("# Buffered places");
        markdown.Should().Contain("## Buffer Parameters");
    }

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task AnalysisBufferPlacesScenario_MarkdownRendersOfflineSafeHtml()
    {
        var scenario = EvalScenarioLoader.LoadById("analysis-buffer-places");
        var package = SynthesizeResultPackageFromScenario(scenario);
        var builder = ReportingFixtures.CreateBuilder();

        var report = await builder.BuildAsync(
            jobId: "eval-analysis-buffer-places",
            package,
            CancellationToken.None);

        var html = new HtmlReportRenderer().Render(report);
        html.Should().NotContain("http://");
        html.Should().NotContain("https://");
        html.Should().NotContain("<script src=");
        html.Should().NotContain("<link rel=\"stylesheet\" href=");
    }

    private static AnalysisResultPackage SynthesizeResultPackageFromScenario(EvalScenario scenario)
    {
        var bufferStep = scenario.PrecompiledPlan.Steps.First(s =>
            string.Equals(s.ProcessId, "analytics.buffer-aggregate", StringComparison.Ordinal));

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (bufferStep.Inputs.TryGetValue("distance", out var distance))
        {
            metadata["distance"] = distance;
        }
        if (bufferStep.Inputs.TryGetValue("unit", out var unit))
        {
            metadata["unit"] = unit;
        }

        var artifact = new ArtifactRef
        {
            ArtifactId = "eval-buffered-layer",
            Kind = ArtifactKind.FeatureLayer,
            Label = "Buffered places",
            Metadata = metadata
        };

        var reportArtifact = new ArtifactRef
        {
            ArtifactId = "eval-report-artifact",
            Kind = ArtifactKind.Report,
            Label = "Buffer report"
        };

        return AnalysisResultPackage.CreateCompleted(
            resultPackageId: "eval-pkg-analysis-buffer-places",
            summary: new ResultSummary
            {
                Title = "Buffered places",
                Description = scenario.Intent.Goal
            },
            artifacts: [artifact, reportArtifact],
            workspaceRefs: [],
            provenance: new ProvenanceRecord
            {
                Sources = scenario.Intent.Inputs
                    .Select(input => new ProvenanceSource { SourceId = input })
                    .ToList(),
                ProcessDefinitions = ["analytics.buffer-aggregate"]
            });
    }
}
