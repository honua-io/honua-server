// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Infrastructure.Monitoring;

public sealed class OpsHealthSnapshotResponse
{
    [JsonPropertyName("evidencePosture")]
    public EvidencePosture? EvidencePosture { get; init; }

    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("overallStatus")]
    public required string OverallStatus { get; init; }

    [JsonPropertyName("health")]
    public required OpsHealthChecksView Health { get; init; }

    [JsonPropertyName("servingLatency")]
    public required OpsServingLatencyView ServingLatency { get; init; }

    [JsonPropertyName("geoprocessing")]
    public required OpsGpQueueView Geoprocessing { get; init; }

    [JsonPropertyName("alertDispatch")]
    public required OpsAlertDispatchView AlertDispatch { get; init; }

    [JsonPropertyName("deploy")]
    public required OpsDeployReadinessView Deploy { get; init; }

    [JsonPropertyName("managedTargets")]
    public IReadOnlyList<OpsManagedTargetHealthView> ManagedTargets { get; init; } = [];

    [JsonPropertyName("database")]
    public required OpsDatabaseView Database { get; init; }
}

public sealed class OpsHealthChecksView
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("totalDurationMs")]
    public required double TotalDurationMs { get; init; }

    [JsonPropertyName("entries")]
    public required IReadOnlyList<OpsHealthCheckEntryView> Entries { get; init; }
}

public sealed class OpsHealthCheckEntryView
{
    [JsonPropertyName("outputStoreAttestation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? OutputStoreAttestation { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("durationMs")]
    public required double DurationMs { get; init; }
}

public sealed class OpsServingLatencyView
{
    [JsonPropertyName("windowSeconds")]
    public required double WindowSeconds { get; init; }

    [JsonPropertyName("protocols")]
    public required IReadOnlyList<OpsServingLatencyProtocolView> Protocols { get; init; }

    [JsonPropertyName("clusterReplicaCount")]
    public int? ClusterReplicaCount { get; init; }
}

public sealed class OpsServingLatencyProtocolView
{
    [JsonPropertyName("protocol")]
    public required string Protocol { get; init; }

    [JsonPropertyName("requestCount")]
    public required long RequestCount { get; init; }

    [JsonPropertyName("errorCount")]
    public required long ErrorCount { get; init; }

    [JsonPropertyName("errorRate")]
    public required double ErrorRate { get; init; }

    [JsonPropertyName("p50Ms")]
    public required double P50Ms { get; init; }

    [JsonPropertyName("p95Ms")]
    public required double P95Ms { get; init; }

    [JsonPropertyName("p99Ms")]
    public required double P99Ms { get; init; }

    [JsonPropertyName("maxMs")]
    public required double MaxMs { get; init; }
}

public sealed class OpsGpQueueView
{
    [JsonPropertyName("totalActive")]
    public required int TotalActive { get; init; }

    [JsonPropertyName("available")]
    public required bool Available { get; init; }

    [JsonPropertyName("buckets")]
    public required IReadOnlyList<OpsGpQueueBucketView> Buckets { get; init; }
}

public sealed class OpsGpQueueBucketView
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("backend")]
    public required string Backend { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }
}

public sealed class OpsAlertDispatchView
{
    [JsonPropertyName("dispatcherRunning")]
    public required bool DispatcherRunning { get; init; }

    [JsonPropertyName("dispatcherEnabled")]
    public required bool DispatcherEnabled { get; init; }

    [JsonPropertyName("storagePollFailing")]
    public required bool StoragePollFailing { get; init; }

    [JsonPropertyName("lastPollAt")]
    public DateTimeOffset? LastPollAt { get; init; }

    [JsonPropertyName("backlogObservedAt")]
    public DateTimeOffset? BacklogObservedAt { get; init; }

    [JsonPropertyName("pendingCount")]
    public long? PendingCount { get; init; }

    [JsonPropertyName("deadLetteredCount")]
    public long? DeadLetteredCount { get; init; }

