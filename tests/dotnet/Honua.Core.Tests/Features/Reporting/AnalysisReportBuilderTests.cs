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
    public async Task BuildAsync_WhenLlmProviderReturnsUnknownSlotKeys_StaysDeterministic()
    {
        // Defends the report-level NarrativeMode invariant: claiming
        // LlmAssisted requires a declared slot to actually be replaced with
        // distinct LLM prose. An LLM that returns slot keys the template did
        // not declare must not flip the mode.
        var llm = new StubLlmNarrativeProvider(new Dictionary<string, string>
        {
            ["unknown-slot"] = "Off-template paragraph"
        });
        var builder = CreateBuilder(out _, narrativeEnabled: true, llmProvider: llm);
        var package = BuildBufferPackage();

        var report = await builder.BuildAsync("job-123", package, CancellationToken.None);

        report.NarrativeMode.Should().Be(NarrativeMode.Deterministic);
        var narrative = report.Sections.OfType<NarrativeSection>().Single();
        narrative.LlmText.Should().BeNull();
        narrative.Mode.Should().Be(NarrativeMode.Deterministic);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task BuildAsync_WhenLlmProviderEchoesDeterministicText_StaysDeterministic()
    {
        // The merge keeps the deterministic baseline when the LLM echoes
        // identical text. Report-level mode must not claim LlmAssisted
        // because no observable replacement happened.
        var package = BuildBufferPackage();
        var draft = new AnalyticsBufferAggregateReportTemplate().Build(package);
        var echo = draft.NarrativeSlots.Single().DeterministicText;
        var llm = new StubLlmNarrativeProvider(new Dictionary<string, string>
        {
            ["summary"] = echo
        });
        var builder = CreateBuilder(out _, narrativeEnabled: true, llmProvider: llm);

        var report = await builder.BuildAsync("job-123", package, CancellationToken.None);

        report.NarrativeMode.Should().Be(NarrativeMode.Deterministic);
        report.Sections.OfType<NarrativeSection>().Single().LlmText.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task BuildAsync_WhenLlmProviderReturnsBlankSlotText_StaysDeterministic()
    {
        // MergeFill drops blank LLM values. After merging, no slot carries
        // distinct LLM prose, so report mode must remain Deterministic.
        var llm = new StubLlmNarrativeProvider(new Dictionary<string, string>
        {
            ["summary"] = "   "
        });
        var builder = CreateBuilder(out _, narrativeEnabled: true, llmProvider: llm);
        var package = BuildBufferPackage();

        var report = await builder.BuildAsync("job-123", package, CancellationToken.None);

        report.NarrativeMode.Should().Be(NarrativeMode.Deterministic);
        report.Sections.OfType<NarrativeSection>().Single().LlmText.Should().BeNull();
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

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task BuildAsync_AppliesMaxTableRowsCapAcrossArtifactAssumptionAndErrorTables()
    {
        var builder = CreateBuilder(
            out _,
            narrativeEnabled: false,
            llmProvider: null,
            maxTableRows: 1);
        var package = BuildFailedGenericPackage();

        var report = await builder.BuildAsync("job-cap", package, CancellationToken.None);

        var tables = report.Sections.OfType<TableSection>().ToList();
        tables.Should().HaveCountGreaterThanOrEqualTo(3,
            "the failed generic package should produce errors, artifacts, and assumptions tables");

        foreach (var table in tables)
        {
            table.Rows.Should().HaveCount(1, "MaxTableRows=1 should bind every TableSection");
            table.TruncatedRowCount.Should().BeGreaterThan(0, "rows beyond the cap must be reflected in TruncatedRowCount");
        }
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task BuildAsync_AllowsMaxTableRowsAboveTwoHundred()
    {
        // Templates previously hard-coded a 200-row cap, masking any operator
        // configuration that wanted to surface more rows. Build a package with
        // >200 rows and a configured cap of 250 — the artifact/assumption
        // tables must keep all 250 rows inside the cap and only truncate the
        // overflow.
        const int packageRows = 280;
        const int configuredCap = 250;

        var builder = CreateBuilder(
            out _,
            narrativeEnabled: false,
            llmProvider: null,
            maxTableRows: configuredCap);
        var package = BuildLargeGenericPackage(packageRows);

        var report = await builder.BuildAsync("job-large", package, CancellationToken.None);

        var tables = report.Sections.OfType<TableSection>().ToList();
        tables.Should().NotBeEmpty();

        foreach (var table in tables)
        {
            table.Rows.Count.Should().BeLessThanOrEqualTo(configuredCap,
                "operator-configured MaxTableRows must be the binding cap");
            if (table.Rows.Count == configuredCap)
            {
                table.Rows.Count.Should().BeGreaterThan(200,
                    "the configured cap must override the legacy template-local 200-row clamp");
                table.TruncatedRowCount.Should().Be(packageRows - configuredCap,
                    "the truncation count must reflect rows trimmed at the configured cap");
            }
        }
    }

    private static AnalysisReportBuilder CreateBuilder(
        out AnalysisReportTemplateRegistry registry,
        bool narrativeEnabled,
        INarrativeProvider? llmProvider,
        int? maxTableRows = null)
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
        var configuration = new ReportingConfiguration
        {
            Narrative = new ReportingNarrativeConfiguration { Enabled = narrativeEnabled }
        };
        if (maxTableRows is int cap)
        {
            configuration.MaxTableRows = cap;
        }
        var options = Options.Create(configuration);
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

    private static AnalysisResultPackage BuildFailedGenericPackage()
    {
        // Failed status hits the Errors table path on GenericAnalysisReportTemplate.
        // Three of each so MaxTableRows=1 forces non-zero TruncatedRowCount on
        // artifacts, assumptions, and errors.
        var artifacts = new[]
        {
            new ArtifactRef { ArtifactId = "a1", Kind = ArtifactKind.FeatureLayer, Label = "L1" },
            new ArtifactRef { ArtifactId = "a2", Kind = ArtifactKind.FeatureLayer, Label = "L2" },
            new ArtifactRef { ArtifactId = "a3", Kind = ArtifactKind.FeatureLayer, Label = "L3" }
        };
        var package = AnalysisResultPackage.CreateFailed(
            resultPackageId: "pkg-cap",
            summary: new ResultSummary { Title = "Failed run" },
            errors: new[]
            {
                new GeoprocessingError { Kind = GeoprocessingErrorKind.ValidationFailed, Message = "e1" },
                new GeoprocessingError { Kind = GeoprocessingErrorKind.ValidationFailed, Message = "e2" },
                new GeoprocessingError { Kind = GeoprocessingErrorKind.ValidationFailed, Message = "e3" }
            },
            provenance: new ProvenanceRecord
            {
                Sources = Array.Empty<ProvenanceSource>(),
                ProcessDefinitions = new[] { "tooling.unknown" },
                Assumptions = new[] { "p1", "p2", "p3" }
            });
        return package with
        {
            Artifacts = artifacts,
            Assumptions = new[] { "u1", "u2", "u3" }
        };
    }

    private static AnalysisResultPackage BuildLargeGenericPackage(int rowCount)
    {
        // Generic-template path with rowCount artifacts and assumptions so we
        // can exercise MaxTableRows values above the historical 200-row clamp.
        var artifacts = Enumerable
            .Range(0, rowCount)
            .Select(i => new ArtifactRef
            {
                ArtifactId = $"a{i}",
                Kind = ArtifactKind.FeatureLayer,
                Label = $"L{i}"
            })
            .ToArray();
        var assumptions = Enumerable
            .Range(0, rowCount)
            .Select(i => $"u{i}")
            .ToArray();
        var package = AnalysisResultPackage.CreateCompleted(
            resultPackageId: "pkg-large",
            summary: new ResultSummary { Title = "Large run" },
            artifacts: artifacts,
            workspaceRefs: Array.Empty<WorkspaceRef>(),
            provenance: new ProvenanceRecord
            {
                Sources = Array.Empty<ProvenanceSource>(),
                ProcessDefinitions = new[] { "tooling.large" }
            },
            assumptions: assumptions);
        return package;
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
