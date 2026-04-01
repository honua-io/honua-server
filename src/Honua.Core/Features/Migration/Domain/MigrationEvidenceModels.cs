// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Supported migration evidence source providers.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MigrationEvidenceProvider>))]
public enum MigrationEvidenceProvider
{
    /// <summary>
    /// ArcGIS GeoServices / FeatureServer and MapServer migration flow.
    /// </summary>
    [JsonStringEnumMemberName("arcgis-geoservices")]
    ArcGisGeoservices
}

/// <summary>
/// Readiness profile used when evaluating cutover evidence.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MigrationCutoverProfile>))]
public enum MigrationCutoverProfile
{
    /// <summary>
    /// Pilot readiness profile with warning tolerance.
    /// </summary>
    [JsonStringEnumMemberName("pilot")]
    Pilot,

    /// <summary>
    /// Production cutover profile with stricter blocking rules.
    /// </summary>
    [JsonStringEnumMemberName("production")]
    Production
}

/// <summary>
/// Top-level cutover readiness state computed from the report checklist.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MigrationReadinessState>))]
public enum MigrationReadinessState
{
    /// <summary>
    /// One or more blocking conditions prevent cutover.
    /// </summary>
    [JsonStringEnumMemberName("blocked")]
    Blocked,

    /// <summary>
    /// The run satisfies the pilot readiness rules.
    /// </summary>
    [JsonStringEnumMemberName("pilot_ready")]
    PilotReady,

    /// <summary>
    /// The run satisfies the production readiness rules.
    /// </summary>
    [JsonStringEnumMemberName("production_ready")]
    ProductionReady
}

/// <summary>
/// Status for comparison checks and checklist items.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MigrationEvidenceStatus>))]
public enum MigrationEvidenceStatus
{
    /// <summary>
    /// Check passed.
    /// </summary>
    [JsonStringEnumMemberName("pass")]
    Pass,

    /// <summary>
    /// Check completed with non-blocking warning(s).
    /// </summary>
    [JsonStringEnumMemberName("warning")]
    Warning,

    /// <summary>
    /// Check failed.
    /// </summary>
    [JsonStringEnumMemberName("fail")]
    Fail,

    /// <summary>
    /// Check does not apply to the current scope.
    /// </summary>
    [JsonStringEnumMemberName("not_applicable")]
    NotApplicable
}

/// <summary>
/// Execution status for background evidence-generation jobs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MigrationEvidenceJobStatus>))]
public enum MigrationEvidenceJobStatus
{
    /// <summary>
    /// Job is queued for background processing.
    /// </summary>
    [JsonStringEnumMemberName("queued")]
    Queued,

    /// <summary>
    /// Source baseline discovery is running.
    /// </summary>
    [JsonStringEnumMemberName("resolving_source_baseline")]
    ResolvingSourceBaseline,

    /// <summary>
    /// Target snapshot discovery is running.
    /// </summary>
    [JsonStringEnumMemberName("resolving_target_snapshot")]
    ResolvingTargetSnapshot,

    /// <summary>
    /// Parity and readiness comparison is running.
    /// </summary>
    [JsonStringEnumMemberName("comparing")]
    Comparing,

    /// <summary>
    /// Final report persistence is running.
    /// </summary>
    [JsonStringEnumMemberName("persisting_report")]
    PersistingReport,

    /// <summary>
    /// Job completed successfully.
    /// </summary>
    [JsonStringEnumMemberName("completed")]
    Completed,

    /// <summary>
    /// Job failed.
    /// </summary>
    [JsonStringEnumMemberName("failed")]
    Failed,

    /// <summary>
    /// Job was cancelled.
    /// </summary>
    [JsonStringEnumMemberName("cancelled")]
    Cancelled
}

/// <summary>
/// Request for generating a migration evidence report.
/// </summary>
public sealed record MigrationEvidenceRequest
{
    /// <summary>
    /// Migration provider under evaluation.
    /// </summary>
    public required MigrationEvidenceProvider Provider { get; init; }

    /// <summary>
    /// Canonical source service URL used for discovery and parity probes.
    /// </summary>
    public required string SourceServiceUrl { get; init; }

    /// <summary>
    /// Canonical public target base URL used for parity probes.
    /// </summary>
    public required string TargetBaseUrl { get; init; }

    /// <summary>
    /// Target Honua service name.
    /// </summary>
    public required string TargetServiceName { get; init; }

    /// <summary>
    /// Layer mappings to compare between source and target.
    /// </summary>
    public MigrationEvidenceLayerMapping[] Layers { get; init; } = [];