    [JsonPropertyName("retryingCount")]
    public long? RetryingCount { get; init; }

    [JsonPropertyName("oldestItemAgeSeconds")]
    public long? OldestItemAgeSeconds { get; init; }

    // Recipient, payload, error, and secret data are never included.
    [JsonPropertyName("channels")]
    public IReadOnlyList<OpsAlertDispatchChannelView> Channels { get; init; } = [];
}

public sealed class OpsAlertDispatchChannelView
{
    [JsonPropertyName("channel")]
    public required string Channel { get; init; }

    [JsonPropertyName("paused")]
    public required bool Paused { get; init; }

    [JsonPropertyName("pendingCount")]
    public required long PendingCount { get; init; }

    [JsonPropertyName("retryingCount")]
    public required long RetryingCount { get; init; }

    [JsonPropertyName("deadLetteredCount")]
    public required long DeadLetteredCount { get; init; }

    [JsonPropertyName("oldestItemAgeSeconds")]
    public required long OldestItemAgeSeconds { get; init; }
}

public sealed class OpsDeployReadinessView
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("readyForCoordinatedDeploy")]
    public required bool ReadyForCoordinatedDeploy { get; init; }

    [JsonPropertyName("pendingMigrationsCount")]
    public required int PendingMigrationsCount { get; init; }

    [JsonPropertyName("pendingContractScriptsCount")]
    public required int PendingContractScriptsCount { get; init; }

    [JsonPropertyName("platformRelease")]
    public required OpsPlatformReleaseView PlatformRelease { get; init; }
}

public sealed class OpsPlatformReleaseView
{
    [JsonPropertyName("releaseVersion")]
    public string? ReleaseVersion { get; init; }

    [JsonPropertyName("releaseDeclared")]
    public required bool ReleaseDeclared { get; init; }

    [JsonPropertyName("isCoVersioned")]
    public required bool IsCoVersioned { get; init; }

    [JsonPropertyName("skewedIds")]
    public required IReadOnlyList<string> SkewedIds { get; init; }
}

public sealed class OpsDatabaseView
{
    [JsonPropertyName("connectionPoolUtilization")]
    public double? ConnectionPoolUtilization { get; init; }

    [JsonPropertyName("hasConnectionPoolData")]
    public required bool HasConnectionPoolData { get; init; }

    [JsonPropertyName("activeConnections")]
    public required int ActiveConnections { get; init; }

    [JsonPropertyName("connectionAcquisitionTimeouts")]
    public required long ConnectionAcquisitionTimeouts { get; init; }

    [JsonPropertyName("connectionAcquisitionFailures")]
    public required long ConnectionAcquisitionFailures { get; init; }

    [JsonPropertyName("cacheHitRatio")]
    public required double CacheHitRatio { get; init; }

    [JsonPropertyName("errorRate")]
    public required double ErrorRate { get; init; }
}

public sealed class OpsHealthHistoryResponse
{
    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("resolution")]
    public required string Resolution { get; init; }

    [JsonPropertyName("windowSeconds")]
    public required double WindowSeconds { get; init; }

    [JsonPropertyName("from")]
    public required DateTimeOffset From { get; init; }

    [JsonPropertyName("to")]
    public required DateTimeOffset To { get; init; }

    [JsonPropertyName("perReplica")]
    public required bool PerReplica { get; init; }

    [JsonPropertyName("latency")]
    public required IReadOnlyList<OpsHealthHistoryLatencySeries> Latency { get; init; }

    [JsonPropertyName("vitals")]
    public required IReadOnlyList<OpsHealthHistoryVitalsPoint> Vitals { get; init; }
}

public sealed class OpsHealthHistoryLatencySeries
{
    [JsonPropertyName("protocol")]
    public required string Protocol { get; init; }

    [JsonPropertyName("replicaId")]
    public string? ReplicaId { get; init; }

