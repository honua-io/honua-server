// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Reporting.Domain;
using Honua.Core.Features.Reporting.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Reporting;

/// <summary>
/// Verifies renderer output shape: contract refusal for unsupported versions,
/// HTML offline guarantees, and Markdown/HTML coverage for every section kind.
/// </summary>
[Protocol(ProtocolNames.TestQuality)]
public sealed class ReportRendererTests
{
    [UnitTest]
    [Operation(Operations.Metadata)]
    public void MarkdownRenderer_RefusesUnsupportedContractVersion()
    {
        var renderer = new MarkdownReportRenderer();
        var report = BuildReport() with { ReportContractVersion = "honua.report.v99" };

        var act = () => renderer.Render(report);

        act.Should().Throw<UnsupportedReportContractVersionException>();
        UnsupportedReportContractVersionException.Code.Should().Be(ReportingConstants.UnsupportedContractVersionErrorCode);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void HtmlRenderer_RefusesUnsupportedContractVersion()
    {
        var renderer = new HtmlReportRenderer();
        var report = BuildReport() with { ReportContractVersion = "honua.report.v99" };

        var act = () => renderer.Render(report);

        act.Should().Throw<UnsupportedReportContractVersionException>();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void MarkdownRenderer_EmitsHeadingsTablesAndNarrative()
    {
        var renderer = new MarkdownReportRenderer();
        var report = BuildReport();

        var body = renderer.Render(report);

        body.Should().Contain("# Buffered places");
        body.Should().Contain("## Buffer Parameters");
        body.Should().Contain("| Artifact ID | Kind | Label | URI | Content Type |");
        body.Should().Contain("This run applied a buffer of 500 meters");
        body.Should().Contain("---");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void HtmlRenderer_ContainsNoExternalReferences()
    {
        var renderer = new HtmlReportRenderer();
        var report = BuildReport();

        var body = renderer.Render(report);

        body.Should().StartWith("<!doctype html>");
        body.Should().Contain("<style>");
        body.Should().NotContain("<script src=", "client-side scripts are forbidden for offline guarantees");
        body.Should().NotContain("<link rel=\"stylesheet\" href=", "external stylesheets are forbidden");
        body.Should().NotContain("http://");
        body.Should().NotContain("https://");
        body.Should().Contain("honua-report");
        body.Should().Contain("data-report-contract=\"honua.report.v1\"");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void HtmlRenderer_EscapesUserContent()
    {
        var renderer = new HtmlReportRenderer();
        var report = BuildReport() with
        {
            Summary = new ResultSummary
            {
                Title = "<script>alert(1)</script>",
                Description = "Contains & special chars"
            },
            Sections = new List<AnalysisReportSection>
            {
                new HeadingSection { Text = "<img src=x onerror=alert(1)>", Level = 2 },
                new ParagraphSection { Text = "Some & text" },
                new ProvenanceFooterSection
                {
                    JobId = "job-1",
                    ResultPackageId = "pkg-1",
                    ProcessDefinitions = Array.Empty<string>(),
                    Sources = Array.Empty<string>(),
                    GeneratedAt = DateTimeOffset.UnixEpoch
                }
            }
        };

        var body = renderer.Render(report);

        body.Should().NotContain("<script>alert(1)</script>");
        body.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt;");
        body.Should().NotContain("<img src=x onerror=alert(1)>",
            "dangerous markup must be HTML-escaped before reaching the output");
        body.Should().Contain("&lt;img src=x onerror=alert(1)&gt;");
        body.Should().Contain("Some &amp; text");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void HtmlRenderer_RendersInlineSvgForChart()
    {
        var renderer = new HtmlReportRenderer();
        var report = BuildReport() with
        {
            Sections = new List<AnalysisReportSection>
            {
                new ChartSection
                {
                    Caption = "Top bins",
                    ChartKind = ReportChartKind.Bar,
                    Categories = new[] { "A", "B", "C" },
                    Series = new[]
                    {
                        new ChartSeries
                        {
                            Name = "Value",
                            Values = new[] { 1.0, 2.0, 3.0 }
                        }
                    }
                },
                new ProvenanceFooterSection
                {
                    JobId = "job-1",
                    ResultPackageId = "pkg-1",
                    ProcessDefinitions = Array.Empty<string>(),
                    Sources = Array.Empty<string>(),
                    GeneratedAt = DateTimeOffset.UnixEpoch
                }
            }
        };

        var body = renderer.Render(report);

        body.Should().Contain("<svg class=\"report-svg\"");
        body.Should().Contain("<rect class=\"bar\"");
        body.Should().NotContain("http://");
    }

    private static AnalysisReport BuildReport()
    {
        var artifact = new ArtifactRef
        {
            ArtifactId = "artifact-1",
            Kind = ArtifactKind.FeatureLayer,
            Label = "Buffered places",
            Uri = "honua://artifacts/artifact-1",
            Metadata = new Dictionary<string, string>
            {
                ["distance"] = "500",
                ["unit"] = "meters",
                ["bufferedFeatureCount"] = "42"
            }
        };

        return new AnalysisReport
        {
            ReportId = "report-1",
            ReportContractVersion = ReportingConstants.ContractVersionV1,
            JobId = "job-1",
            ResultPackageId = "pkg-1",
            ProcessId = "analytics.buffer-aggregate",
            ProcessFamily = "analytics",
            TemplateId = "analysis-report.analytics-buffer-aggregate",
            TemplateVersion = "1.0.0",
            Summary = new ResultSummary
            {
                Title = "Buffered places",
                Description = "500m buffers."
            },
            Sections = new List<AnalysisReportSection>
            {
                new HeadingSection { Text = "Buffered places", Level = 1 },
                new ParagraphSection { Text = "500m buffers." },
                new HeadingSection { Text = "Buffer Parameters", Level = 2 },
                new KeyMetricSection { Label = "Buffer distance", Value = "500", Unit = "meters" },
                new HeadingSection { Text = "Artifacts", Level = 2 },
                new TableSection
                {
                    Columns = new[] { "Artifact ID", "Kind", "Label", "URI", "Content Type" },
                    Rows = new List<IReadOnlyList<string>>
                    {
                        new[] { "artifact-1", "FeatureLayer", "Buffered places", "honua://artifacts/artifact-1", "-" }
                    }
                },
                new NarrativeSection
                {
                    SlotId = "summary",
                    DeterministicText = "This run applied a buffer of 500 meters, buffered 42 feature(s) without dissolution.",
                    Mode = NarrativeMode.Deterministic
                },
                new ProvenanceFooterSection
                {
                    JobId = "job-1",
                    ResultPackageId = "pkg-1",
                    ProcessDefinitions = new[] { "analytics.buffer-aggregate" },
                    Sources = new[] { "places" },
                    ExecutedAt = DateTimeOffset.Parse("2026-04-24T09:55:00Z", CultureInfo.InvariantCulture),
                    GeneratedAt = DateTimeOffset.Parse("2026-04-24T10:00:00Z", CultureInfo.InvariantCulture)
                }
            },
            NarrativeMode = NarrativeMode.Deterministic,
            Provenance = new ProvenanceRecord
            {
                Sources = Array.Empty<ProvenanceSource>(),
                ProcessDefinitions = new[] { "analytics.buffer-aggregate" }
            },
            GeneratedAt = DateTimeOffset.Parse("2026-04-24T10:00:00Z", CultureInfo.InvariantCulture)
        };
    }
}