    /// <summary>
    /// Cutover readiness profile used for checklist evaluation.
    /// </summary>
    public required MigrationCutoverProfile CutoverProfile { get; init; }

    /// <summary>
    /// Optional inventory artifact reference used as provenance.
    /// </summary>
    public string? InventoryArtifactRef { get; init; }

    /// <summary>
    /// Optional translation manifest reference used as provenance.
    /// </summary>
    public string? TranslationManifestRef { get; init; }

    /// <summary>
    /// Optional import job identifier used as provenance.
    /// </summary>
    public string? ImportJobId { get; init; }

    /// <summary>
    /// Operator-provided rollback reference for the cutover decision.
    /// </summary>
    public required string RollbackPlanReference { get; init; }

    /// <summary>
    /// Optional operator identifier.
    /// </summary>
    public string? RequestedBy { get; init; }

    /// <summary>
    /// Optional human-readable summary for the run.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Maximum number of sample rows to compare per layer.
    /// </summary>
    public int SampleRowCount { get; init; } = 25;

    /// <summary>
    /// Maximum page size used for bounded parity probes.
    /// </summary>
    public int QueryPageSize { get; init; } = 50;

    /// <summary>
    /// Number of latency samples per bounded probe family.
    /// </summary>
    public int LatencySampleCount { get; init; } = 5;

    /// <summary>
    /// Timeout in seconds for a single remote probe.
    /// </summary>
    public int ProbeTimeoutSeconds { get; init; } = 30;
}

/// <summary>
/// Maps a source layer to a target layer for evidence generation.
/// </summary>
public sealed record MigrationEvidenceLayerMapping
{
    /// <summary>
    /// Source layer identifier.
    /// </summary>
    public required int SourceLayerId { get; init; }

    /// <summary>
    /// Target layer identifier.
    /// </summary>
    public required int TargetLayerId { get; init; }
}

/// <summary>
/// Immutable migration evidence report artifact.
/// </summary>
public sealed record MigrationEvidenceReport
{
    /// <summary>
    /// Unique report identifier.
    /// </summary>
    public required Guid ReportId { get; init; }

    /// <summary>
    /// Schema version for the JSON artifact.
    /// </summary>
    public required string SchemaVersion { get; init; }

    /// <summary>
    /// Timestamp when the report was generated.
    /// </summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// Stable hash of the canonical report payload.
    /// </summary>
    public required string ReportHash { get; init; }

    /// <summary>
    /// Original request and provenance metadata.
    /// </summary>
    public required MigrationEvidenceRequest Request { get; init; }

    /// <summary>
    /// Source baseline used for the run.
    /// </summary>
    public required MigrationEvidenceSourceBaseline SourceBaseline { get; init; }

    /// <summary>
    /// Target snapshot used for the run.
    /// </summary>
    public required MigrationEvidenceTargetSnapshot TargetSnapshot { get; init; }

    /// <summary>
    /// Comparison sections for capability, style, data, and readiness.
    /// </summary>
    public required MigrationEvidenceComparison Comparison { get; init; }

    /// <summary>
    /// Computed cutover-readiness summary.
    /// </summary>
    public required MigrationEvidenceReadinessSummary CutoverReadiness { get; init; }
}

/// <summary>
/// Summary row for listing stored evidence artifacts.
/// </summary>
public sealed record MigrationEvidenceReportSummary
{
    /// <summary>
    /// Report identifier.
    /// </summary>
    public required Guid ReportId { get; init; }

    /// <summary>
    /// Report schema version.
    /// </summary>
    public required string SchemaVersion { get; init; }

    /// <summary>
    /// Migration provider.
    /// </summary>
    public required MigrationEvidenceProvider Provider { get; init; }

    /// <summary>
    /// Cutover profile used for evaluation.
    /// </summary>
    public required MigrationCutoverProfile CutoverProfile { get; init; }

    /// <summary>
    /// Computed readiness state.
    /// </summary>
    public required MigrationReadinessState Readiness { get; init; }

    /// <summary>
    /// Source service URL.
    /// </summary>
    public required string SourceServiceUrl { get; init; }

    /// <summary>
    /// Target base URL.
    /// </summary>
    public required string TargetBaseUrl { get; init; }

    /// <summary>
    /// Target service name.
    /// </summary>
    public required string TargetServiceName { get; init; }

    /// <summary>
    /// Report hash.
    /// </summary>
    public required string ReportHash { get; init; }

    /// <summary>
    /// Optional operator identifier.
    /// </summary>
    public string? RequestedBy { get; init; }

