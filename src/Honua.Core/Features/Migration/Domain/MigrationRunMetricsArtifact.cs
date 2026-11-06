// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Domain;

// The artifact records raw measurements only and never includes thresholds, source URLs, credentials, or payloads.
public sealed record MigrationRunMetricsArtifact
{
    public string ArtifactKind { get; init; } = "honua.migration.run-metrics";

    public string ArtifactVersion { get; init; } = "1.0";

    public required string SourceKind { get; init; }

    public required string SourceFamily { get; init; }

    public required MigrationRunMetricsSourceSummary Source { get; init; }

    public string? RunId { get; init; }

    public required string MeasurementScope { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public required MigrationRunMetricsValues Totals { get; init; }

    public MigrationRunMetricsPhase[] Phases { get; init; } = [];

    public MigrationRunMetricsResourceSample[] ResourceSamples { get; init; } = [];

    public string[] ResumeMarkers { get; init; } = [];

    public required MigrationRunMetricsPrivacySummary Privacy { get; init; }
}

public sealed record MigrationRunMetricsSourceSummary
{
    public required string DisplayName { get; init; }

    public string? Product { get; init; }

    public string? Version { get; init; }

    public string? ServiceType { get; init; }
}

public sealed record MigrationRunMetricsValues
{
    public long? DurationMilliseconds { get; init; }

    public long? SourceRequestCount { get; init; }

    public long? BytesRead { get; init; }

    public long? BytesWritten { get; init; }

    public int? RetryCount { get; init; }

    public int? ResumeCount { get; init; }

    public bool? ResumeFromCheckpoint { get; init; }

    public int? IdempotentReplayCount { get; init; }

    public int? CancellationCount { get; init; }

    public long? CpuMilliseconds { get; init; }

    public long? PeakMemoryBytes { get; init; }

    public long? DatabaseGrowthBytes { get; init; }

    public long? DatabaseGrowthRows { get; init; }

    public long? ArtifactBytes { get; init; }

    public long? ResourceCount { get; init; }

    public long? FeatureCount { get; init; }

    public long? CoverageCount { get; init; }

    public double? ResourceThroughputPerSecond { get; init; }

    public double? FeatureThroughputPerSecond { get; init; }

    public double? CoverageThroughputPerSecond { get; init; }

    public int? ManualReviewCount { get; init; }

    public int? CandidateItemCount { get; init; }

    public double? ManualReviewRatio { get; init; }
}

public sealed record MigrationRunMetricsPhase
{
    public required string Phase { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public required MigrationRunMetricsValues Metrics { get; init; }
}

public sealed record MigrationRunMetricsResourceSample
{
    public required DateTimeOffset SampledAt { get; init; }

    public string? Phase { get; init; }

    public long? CpuMilliseconds { get; init; }

    public long? WorkingSetBytes { get; init; }

    public long? GcHeapBytes { get; init; }
}

public sealed record MigrationRunMetricsPrivacySummary
{
    public bool SourceUrlsIncluded { get; init; }

    public bool CredentialValuesIncluded { get; init; }

    public bool SourceDataIncluded { get; init; }

    public string[] OmittedFields { get; init; } = [];
}
