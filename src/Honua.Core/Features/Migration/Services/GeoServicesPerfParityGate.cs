// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Grades measured Honua-vs-source latency ratios against a configurable
/// <see cref="PerfParityBudget"/> and emits a <see cref="PerfParityVerdict"/> (issue #1249).
/// </summary>
/// <remarks>
/// <para>
/// The reconciliation suite already <em>computes</em> p95/p99 latency ratios for sampled
/// operations but never <em>gated</em> them, so a performance regression could ship silently. This
/// gate is the performance analogue of <see cref="MigrationRunMetricsBaselineEvaluator"/>: it is a
/// pure, deterministic function over the already-measured ratios, so the gating logic, the verdict
/// emission, and the scorecard shape are all fully testable offline without a live benchmark run.
/// </para>
/// <para>
/// Wiring: the GeoServices parity integration suite calls <see cref="Evaluate"/> with the ratios it
/// already measures and embeds the returned verdict into the emitted scorecard; CI then enforces the
/// verdict via <c>scripts/ci/check-parity-perf-budget.sh</c>.
/// </para>
/// </remarks>
public static class GeoServicesPerfParityGate
{
    /// <summary>
    /// Grade the supplied latency ratios against <paramref name="budget"/>.
    /// </summary>
    /// <param name="sampleCount">Number of latency samples that produced the ratios.</param>
    /// <param name="honuaToSourceP95Ratio">Honua/source p95 latency ratio, or <c>null</c> when unmeasured.</param>
    /// <param name="honuaToSourceP99Ratio">Honua/source p99 latency ratio, or <c>null</c> when unmeasured.</param>
    /// <param name="budget">Budget to grade against.</param>
    /// <returns>A verdict with per-metric signals and an aggregate Pass/Warn/Fail/Unknown state.</returns>
    public static PerfParityVerdict Evaluate(
        int sampleCount,
        double? honuaToSourceP95Ratio,
        double? honuaToSourceP99Ratio,
        PerfParityBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);

        var hasEnoughSamples = sampleCount >= budget.MinimumSampleCount;

        var signals = new List<PerfParitySignal>(2)
        {
            EvaluateMetric(
                PerfParityMetrics.P95Ratio,
                hasEnoughSamples ? honuaToSourceP95Ratio : null,
                budget.WarnP95RatioAtOrAbove,
                budget.FailP95RatioAtOrAbove,
                hasEnoughSamples,
                sampleCount,
                budget.MinimumSampleCount),
            EvaluateMetric(
                PerfParityMetrics.P99Ratio,
                hasEnoughSamples ? honuaToSourceP99Ratio : null,
                budget.WarnP99RatioAtOrAbove,
                budget.FailP99RatioAtOrAbove,
                hasEnoughSamples,
                sampleCount,
                budget.MinimumSampleCount)
        };

        var verdict = Aggregate(signals);

        return new PerfParityVerdict
        {
            BudgetProfile = budget.ProfileName,
            Verdict = verdict,
            Summary = BuildSummary(verdict, signals),
            SampleCount = sampleCount,
            Signals = signals.ToArray()
        };
    }

    private static PerfParitySignal EvaluateMetric(
        string metric,
        double? observed,
        double? warn,
        double? fail,
        bool hasEnoughSamples,
        int sampleCount,
        int minimumSampleCount)
    {
        // Grading disabled for this metric: no thresholds configured.
        if (warn == null && fail == null)
        {
            return new PerfParitySignal
            {
                Metric = metric,
                Verdict = PerfParityVerdicts.Pass,
                ObservedRatio = observed,
                WarnThreshold = warn,
                FailThreshold = fail,
                Summary = $"{metric} grading is disabled (no budget configured)."
            };
        }

        if (!hasEnoughSamples)
        {
            return new PerfParitySignal
            {
                Metric = metric,
                Verdict = PerfParityVerdicts.Unknown,
                ObservedRatio = null,
                WarnThreshold = warn,
                FailThreshold = fail,
                Summary = $"{metric} was not graded: {sampleCount} sample(s) is below the minimum of {minimumSampleCount}."
            };
        }

        if (observed == null)
        {
            return new PerfParitySignal
            {
                Metric = metric,
                Verdict = PerfParityVerdicts.Unknown,
                ObservedRatio = null,
                WarnThreshold = warn,
                FailThreshold = fail,
                Summary = $"{metric} was not measured (source latency was zero or unavailable)."
            };
        }

        // A non-positive ratio is impossible for real latency measurements; treat as a fault.
        if (observed <= 0)
        {
            return new PerfParitySignal
            {
                Metric = metric,
                Verdict = PerfParityVerdicts.Fail,
                ObservedRatio = observed,
                WarnThreshold = warn,
                FailThreshold = fail,
                Summary = $"{metric} must be a positive ratio."
            };
        }

        var verdict = Classify(observed.Value, warn, fail);
        return new PerfParitySignal
        {
            Metric = metric,
            Verdict = verdict,
            ObservedRatio = observed,
            WarnThreshold = warn,
            FailThreshold = fail,
            Summary = BuildSignalSummary(metric, verdict, observed.Value, warn, fail)
        };
    }

    private static string Classify(double observed, double? warn, double? fail)
    {
        if (fail != null && observed >= fail.Value)
        {
            return PerfParityVerdicts.Fail;
        }

        if (warn != null && observed >= warn.Value)
        {
            return PerfParityVerdicts.Warn;
        }

        return PerfParityVerdicts.Pass;
    }

    private static string Aggregate(IReadOnlyCollection<PerfParitySignal> signals)
    {
        if (signals.Any(static s => s.Verdict == PerfParityVerdicts.Fail))
        {
            return PerfParityVerdicts.Fail;
        }

        if (signals.Any(static s => s.Verdict == PerfParityVerdicts.Warn))
        {
            return PerfParityVerdicts.Warn;
        }

        // If every graded metric is Unknown, the aggregate is Unknown rather than a false Pass.
        if (signals.All(static s => s.Verdict == PerfParityVerdicts.Unknown))
        {
            return PerfParityVerdicts.Unknown;
        }

        return PerfParityVerdicts.Pass;
    }

    private static string BuildSignalSummary(string metric, string verdict, double observed, double? warn, double? fail)
    {
        var ratio = observed.ToString("0.###", CultureInfo.InvariantCulture);
        return verdict switch
        {
            PerfParityVerdicts.Fail =>
                $"{metric} {ratio}x reached the fail budget of {Format(fail)}x.",
            PerfParityVerdicts.Warn =>
                $"{metric} {ratio}x reached the warn budget of {Format(warn)}x.",
            _ => $"{metric} {ratio}x is within the perf budget."
        };
    }

    private static string BuildSummary(string verdict, IReadOnlyCollection<PerfParitySignal> signals)
    {
        var failCount = signals.Count(static s => s.Verdict == PerfParityVerdicts.Fail);
        var warnCount = signals.Count(static s => s.Verdict == PerfParityVerdicts.Warn);

        return verdict switch
        {
            PerfParityVerdicts.Fail =>
                $"Performance parity failed the budget with {failCount} metric(s) at fail and {warnCount} metric(s) at warn.",
            PerfParityVerdicts.Warn =>
                $"Performance parity is within fail bounds but has {warnCount} metric(s) at warn.",
            PerfParityVerdicts.Unknown =>
                "Performance parity could not be graded (insufficient samples or no source latency).",
            _ => "Performance parity is within budget for all graded metrics."
        };
    }

    private static string Format(double? value) =>
        value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "n/a";
}