    /// <summary>
    /// Optional human-readable summary.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Optional inventory artifact reference.
    /// </summary>
    public string? InventoryArtifactRef { get; init; }

    /// <summary>
    /// Optional translation manifest reference.
    /// </summary>
    public string? TranslationManifestRef { get; init; }

    /// <summary>
    /// Optional import job identifier.
    /// </summary>
    public string? ImportJobId { get; init; }

    /// <summary>
    /// Number of warnings captured in the readiness summary.
    /// </summary>
    public int WarningCount { get; init; }

    /// <summary>
    /// Number of blocking reasons captured in the readiness summary.
    /// </summary>
    public int BlockerCount { get; init; }

    /// <summary>
    /// Generation timestamp.
    /// </summary>
    public required DateTimeOffset GeneratedAt { get; init; }
}

/// <summary>
/// Source-side baseline snapshot for the evidence run.
/// </summary>
public sealed record MigrationEvidenceSourceBaseline
{
    /// <summary>
    /// Source service URL.
    /// </summary>
    public required string ServiceUrl { get; init; }

    /// <summary>
    /// Source service name.
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Source service version, when reported.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Source service capabilities.
    /// </summary>
    public string[] Capabilities { get; init; } = [];

    /// <summary>
    /// Source supported query formats.
    /// </summary>
    public string[] SupportedQueryFormats { get; init; } = [];

    /// <summary>
    /// Digest of the source service metadata used for the run.
    /// </summary>
    public required string ServiceDigest { get; init; }

    /// <summary>
    /// Per-layer baseline snapshots.
    /// </summary>
    public MigrationEvidenceLayerSnapshot[] Layers { get; init; } = [];
}

/// <summary>
/// Target-side snapshot for the evidence run.
/// </summary>
public sealed record MigrationEvidenceTargetSnapshot
{
    /// <summary>
    /// Target base URL used for public probes.
    /// </summary>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// Target service name.
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Target service digest.
    /// </summary>
    public required string ServiceDigest { get; init; }

    /// <summary>
    /// Target service capabilities.
    /// </summary>
    public string[] Capabilities { get; init; } = [];

    /// <summary>
    /// Target supported query formats.
    /// </summary>
    public string[] SupportedQueryFormats { get; init; } = [];

    /// <summary>
    /// Per-layer target snapshots.
    /// </summary>
    public MigrationEvidenceLayerSnapshot[] Layers { get; init; } = [];

    /// <summary>
    /// Operational readiness snapshot captured during report generation.
    /// </summary>
    public required MigrationEvidenceOperationalSnapshot OperationalSnapshot { get; init; }
}

/// <summary>
/// Per-layer snapshot captured from either the source or target side.
/// </summary>
public sealed record MigrationEvidenceLayerSnapshot
{
    /// <summary>
    /// Layer identifier.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Layer name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Layer geometry type string.
    /// </summary>
    public string? GeometryType { get; init; }

    /// <summary>
    /// Layer spatial reference WKID.
    /// </summary>
    public int? SpatialReferenceWkid { get; init; }

    /// <summary>
    /// Layer feature count when reported by metadata or probes.
    /// </summary>
    public long? FeatureCount { get; init; }

    /// <summary>
    /// Whether the layer advertises attachment support.
    /// </summary>
    public bool HasAttachments { get; init; }

    /// <summary>
    /// Layer fields used for comparison and provenance.
    /// </summary>
    public MigrationEvidenceFieldSnapshot[] Fields { get; init; } = [];

    /// <summary>
    /// Layer extent snapshot.
    /// </summary>
    public MigrationEvidenceExtentSnapshot? Extent { get; init; }

    /// <summary>
    /// Metadata digest for the layer payload.
    /// </summary>
    public required string LayerDigest { get; init; }

    /// <summary>
    /// Canonical style-input digest when available.
    /// </summary>
    public string? StyleDigest { get; init; }

    /// <summary>
    /// Canonical target MapLibre style digest when available.
    /// </summary>
    public string? MapLibreStyleDigest { get; init; }

    /// <summary>
    /// Notes captured while building the snapshot.
    /// </summary>
    public string[] Notes { get; init; } = [];
}

/// <summary>
/// Field snapshot used in source and target layer summaries.
/// </summary>
public sealed record MigrationEvidenceFieldSnapshot
{
    /// <summary>
    /// Original field name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Sanitized canonical field name used for parity matching.
    /// </summary>
    public required string CanonicalName { get; init; }

    /// <summary>
    /// Field type identifier.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Whether the field is nullable.
    /// </summary>
    public bool Nullable { get; init; }
}

