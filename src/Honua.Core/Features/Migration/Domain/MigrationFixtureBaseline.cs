// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;
namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Stable status values for migration run metric baseline classification.
/// </summary>
/// <remarks>
/// Mirrors <see cref="MigrationCostPerformanceClassifications"/> but capitalized
/// to match the <c>Status</c> field required by the slice-2 baseline artifact.
/// </remarks>
public static class MigrationMetricBaselineStatuses
{
    /// <summary>All observed metrics are within the configured pass bands.</summary>
    public const string Pass = "Pass";

    /// <summary>At least one metric reached the warning band.</summary>
    public const string Warn = "Warn";

    /// <summary>At least one metric reached the failure band.</summary>
    public const string Fail = "Fail";
}

/// <summary>
/// Deterministic expected envelope describing what a fixture of a given size should
/// produce for a particular source family. The envelope is used to size pass/warn/fail
/// bands and to document the fixture for release evidence consumers.
/// </summary>
public sealed record MigrationFixtureSizeProfile
{
    /// <summary>Stable source family classification (see <see cref="MigrationCostPerformanceSourceFamilies"/>).</summary>
    public required string SourceFamily { get; init; }

    /// <summary>Fixture size classification (see <see cref="MigrationCostPerformanceFixtureSizes"/>).</summary>
    public required string Size { get; init; }

    /// <summary>Reviewer-facing description of the fixture envelope.</summary>
    public required string Description { get; init; }

    /// <summary>Expected resource count (workspaces, layers, services) emitted by the fixture.</summary>
    public long? ExpectedResourceCount { get; init; }

    /// <summary>Expected features emitted by the fixture.</summary>
    public long? ExpectedFeatureCount { get; init; }

    /// <summary>Expected coverages or raster assets emitted by the fixture.</summary>
    public long? ExpectedCoverageCount { get; init; }

    /// <summary>Expected wall-clock duration for a full migration run in milliseconds.</summary>
    public long? ExpectedDurationMilliseconds { get; init; }

    /// <summary>Expected bytes read from source during a full migration run.</summary>
    public long? ExpectedBytesRead { get; init; }

    /// <summary>Expected bytes written to Honua stores or artifacts during a full migration run.</summary>
    public long? ExpectedBytesWritten { get; init; }

    /// <summary>Expected source request count for a full migration run.</summary>
    public long? ExpectedSourceRequestCount { get; init; }
}

/// <summary>
/// Pass/warn/fail bands for a single metric within a fixture-size baseline.
/// </summary>
/// <remarks>
/// Most metrics are upper-bound (smaller is better, e.g. duration). Lower-bound metrics
/// (throughput) are encoded with <see cref="LowerBound"/> set to <c>true</c>: the WarnAtOrBelow
/// and FailAtOrBelow values are then floors instead of ceilings.
/// </remarks>
public sealed record MigrationMetricBaselineBand
{
    /// <summary>Stable metric identifier (e.g. <c>durationMilliseconds</c>, <c>featureThroughputPerSecond</c>).</summary>
    public required string Metric { get; init; }

    /// <summary>Metric unit, such as <c>milliseconds</c>, <c>bytes</c>, <c>count</c>, <c>ratio</c>, or <c>features/second</c>.</summary>
    public required string Unit { get; init; }

    /// <summary>Warning threshold. For upper-bound metrics, values at or above this trigger Warn.</summary>
    public double? WarnAtOrAbove { get; init; }

    /// <summary>Failure threshold. For upper-bound metrics, values at or above this trigger Fail.</summary>
    public double? FailAtOrAbove { get; init; }

    /// <summary>Warning floor for lower-bound metrics. Values at or below this trigger Warn.</summary>
    public double? WarnAtOrBelow { get; init; }

    /// <summary>Failure floor for lower-bound metrics. Values at or below this trigger Fail.</summary>
    public double? FailAtOrBelow { get; init; }

    /// <summary>Whether this metric is lower-bound (throughput-like). Defaults to <c>false</c> (upper-bound).</summary>
    public bool LowerBound { get; init; }
}

/// <summary>
/// Per-metric pass/warn/fail bands for a single (source family, fixture size) baseline.
/// </summary>
public sealed record MigrationMetricBaseline
{
    /// <summary>Stable baseline profile name used in artifact output.</summary>
    public required string ProfileName { get; init; }

    /// <summary>Source family the baseline applies to.</summary>
    public required string SourceFamily { get; init; }

    /// <summary>Fixture size the baseline applies to.</summary>
    public required string Size { get; init; }

    /// <summary>Expected fixture envelope. Documents what the baseline assumes about counts/bytes/duration.</summary>
    public required MigrationFixtureSizeProfile FixtureProfile { get; init; }

    /// <summary>Per-metric bands. Order is preserved when emitted.</summary>
    public MigrationMetricBaselineBand[] Bands { get; init; } = [];
}

/// <summary>
/// Per-metric classification signal emitted by the baseline evaluator.
/// </summary>
public sealed record MigrationMetricBaselineSignal
{
    /// <summary>Metric identifier matching the corresponding <see cref="MigrationMetricBaselineBand.Metric"/>.</summary>
    public required string Metric { get; init; }

    /// <summary>Status for this metric: <c>Pass</c>, <c>Warn</c>, or <c>Fail</c>.</summary>
    public required string Status { get; init; }

    /// <summary>Metric unit copied from the band.</summary>
    public required string Unit { get; init; }

    /// <summary>Observed metric value when present in the run artifact.</summary>
    public double? Observed { get; init; }

    /// <summary>Warning threshold or floor copied from the band.</summary>
    public double? WarnThreshold { get; init; }

    /// <summary>Failure threshold or floor copied from the band.</summary>
    public double? FailThreshold { get; init; }

    /// <summary>Short deterministic explanation for reviewers.</summary>
    public required string Summary { get; init; }
}

/// <summary>
/// Release-safe artifact pairing a <see cref="MigrationRunMetricsArtifact"/> with its
/// pass/warn/fail evaluation against a chosen <see cref="MigrationMetricBaseline"/>.
/// </summary>
public sealed record MigrationRunMetricsBaselineArtifact
{
    /// <summary>Stable artifact kind identifier.</summary>
    public string ArtifactKind { get; init; } = "honua.migration.run-metrics-baseline";

    /// <summary>Artifact schema version.</summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>Source family the run was evaluated for.</summary>
    public required string SourceFamily { get; init; }

    /// <summary>Fixture size the run was evaluated against.</summary>
    public required string Size { get; init; }

    /// <summary>Baseline profile name used by the evaluator.</summary>
    public required string BaselineProfile { get; init; }

    /// <summary>Aggregate status across all evaluated metrics.</summary>
    public required string Status { get; init; }

    /// <summary>Reviewer-facing summary explaining the status.</summary>
    public required string Summary { get; init; }

    /// <summary>Run id copied from the input artifact when available.</summary>
    public string? RunId { get; init; }

    /// <summary>Measurement scope copied from the input artifact.</summary>
    public required string MeasurementScope { get; init; }

    /// <summary>Per-metric classification signals in deterministic order.</summary>
    public MigrationMetricBaselineSignal[] Signals { get; init; } = [];

    /// <summary>Expected fixture envelope used by the evaluator.</summary>
    public required MigrationFixtureSizeProfile FixtureProfile { get; init; }
}
