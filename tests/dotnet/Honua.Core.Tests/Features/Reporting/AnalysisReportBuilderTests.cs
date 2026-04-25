// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Reporting;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Reporting.Domain;
using Honua.Core.Features.Reporting.Services;
using Honua.Core.Features.Reporting.Templates;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Core.Tests.Features.Reporting;

/// <summary>
/// Verifies the canonical analysis-report builder behavior: contract versioning,
/// deterministic narrative composition, LLM enrichment, and clean fallback
/// when the LLM provider fails.
/// </summary>
[Protocol(Protocols.TestQuality)]
public sealed class AnalysisReportBuilderTests
{
    private static readonly DateTimeOffset _fixedInstant = DateTimeOffset.Parse("2026-04-24T10:00:00Z", CultureInfo.InvariantCulture);

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task BuildAsync_DeterministicOnly_StampsContractAndTemplateMetadata()
    {
        var builder = CreateBuilder(out _, narrativeEnabled: false, llmProvider: null);
        var package = BuildBufferPackage();

        var report = await builder.BuildAsync("job-123", package, CancellationToken.None);

        report.ReportContractVersion.Should().Be(ReportingConstants.ContractVersionV1);
        report.JobId.Should().Be("job-123");
        report.ResultPackageId.Should().Be("pkg-1");
        report.TemplateId.Should().Be("analysis-report.analytics-buffer-aggregate");
        report.TemplateVersion.Should().Be("1.0.0");
        report.NarrativeMode.Should().Be(NarrativeMode.Deterministic);
        report.ProcessFamily.Should().Be("analytics");
        report.Sections.Should().ContainItemsAssignableTo<AnalysisReportSection>();
        report.Sections.OfType<ProvenanceFooterSection>().Should().HaveCount(1);
        report.Sections.OfType<NarrativeSection>().Single().LlmText.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task BuildAsync_WhenLlmProviderSucceeds_UsesLlmText()
    {
        var llm = new StubLlmNarrativeProvider(new Dictionary<string, string>
        {
            ["summary"] = "LLM-authored summary paragraph."
        });
        var builder = CreateBuilder(out _, narrativeEnabled: true, llmProvider: llm);
        var package = BuildBufferPackage();

        var report = await builder.BuildAsync("job-123", package, CancellationToken.None);

        report.NarrativeMode.Should().Be(NarrativeMode.LlmAssisted);
        var narrative = report.Sections.OfType<NarrativeSection>().Single();
        narrative.LlmText.Should().Be("LLM-authored summary paragraph.");
        narrative.DeterministicText.Should().NotBeNullOrEmpty();
        narrative.Mode.Should().Be(NarrativeMode.LlmAssisted);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task BuildAsync_WhenLlmProviderThrows_FallsBackToDeterministic()
    {
        var llm = new ThrowingLlmNarrativeProvider(new InvalidOperationException("boom"));
        var builder = CreateBuilder(out _, narrativeEnabled: true, llmProvider: llm);
        var package = BuildBufferPackage();

        var report = await builder.BuildAsync("job-123", package, CancellationToken.None);

        report.NarrativeMode.Should().Be(NarrativeMode.FallbackFromLlmError);
        var narrative = report.Sections.OfType<NarrativeSection>().Single();
        narrative.LlmText.Should().BeNull();
        narrative.DeterministicText.Should().NotBeNullOrEmpty();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task BuildAsync_WhenNarrativeDisabled_SkipsLlmProvider()
    {
        var llm = new ThrowingLlmNarrativeProvider(new InvalidOperationException("should not be called"));
        var builder = CreateBuilder(out _, narrativeEnabled: false, llmProvider: llm);
        var package = BuildBufferPackage();

        var report = await builder.BuildAsync("job-123", package, CancellationToken.None);

        report.NarrativeMode.Should().Be(NarrativeMode.Deterministic);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task BuildAsync_UnknownProcess_UsesGenericTemplate()
    {
        var builder = CreateBuilder(out _, narrativeEnabled: false, llmProvider: null);
        var package = AnalysisResultPackage.CreateCompleted(
            resultPackageId: "pkg-generic",
            summary: new ResultSummary { Title = "Unknown process" },
            artifacts: Array.Empty<ArtifactRef>(),
            workspaceRefs: Array.Empty<WorkspaceRef>(),
            provenance: new ProvenanceRecord
            {
                Sources = Array.Empty<ProvenanceSource>(),
                ProcessDefinitions = new[] { "tooling.unknown" }
            });

        var report = await builder.BuildAsync("job-unknown", package, CancellationToken.None);

        report.TemplateId.Should().Be(ReportingConstants.GenericTemplateId);
        report.ProcessId.Should().Be("tooling.unknown");
        report.ProcessFamily.Should().Be("tooling");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task BuildAsync_StableReportId_IsIdempotentForSamePackage()
    {
        var builder = CreateBuilder(out _, narrativeEnabled: false, llmProvider: null);
        var package = BuildBufferPackage();

        var first = await builder.BuildAsync("job-123", package, CancellationToken.None);
        var second = await builder.BuildAsync("job-123", package, CancellationToken.None);

        first.ReportId.Should().Be(second.ReportId);
    }

    private static AnalysisReportBuilder CreateBuilder(
        out AnalysisReportTemplateRegistry registry,
        bool narrativeEnabled,
        INarrativeProvider? llmProvider)
    {
        var templates = new IAnalysisReportTemplate[]
        {
            new GenericAnalysisReportTemplate(),
            new AnalyticsBufferAggregateReportTemplate(),
            new AnalyticsDensityReportTemplate(),
            new SurfaceSlopeReportTemplate(),
            new GeneralizationDissolveReportTemplate()
        };
        registry = new AnalysisReportTemplateRegistry(templates);
        var deterministic = new DeterministicNarrativeProvider();
        var options = Options.Create(new ReportingConfiguration
        {
            Narrative = new ReportingNarrativeConfiguration { Enabled = narrativeEnabled }
        });
        return new AnalysisReportBuilder(
            registry,
            deterministic,
            options,
            new FixedTimeProvider(_fixedInstant),
            NullLogger<AnalysisReportBuilder>.Instance,
            llmProvider);
    }

    private static AnalysisResultPackage BuildBufferPackage()
    {
        var artifact = new ArtifactRef
        {
            ArtifactId = "artifact-1",
            Kind = ArtifactKind.FeatureLayer,
            Label = "Buffered places",
            Metadata = new Dictionary<string, string>
            {
                ["distance"] = "500",
                ["unit"] = "meters",
                ["bufferedFeatureCount"] = "42",
                ["dissolvedFeatureCount"] = "7",
                ["totalAreaSquareMeters"] = "123456.789"
            }
        };

        return AnalysisResultPackage.CreateCompleted(
            resultPackageId: "pkg-1",
            summary: new ResultSummary
            {
                Title = "Buffered places",
                Description = "500m buffers applied to seed places, dissolved per group."
            },
            artifacts: new[] { artifact },
            workspaceRefs: Array.Empty<WorkspaceRef>(),
            provenance: new ProvenanceRecord
            {
                Sources = new[]
                {
                    new ProvenanceSource { SourceId = "places", Description = "Seed places layer" }
                },
                ProcessDefinitions = new[] { "analytics.buffer-aggregate" },
                ExecutedAt = DateTimeOffset.Parse("2026-04-24T09:55:00Z", CultureInfo.InvariantCulture),
                GeneratedArtifactIds = new[] { "artifact-1" }
            },
            assumptions: new[] { "Input places are in EPSG:4326." });
    }

    private sealed class StubLlmNarrativeProvider : INarrativeProvider
    {
        private readonly Dictionary<string, string> _slots;

        public StubLlmNarrativeProvider(Dictionary<string, string> slots)
        {
            _slots = slots;
        }

        public bool IsDeterministic => false;

        public Task<NarrativeFill> GenerateAsync(AnalysisReportDraft draft, CancellationToken cancellationToken)
            => Task.FromResult(new NarrativeFill { SlotText = _slots });
    }

    private sealed class ThrowingLlmNarrativeProvider : INarrativeProvider
    {
        private readonly Exception _ex;

        public ThrowingLlmNarrativeProvider(Exception ex)
        {
            _ex = ex;
        }

        public bool IsDeterministic => false;

        public Task<NarrativeFill> GenerateAsync(AnalysisReportDraft draft, CancellationToken cancellationToken)
            => throw _ex;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