/// <summary>
/// Extent snapshot used in source and target layer summaries.
/// </summary>
public sealed record MigrationEvidenceExtentSnapshot
{
    /// <summary>
    /// Minimum X coordinate.
    /// </summary>
    public required double MinX { get; init; }

    /// <summary>
    /// Minimum Y coordinate.
    /// </summary>
    public required double MinY { get; init; }

    /// <summary>
    /// Maximum X coordinate.
    /// </summary>
    public required double MaxX { get; init; }

    /// <summary>
    /// Maximum Y coordinate.
    /// </summary>
    public required double MaxY { get; init; }

    /// <summary>
    /// Spatial reference WKID.
    /// </summary>
    public int? SpatialReferenceWkid { get; init; }
}

/// <summary>
/// Operational readiness snapshot included in the target section.
/// </summary>
public sealed record MigrationEvidenceOperationalSnapshot
{
    /// <summary>
    /// Overall preflight status label.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Whether the instance was ready for coordinated deployment at probe time.
    /// </summary>
    public bool ReadyForCoordinatedDeploy { get; init; }

    /// <summary>
    /// Human-readable preflight message.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Whether a migration plan could be generated.
    /// </summary>
    public bool MigrationPlanAvailable { get; init; }

    /// <summary>
    /// Whether the instance reported pending migrations.
    /// </summary>
    public bool UpgradeRequired { get; init; }

    /// <summary>
    /// Pending scripts identified by the migration plan.
    /// </summary>
    public string[] PendingScripts { get; init; } = [];

    /// <summary>
    /// Executed scripts that are no longer discovered by the current binary.
    /// </summary>
    public string[] ExecutedButNotDiscoveredScripts { get; init; } = [];

    /// <summary>
    /// Whether the database compatibility probe considered the database compatible.
    /// </summary>
    public bool? DatabaseCompatible { get; init; }

    /// <summary>
    /// Compatibility warnings emitted by the database probe.
    /// </summary>
    public string[] DatabaseCompatibilityWarnings { get; init; } = [];

    /// <summary>
    /// Error message emitted by preflight or compatibility probes.
    /// </summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Typed comparison sections for the report artifact.
/// </summary>
public sealed record MigrationEvidenceComparison
{
    /// <summary>
    /// Capability comparison checks.
    /// </summary>
    public MigrationComparisonCheck[] Capability { get; init; } = [];

    /// <summary>
    /// Style comparison checks.
    /// </summary>
    public MigrationComparisonCheck[] Style { get; init; } = [];

    /// <summary>
    /// Data comparison checks.
    /// </summary>
    public MigrationComparisonCheck[] Data { get; init; } = [];

    /// <summary>
    /// Operational-readiness comparison checks.
    /// </summary>
    public MigrationComparisonCheck[] OperationalReadiness { get; init; } = [];
}

/// <summary>
/// Single structured comparison check within a report section.
/// </summary>
public sealed record MigrationComparisonCheck
{
    /// <summary>
    /// Stable check identifier.
    /// </summary>
    public required string CheckName { get; init; }

    /// <summary>
    /// Status of the check.
    /// </summary>
    public required MigrationEvidenceStatus Status { get; init; }

    /// <summary>
    /// Scope string describing the affected service or layer mapping.
    /// </summary>
    public required string Scope { get; init; }

    /// <summary>
    /// Short operator-facing summary.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Optional detailed notes.
    /// </summary>
    public string[] Notes { get; init; } = [];

    /// <summary>
    /// Structured observations captured for the check.
    /// </summary>
    public MigrationComparisonObservation[] Observations { get; init; } = [];
}

/// <summary>
/// Name/value observation captured for a comparison check.
/// </summary>
public sealed record MigrationComparisonObservation
{
    /// <summary>
    /// Observation name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Expected value.
    /// </summary>
    public string? Expected { get; init; }

    /// <summary>
    /// Actual value.
    /// </summary>
    public string? Actual { get; init; }
}

/// <summary>
/// Cutover-readiness summary derived from the report checklist.
/// </summary>
public sealed record MigrationEvidenceReadinessSummary
{
    /// <summary>
    /// Computed readiness state.
    /// </summary>
    public required MigrationReadinessState State { get; init; }

    /// <summary>
    /// Operator-facing blocking reasons.
    /// </summary>
    public string[] BlockingReasons { get; init; } = [];

    /// <summary>
    /// Operator-facing warnings.
    /// </summary>
    public string[] Warnings { get; init; } = [];