    [JsonPropertyName("points")]
    public required IReadOnlyList<OpsHealthHistoryLatencyPoint> Points { get; init; }
}

public sealed class OpsHealthHistoryLatencyPoint
{
    [JsonPropertyName("bucketStart")]
    public required DateTimeOffset BucketStart { get; init; }

    [JsonPropertyName("requestCount")]
    public required long RequestCount { get; init; }

    [JsonPropertyName("errorCount")]
    public required long ErrorCount { get; init; }

    [JsonPropertyName("errorRate")]
    public required double ErrorRate { get; init; }

    [JsonPropertyName("p50Ms")]
    public required double P50Ms { get; init; }

    [JsonPropertyName("p95Ms")]
    public required double P95Ms { get; init; }

    [JsonPropertyName("p99Ms")]
    public required double P99Ms { get; init; }

    [JsonPropertyName("maxMs")]
    public required double MaxMs { get; init; }
}

public sealed class OpsHealthHistoryVitalsPoint
{
    [JsonPropertyName("bucketStart")]
    public required DateTimeOffset BucketStart { get; init; }

    [JsonPropertyName("replicaId")]
    public string? ReplicaId { get; init; }

    [JsonPropertyName("overallStatus")]
    public required string OverallStatus { get; init; }

    [JsonPropertyName("gpQueueTotal")]
    public required int GpQueueTotal { get; init; }

    [JsonPropertyName("gpQueueBreakdown")]
    public required IReadOnlyDictionary<string, int> GpQueueBreakdown { get; init; }

    [JsonPropertyName("alertPending")]
    public long? AlertPending { get; init; }

    [JsonPropertyName("alertDeadLettered")]
    public long? AlertDeadLettered { get; init; }

    [JsonPropertyName("dbPoolUtilization")]
    public double? DbPoolUtilization { get; init; }

    [JsonPropertyName("dbActiveConnections")]
    public required int DbActiveConnections { get; init; }

    [JsonPropertyName("cacheHitRatio")]
    public required double CacheHitRatio { get; init; }

    [JsonPropertyName("errorRate")]
    public required double ErrorRate { get; init; }
}

public sealed class OpsFindingsListResponse
{
    [JsonPropertyName("evidencePosture")]
    public EvidencePosture? EvidencePosture { get; init; }

    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("findings")]
    public required IReadOnlyList<OpsFindingView> Findings { get; init; }
}

public sealed class OpsFindingView
{
    [JsonPropertyName("requiredSourceIds")]
    public IReadOnlyList<string> RequiredSourceIds { get; init; } = [EvidencePostureVocabulary.SourceIds.Findings];

    [JsonPropertyName("observationWindow")]
    public EvidenceSourceCoverage? ObservationWindow { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("rule")]
    public required string Rule { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("explanation")]
    public required string Explanation { get; init; }

    [JsonPropertyName("detectedAt")]
    public required DateTimeOffset DetectedAt { get; init; }

    [JsonPropertyName("subject")]
    public required OpsFindingSubjectView Subject { get; init; }

    [JsonPropertyName("evidenceRefs")]
    public required IReadOnlyList<string> EvidenceRefs { get; init; }

    [JsonPropertyName("recommendedAction")]
    public OpsFindingActionView? RecommendedAction { get; init; }
}

public sealed class OpsFindingSubjectView
{
    [JsonPropertyName("targetId")]
    public string? TargetId { get; init; }

    [JsonPropertyName("workloadId")]
    public string? WorkloadId { get; init; }

    [JsonPropertyName("channel")]
    public string? Channel { get; init; }

    [JsonPropertyName("operationId")]
    public string? OperationId { get; init; }

    [JsonPropertyName("releaseVersion")]
    public string? ReleaseVersion { get; init; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; init; }
}

public sealed class OpsFindingActionView
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("autoSafe")]
    public required bool AutoSafe { get; init; }

    [JsonPropertyName("blastRadius")]
    public required int BlastRadius { get; init; }
}

