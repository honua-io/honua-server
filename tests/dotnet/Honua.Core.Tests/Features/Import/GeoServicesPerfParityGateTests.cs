// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Offline tests for <see cref="GeoServicesPerfParityGate"/> — the performance-parity gate that
/// grades measured Honua-vs-source latency ratios and emits a scorecard verdict (issue #1249).
/// </summary>
public sealed class GeoServicesPerfParityGateTests
{
    private static readonly PerfParityBudget Budget = new()
    {
        ProfileName = "test-budget-v1",
        WarnP95RatioAtOrAbove = 1.5d,
        FailP95RatioAtOrAbove = 2.0d,
        WarnP99RatioAtOrAbove = 1.75d,
        FailP99RatioAtOrAbove = 2.5d,
        MinimumSampleCount = 5
    };

    [Fact]
    public void Evaluate_WithRatiosWithinBudget_EmitsPassVerdict()
    {
        var verdict = GeoServicesPerfParityGate.Evaluate(
            sampleCount: 9,
            honuaToSourceP95Ratio: 1.1d,
            honuaToSourceP99Ratio: 1.2d,
            Budget);

        verdict.Verdict.Should().Be(PerfParityVerdicts.Pass);
        verdict.BudgetProfile.Should().Be("test-budget-v1");
        verdict.SampleCount.Should().Be(9);
        verdict.Signals.Should().OnlyContain(s => s.Verdict == PerfParityVerdicts.Pass);
        verdict.Summary.Should().Contain("within budget");
    }

    [Fact]
    public void Evaluate_OnWarnBoundary_EmitsWarnVerdict()
    {
        // p95 ratio exactly at the warn budget (>=) should warn, not fail.
        var verdict = GeoServicesPerfParityGate.Evaluate(
            sampleCount: 9,
            honuaToSourceP95Ratio: 1.5d,
            honuaToSourceP99Ratio: 1.2d,
            Budget);

        verdict.Verdict.Should().Be(PerfParityVerdicts.Warn);
        var p95 = verdict.Signals.Single(s => s.Metric == PerfParityMetrics.P95Ratio);
        p95.Verdict.Should().Be(PerfParityVerdicts.Warn);
        p95.ObservedRatio.Should().Be(1.5d);
    }

    [Fact]
    public void Evaluate_OnFailBoundary_EmitsFailVerdict()
    {
        // p95 ratio exactly at the fail budget (>=) should fail the gate.
        var verdict = GeoServicesPerfParityGate.Evaluate(
            sampleCount: 9,
            honuaToSourceP95Ratio: 2.0d,
            honuaToSourceP99Ratio: 1.2d,
            Budget);

        verdict.Verdict.Should().Be(PerfParityVerdicts.Fail);
        var p95 = verdict.Signals.Single(s => s.Metric == PerfParityMetrics.P95Ratio);
        p95.Verdict.Should().Be(PerfParityVerdicts.Fail);
        p95.FailThreshold.Should().Be(2.0d);
        verdict.Summary.Should().Contain("failed the budget");
    }

    [Fact]
    public void Evaluate_WhenP99RegressesButP95Passes_FailsOnAggregate()
    {
        var verdict = GeoServicesPerfParityGate.Evaluate(
            sampleCount: 9,
            honuaToSourceP95Ratio: 1.1d,
            honuaToSourceP99Ratio: 3.0d,
            Budget);

        verdict.Verdict.Should().Be(PerfParityVerdicts.Fail);
        verdict.Signals.Single(s => s.Metric == PerfParityMetrics.P95Ratio).Verdict
            .Should().Be(PerfParityVerdicts.Pass);
        verdict.Signals.Single(s => s.Metric == PerfParityMetrics.P99Ratio).Verdict
            .Should().Be(PerfParityVerdicts.Fail);
    }

