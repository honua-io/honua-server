// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Stable verdict states for the performance-parity gate (issue #1249).
/// </summary>
/// <remarks>
/// The values intentionally mirror <see cref="MigrationMetricBaselineStatuses"/> so the
/// perf-parity gate reads as the latency analogue of the migration baseline gate and so a
/// single aggregation rule (any Fail =&gt; Fail, else any Warn =&gt; Warn, else Pass) governs
/// both the correctness and performance lanes of the reconciliation scorecard.
/// </remarks>
public static class PerfParityVerdicts
{
    /// <summary>All measured latency ratios are within the configured pass budget.</summary>
    public const string Pass = "Pass";

    /// <summary>At least one measured latency ratio reached the warning budget.</summary>
    public const string Warn = "Warn";

    /// <summary>At least one measured latency ratio reached or exceeded the fail budget.</summary>
    public const string Fail = "Fail";

    /// <summary>No latency was measured (e.g. the source service returned no samples).</summary>
    public const string Unknown = "Unknown";
}

/// <summary>
/// Stable identifiers for the latency metrics graded by the performance-parity gate.
/// </summary>
public static class PerfParityMetrics
{
    /// <summary>Ratio of Honua p95 latency to source p95 latency.</summary>
    public const string P95Ratio = "p95Ratio";

    /// <summary>Ratio of Honua p99 latency to source p99 latency.</summary>
    public const string P99Ratio = "p99Ratio";
}

/// <summary>
/// Configurable budget that grades Honua-vs-source latency ratios for a sampled operation.
/// </summary>
/// <remarks>
/// <para>
/// A ratio of <c>1.0</c> means Honua and the source service served the operation at identical
/// latency; a ratio of <c>1.5</c> means Honua's p95 was 50&#37; slower than the source. The gate
/// is an <em>upper-bound</em> check: lower ratios are always better, so a ratio at or above the
/// <see cref="WarnP95RatioAtOrAbove"/> budget produces a warning and a ratio at or above the
/// <see cref="FailP95RatioAtOrAbove"/> budget fails the gate.
/// </para>
/// <para>
/// Thresholds are intentionally first-class, serializable fields rather than constants so that
/// CI, the integration suite, and operators can tighten or relax the budget per deployment without
/// recompiling. A budget where both thresholds are <c>null</c> disables grading for that metric.
/// </para>
/// </remarks>
public sealed record PerfParityBudget
{
    /// <summary>Reviewer-facing budget profile name (e.g. <c>geoservices-parity-default-v1</c>).</summary>
    public required string ProfileName { get; init; }

    /// <summary>
    /// Warning threshold for the Honua/source p95 ratio. A measured ratio at or above this value,
    /// but below <see cref="FailP95RatioAtOrAbove"/>, produces a <see cref="PerfParityVerdicts.Warn"/>.
    /// </summary>
    public double? WarnP95RatioAtOrAbove { get; init; }

    /// <summary>
    /// Fail threshold for the Honua/source p95 ratio. A measured ratio at or above this value fails
    /// the gate. This is the perf budget the gate enforces.
    /// </summary>
    public double? FailP95RatioAtOrAbove { get; init; }

    /// <summary>Warning threshold for the Honua/source p99 ratio.</summary>
    public double? WarnP99RatioAtOrAbove { get; init; }

    /// <summary>Fail threshold for the Honua/source p99 ratio.</summary>
    public double? FailP99RatioAtOrAbove { get; init; }

    /// <summary>
    /// Minimum latency sample count required before a ratio is graded. Runs with fewer samples are
    /// reported as <see cref="PerfParityVerdicts.Unknown"/> rather than graded, so a thin sample does
    /// not produce a false Pass or Fail. Defaults to 1.
    /// </summary>
    public int MinimumSampleCount { get; init; } = 1;

    /// <summary>
    /// Default budget used by the GeoServices parity suite. Honua is expected to serve sampled
    /// queries no slower than 1.5&#215; the source at p95 (warn) / 2.0&#215; (fail), with slightly more
    /// headroom at the noisier p99 tail (warn 1.75&#215; / fail 2.5&#215;).
    /// </summary>
    public static PerfParityBudget GeoServicesDefault { get; } = new()
    {
        ProfileName = "geoservices-parity-default-v1",
        WarnP95RatioAtOrAbove = 1.5d,
        FailP95RatioAtOrAbove = 2.0d,
        WarnP99RatioAtOrAbove = 1.75d,
        FailP99RatioAtOrAbove = 2.5d,
        MinimumSampleCount = 5
    };
}

/// <summary>
/// Per-metric grading signal emitted by the performance-parity gate.
/// </summary>
public sealed record PerfParitySignal
{
    /// <summary>Metric identifier (see <see cref="PerfParityMetrics"/>).</summary>
    public required string Metric { get; init; }

    /// <summary>Verdict for this metric (see <see cref="PerfParityVerdicts"/>).</summary>
    public required string Verdict { get; init; }

    /// <summary>Measured Honua/source ratio, or <c>null</c> when not measured.</summary>
    public double? ObservedRatio { get; init; }

    /// <summary>Warning budget applied to this metric, or <c>null</c> when grading was disabled.</summary>
    public double? WarnThreshold { get; init; }

    /// <summary>Fail budget applied to this metric, or <c>null</c> when grading was disabled.</summary>
    public double? FailThreshold { get; init; }

    /// <summary>Short reviewer-facing explanation of the assigned verdict.</summary>
    public required string Summary { get; init; }
}

/// <summary>
/// Performance-parity verdict for a single sampled operation, the latency analogue of the
/// correctness checks already carried by the reconciliation scorecard (issue #1249).
/// </summary>
public sealed record PerfParityVerdict
{
    /// <summary>Stable artifact kind identifier.</summary>
    public string ArtifactKind { get; init; } = "honua.migration.perf-parity-verdict";

    /// <summary>Artifact schema version.</summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>Budget profile name the verdict was graded against.</summary>
    public required string BudgetProfile { get; init; }

    /// <summary>Aggregate verdict across all graded metrics (see <see cref="PerfParityVerdicts"/>).</summary>
    public required string Verdict { get; init; }

    /// <summary>Reviewer-facing one-line summary of the graded run.</summary>
    public required string Summary { get; init; }

    /// <summary>Number of latency samples that produced the graded ratios.</summary>
    public int SampleCount { get; init; }

    /// <summary>Per-metric grading signals in deterministic order.</summary>
    public PerfParitySignal[] Signals { get; init; } = [];
}