public sealed class OpsFindingProposeResponse
{
    [JsonPropertyName("findingId")]
    public required string FindingId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("proposalId")]
    public string? ProposalId { get; init; }

    [JsonPropertyName("executionOperationId")]
    public string? ExecutionOperationId { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("rootProvisioningOperationId")]
    public string? RootProvisioningOperationId { get; init; }
}

public sealed class OpsFindingProposeRequest
{
    [JsonPropertyName("rootProvisioningOperationId")]
    public string? RootProvisioningOperationId { get; init; }
}

public sealed class OpsAutonomyPolicyListResponse
{
    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("policies")]
    public required IReadOnlyList<OpsAutonomyPolicyResponse> Policies { get; init; }
}

public sealed class OpsAutonomyPolicyResponse
{
    [JsonPropertyName("rule")]
    public required string Rule { get; init; }

    [JsonPropertyName("isPersisted")]
    public required bool IsPersisted { get; init; }

    [JsonPropertyName("mode")]
    public required string Mode { get; init; }

    [JsonPropertyName("maxAutoActionsPerWindow")]
    public required int MaxAutoActionsPerWindow { get; init; }

    [JsonPropertyName("windowSeconds")]
    public required int WindowSeconds { get; init; }

    [JsonPropertyName("maxBlastRadius")]
    public required int MaxBlastRadius { get; init; }

    [JsonPropertyName("updatedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("updatedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? UpdatedBy { get; init; }

    [JsonPropertyName("trackRecord")]
    public required OpsAutonomyTrackRecordResponse TrackRecord { get; init; }
}

public sealed class OpsAutonomySettingsResponse
{
    [JsonPropertyName("killSwitchEnabled")]
    public required bool KillSwitchEnabled { get; init; }

    [JsonPropertyName("updatedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("updatedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? UpdatedBy { get; init; }
}

public sealed class OpsAutonomyTrackRecordResponse
{
    [JsonPropertyName("proposalsRaised")]
    public required long ProposalsRaised { get; init; }

    [JsonPropertyName("proposalsApproved")]
    public required long ProposalsApproved { get; init; }

    [JsonPropertyName("proposalsRejected")]
    public required long ProposalsRejected { get; init; }

    [JsonPropertyName("autoApplied")]
    public required long AutoApplied { get; init; }

    [JsonPropertyName("rolledBack")]
    public required long RolledBack { get; init; }

    [JsonPropertyName("failed")]
    public required long Failed { get; init; }

    [JsonPropertyName("firstActivityAt")]
    public DateTimeOffset? FirstActivityAt { get; init; }

    [JsonPropertyName("lastActivityAt")]
    public DateTimeOffset? LastActivityAt { get; init; }
}

public sealed class OpsAutonomyPolicyUpdateRequest
{
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("maxAutoActionsPerWindow")]
    public int? MaxAutoActionsPerWindow { get; init; }

    [JsonPropertyName("windowSeconds")]
    public int? WindowSeconds { get; init; }

    [JsonPropertyName("maxBlastRadius")]
    public int? MaxBlastRadius { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed class OpsAutonomySettingsUpdateRequest
{
    [JsonPropertyName("killSwitchEnabled")]
    public bool? KillSwitchEnabled { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed class OpsManagedTargetHealthView
{
    // Internal correlation only; the public health view does not expose container identities.
    [JsonIgnore]
    internal string? ServingIncarnation { get; init; }

    [JsonIgnore]
    internal IReadOnlyDictionary<string, string> ServiceIncarnations { get; init; } = new Dictionary<string, string>();

    public required string TargetId { get; init; }
    public bool Available { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public required string Status { get; init; }
    public string? RunningImage { get; init; }
    public string? ImageId { get; init; }
    public string? ServerVersion { get; init; }
    public IReadOnlyList<OpsHealthCheckEntryView> Services { get; init; } = [];
}