    [Fact]
    public void Evaluate_WithTooFewSamples_EmitsUnknownVerdict()
    {
        var verdict = GeoServicesPerfParityGate.Evaluate(
            sampleCount: 2,
            honuaToSourceP95Ratio: 5.0d,
            honuaToSourceP99Ratio: 9.0d,
            Budget);

        verdict.Verdict.Should().Be(PerfParityVerdicts.Unknown);
        verdict.Signals.Should().OnlyContain(s => s.Verdict == PerfParityVerdicts.Unknown);
        verdict.Signals.Should().OnlyContain(s => s.ObservedRatio == null);
    }

    [Fact]
    public void Evaluate_WithNullRatio_EmitsUnknownForThatMetric()
    {
        // Source p95 was zero/unavailable, so the ratio is null and cannot be graded.
        var verdict = GeoServicesPerfParityGate.Evaluate(
            sampleCount: 9,
            honuaToSourceP95Ratio: null,
            honuaToSourceP99Ratio: 1.2d,
            Budget);

        verdict.Signals.Single(s => s.Metric == PerfParityMetrics.P95Ratio).Verdict
            .Should().Be(PerfParityVerdicts.Unknown);
        // p99 still passes, so the aggregate is Pass (one Unknown does not poison a real Pass).
        verdict.Verdict.Should().Be(PerfParityVerdicts.Pass);
    }

    [Fact]
    public void Evaluate_WithNonPositiveRatio_FailsTheMetric()
    {
        var verdict = GeoServicesPerfParityGate.Evaluate(
            sampleCount: 9,
            honuaToSourceP95Ratio: 0d,
            honuaToSourceP99Ratio: 1.2d,
            Budget);

        verdict.Signals.Single(s => s.Metric == PerfParityMetrics.P95Ratio).Verdict
            .Should().Be(PerfParityVerdicts.Fail);
        verdict.Verdict.Should().Be(PerfParityVerdicts.Fail);
    }

    [Fact]
    public void Evaluate_WithDisabledBudget_DoesNotGrade()
    {
        var disabled = new PerfParityBudget
        {
            ProfileName = "disabled",
            WarnP95RatioAtOrAbove = null,
            FailP95RatioAtOrAbove = null,
            WarnP99RatioAtOrAbove = null,
            FailP99RatioAtOrAbove = null,
            MinimumSampleCount = 1
        };

        var verdict = GeoServicesPerfParityGate.Evaluate(
            sampleCount: 9,
            honuaToSourceP95Ratio: 99d,
            honuaToSourceP99Ratio: 99d,
            disabled);

        verdict.Verdict.Should().Be(PerfParityVerdicts.Pass);
        verdict.Signals.Should().OnlyContain(s => s.Summary.Contains("disabled"));
    }

    [Fact]
    public void Evaluate_ThrowsOnNullBudget()
    {
        var act = () => GeoServicesPerfParityGate.Evaluate(9, 1.0d, 1.0d, budget: null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GeoServicesDefaultBudget_HasExpectedThresholds()
    {
        var budget = PerfParityBudget.GeoServicesDefault;
        budget.ProfileName.Should().Be("geoservices-parity-default-v1");
        budget.FailP95RatioAtOrAbove.Should().Be(2.0d);
        budget.WarnP95RatioAtOrAbove.Should().Be(1.5d);
        budget.MinimumSampleCount.Should().Be(5);
    }

    [Fact]
    public void Verdict_SerializesWithStableShape()
    {
        var verdict = GeoServicesPerfParityGate.Evaluate(9, 2.1d, 1.2d, Budget);

        var json = JsonSerializer.Serialize(verdict, new JsonSerializerOptions { WriteIndented = false });
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.GetProperty("ArtifactKind").GetString().Should().Be("honua.migration.perf-parity-verdict");
        root.GetProperty("Verdict").GetString().Should().Be(PerfParityVerdicts.Fail);
        root.GetProperty("BudgetProfile").GetString().Should().Be("test-budget-v1");
        root.GetProperty("Signals").GetArrayLength().Should().Be(2);
    }
}