    /// <summary>
    /// Full readiness checklist.
    /// </summary>
    public CutoverChecklistItem[] Checklist { get; init; } = [];
}

/// <summary>
/// Single checklist item used for readiness evaluation.
/// </summary>
public sealed record CutoverChecklistItem
{
    /// <summary>
    /// Stable checklist identifier.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Requirement level for the checklist item.
    /// </summary>
    public required string RequirementLevel { get; init; }

    /// <summary>
    /// Evaluation status.
    /// </summary>
    public required MigrationEvidenceStatus Status { get; init; }

    /// <summary>
    /// Operator-facing summary for the item.
    /// </summary>
    public required string Summary { get; init; }
}

/// <summary>
/// Background-job progress for migration evidence generation.
/// </summary>
public sealed record MigrationEvidenceProgress : IOperationProgress, ICancellableOperationProgress
{
    /// <summary>
    /// Job identifier.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Migration provider being evaluated.
    /// </summary>
    public required MigrationEvidenceProvider Provider { get; init; }

    /// <summary>
    /// Source service URL.
    /// </summary>
    public required string SourceServiceUrl { get; init; }

    /// <summary>
    /// Target base URL.
    /// </summary>
    public required string TargetBaseUrl { get; init; }

    /// <summary>
    /// Target service name.
    /// </summary>
    public required string TargetServiceName { get; init; }

    /// <summary>
    /// Cutover profile.
    /// </summary>
    public required MigrationCutoverProfile CutoverProfile { get; init; }

    /// <summary>
    /// Current job status.
    /// </summary>
    public required MigrationEvidenceJobStatus Status { get; init; }

    /// <summary>
    /// Number of completed logical steps.
    /// </summary>
    public int CompletedSteps { get; init; }

    /// <summary>
    /// Number of total logical steps.
    /// </summary>
    public int TotalSteps { get; init; } = 4;

    /// <summary>
    /// Percent complete for the job.
    /// </summary>
    public double? PercentComplete => TotalSteps <= 0
        ? null
        : Math.Clamp((double)CompletedSteps / TotalSteps * 100d, 0d, 100d);

    /// <summary>
    /// When the job started.
    /// </summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// When the job completed.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Operation duration.
    /// </summary>
    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    /// <summary>
    /// Failure message when the job failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Warnings captured during job processing.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Current processing phase.
    /// </summary>
    public string? CurrentPhase { get; init; }

    /// <summary>
    /// Persisted report identifier when the job completed successfully.
    /// </summary>
    public Guid? ReportId { get; init; }

    /// <summary>
    /// Computed readiness state when available.
    /// </summary>
    public MigrationReadinessState? Readiness { get; init; }

    string IOperationProgress.OperationId => JobId;
    OperationType IOperationProgress.Type => OperationType.MigrationEvidence;
    OperationStatus IOperationProgress.Status => Status switch
    {
        MigrationEvidenceJobStatus.Queued => OperationStatus.Queued,
        MigrationEvidenceJobStatus.ResolvingSourceBaseline => OperationStatus.Processing,
        MigrationEvidenceJobStatus.ResolvingTargetSnapshot => OperationStatus.Processing,
        MigrationEvidenceJobStatus.Comparing => OperationStatus.Processing,
        MigrationEvidenceJobStatus.PersistingReport => OperationStatus.Processing,
        MigrationEvidenceJobStatus.Completed => OperationStatus.Completed,
        MigrationEvidenceJobStatus.Failed => OperationStatus.Failed,
        MigrationEvidenceJobStatus.Cancelled => OperationStatus.Cancelled,
        _ => OperationStatus.Processing
    };

    /// <inheritdoc />
    public IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase)
        => this with
        {
            Status = MigrationEvidenceJobStatus.Cancelled,
            CompletedAt = completedAt,
            CurrentPhase = currentPhase
        };

    /// <summary>
    /// Creates the initial queued progress record for a new evidence job.
    /// </summary>
    /// <param name="jobId">Job identifier.</param>
    /// <param name="request">Evidence request.</param>
    /// <returns>Queued progress record.</returns>
    public static MigrationEvidenceProgress CreateInitial(string jobId, MigrationEvidenceRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(request);

        return new MigrationEvidenceProgress
        {
            JobId = jobId,
            Provider = request.Provider,
            SourceServiceUrl = request.SourceServiceUrl,
            TargetBaseUrl = request.TargetBaseUrl,
            TargetServiceName = request.TargetServiceName,
            CutoverProfile = request.CutoverProfile,
            Status = MigrationEvidenceJobStatus.Queued,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Queued for processing"
        };
    }
}
