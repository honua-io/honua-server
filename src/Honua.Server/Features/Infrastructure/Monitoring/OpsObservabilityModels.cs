// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Consolidated operational-health snapshot returned by
/// <c>GET /api/v{version}/admin/observability/ops-health</c>. Composes the comprehensive
/// health-check roll-up, in-process serving latency, GP queue depth, alert-dispatch backlog,
/// deploy readiness, and database/cache utilization into one admin payload for the console.
/// </summary>
public sealed class OpsHealthSnapshotResponse
{
    /// <summary>Gets the UTC time the snapshot was produced.</summary>
    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Gets the overall status roll-up (<c>Healthy</c>/<c>Degraded</c>/<c>Unhealthy</c>) from the health report.</summary>
    [JsonPropertyName("overallStatus")]
    public required string OverallStatus { get; init; }

    /// <summary>Gets the comprehensive health-check roll-up, including the alert-dispatch check.</summary>
    [JsonPropertyName("health")]
    public required OpsHealthChecksView Health { get; init; }

    /// <summary>Gets the in-process serving-latency snapshot (per-protocol percentiles + error rate).</summary>
    [JsonPropertyName("servingLatency")]
    public required OpsServingLatencyView ServingLatency { get; init; }

    /// <summary>Gets the geoprocessing queue depth by status/backend plus the active total.</summary>
    [JsonPropertyName("geoprocessing")]
    public required OpsGpQueueView Geoprocessing { get; init; }

    /// <summary>Gets the alert-dispatch backlog / dead-letter summary.</summary>
    [JsonPropertyName("alertDispatch")]
    public required OpsAlertDispatchView AlertDispatch { get; init; }

    /// <summary>Gets the coordinated-deploy readiness summary and platform-release skew.</summary>
    [JsonPropertyName("deploy")]
    public required OpsDeployReadinessView Deploy { get; init; }

    /// <summary>Gets the database connection-pool utilization and cache/error metrics.</summary>
    [JsonPropertyName("database")]
    public required OpsDatabaseView Database { get; init; }
}

/// <summary>Comprehensive health-check roll-up view.</summary>
public sealed class OpsHealthChecksView
{
    /// <summary>Gets the aggregate health status string.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Gets the total health-check evaluation duration in milliseconds.</summary>
    [JsonPropertyName("totalDurationMs")]
    public required double TotalDurationMs { get; init; }

    /// <summary>Gets the individual health-check entries.</summary>
    [JsonPropertyName("entries")]
    public required IReadOnlyList<OpsHealthCheckEntryView> Entries { get; init; }
}

/// <summary>A single health-check entry.</summary>
public sealed class OpsHealthCheckEntryView
{
    /// <summary>Gets the registered health-check name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Gets the check status string.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Gets the operator-facing description, when the check provides one.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Gets the check duration in milliseconds.</summary>
    [JsonPropertyName("durationMs")]
    public required double DurationMs { get; init; }
}

/// <summary>In-process serving-latency snapshot view.</summary>
public sealed class OpsServingLatencyView
{
    /// <summary>Gets the rolling window length in seconds.</summary>
    [JsonPropertyName("windowSeconds")]
    public required double WindowSeconds { get; init; }

    /// <summary>Gets the per-protocol latency entries.</summary>
    [JsonPropertyName("protocols")]
    public required IReadOnlyList<OpsServingLatencyProtocolView> Protocols { get; init; }

    /// <summary>
    /// Gets the number of replicas merged into this view (the responding instance plus any peers whose recent
    /// rollup flushes were folded in), when cluster aggregation is active. <c>null</c> or <c>1</c> means the
    /// view reflects only the responding instance (single-node, or the rollup store is unavailable).
    /// </summary>
    [JsonPropertyName("clusterReplicaCount")]
    public int? ClusterReplicaCount { get; init; }
}

/// <summary>Per-protocol serving latency entry.</summary>
public sealed class OpsServingLatencyProtocolView
{
    /// <summary>Gets the protocol family.</summary>
    [JsonPropertyName("protocol")]
    public required string Protocol { get; init; }

    /// <summary>Gets the in-window request count.</summary>
    [JsonPropertyName("requestCount")]
    public required long RequestCount { get; init; }

    /// <summary>Gets the in-window server-error count (HTTP status &gt;= 500).</summary>
    [JsonPropertyName("errorCount")]
    public required long ErrorCount { get; init; }

    /// <summary>Gets the server-error rate over the window (0.0 to 1.0).</summary>
    [JsonPropertyName("errorRate")]
    public required double ErrorRate { get; init; }

    /// <summary>Gets the 50th-percentile duration in milliseconds.</summary>
    [JsonPropertyName("p50Ms")]
    public required double P50Ms { get; init; }

    /// <summary>Gets the 95th-percentile duration in milliseconds.</summary>
    [JsonPropertyName("p95Ms")]
    public required double P95Ms { get; init; }

    /// <summary>Gets the 99th-percentile duration in milliseconds.</summary>
    [JsonPropertyName("p99Ms")]
    public required double P99Ms { get; init; }

    /// <summary>Gets the maximum duration in milliseconds.</summary>
    [JsonPropertyName("maxMs")]
    public required double MaxMs { get; init; }
}

/// <summary>Geoprocessing queue-depth view.</summary>
public sealed class OpsGpQueueView
{
    /// <summary>Gets the total active jobs (queued + provisioning + running).</summary>
    [JsonPropertyName("totalActive")]
    public required int TotalActive { get; init; }

    /// <summary>Gets a value indicating whether GP queue telemetry is available (an execution-job store is registered).</summary>
    [JsonPropertyName("available")]
    public required bool Available { get; init; }

    /// <summary>Gets the active-job counts bucketed by status and backend.</summary>
    [JsonPropertyName("buckets")]
    public required IReadOnlyList<OpsGpQueueBucketView> Buckets { get; init; }
}

/// <summary>A single GP queue-depth bucket.</summary>
public sealed class OpsGpQueueBucketView
{
    /// <summary>Gets the non-terminal job status (Queued/Provisioning/Running).</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Gets the batch-compute backend identifier.</summary>
    [JsonPropertyName("backend")]
    public required string Backend { get; init; }

    /// <summary>Gets the active-job count in this status/backend bucket.</summary>
    [JsonPropertyName("count")]
    public required int Count { get; init; }
}

/// <summary>Alert-dispatch backlog / dead-letter summary view.</summary>
public sealed class OpsAlertDispatchView
{
    /// <summary>Gets a value indicating whether the dispatcher loop is running.</summary>
    [JsonPropertyName("dispatcherRunning")]
    public required bool DispatcherRunning { get; init; }

    /// <summary>Gets a value indicating whether the alert pipeline is enabled by configuration.</summary>
    [JsonPropertyName("dispatcherEnabled")]
    public required bool DispatcherEnabled { get; init; }

    /// <summary>Gets a value indicating whether the most recent dispatch pass failed to reach the backlog store.</summary>
    [JsonPropertyName("storagePollFailing")]
    public required bool StoragePollFailing { get; init; }

    /// <summary>Gets the timestamp of the most recent successful dispatch pass, when known.</summary>
    [JsonPropertyName("lastPollAt")]
    public DateTimeOffset? LastPollAt { get; init; }

    /// <summary>Gets the pending backlog count, when a backlog snapshot is available.</summary>
    [JsonPropertyName("pendingCount")]
    public long? PendingCount { get; init; }

    /// <summary>Gets the dead-lettered count, when a backlog snapshot is available.</summary>
    [JsonPropertyName("deadLetteredCount")]
    public long? DeadLetteredCount { get; init; }

    /// <summary>Gets the retrying count, when a backlog snapshot is available.</summary>
    [JsonPropertyName("retryingCount")]
    public long? RetryingCount { get; init; }

    /// <summary>Gets the age in whole seconds of the oldest active row, when one exists.</summary>
    [JsonPropertyName("oldestItemAgeSeconds")]
    public long? OldestItemAgeSeconds { get; init; }

    /// <summary>
    /// Gets privacy-safe active backlog health grouped by stable configured channel identifier.
    /// Recipient, payload, error, credential, and secret data are never included.
    /// </summary>
    [JsonPropertyName("channels")]
    public IReadOnlyList<OpsAlertDispatchChannelView> Channels { get; init; } = [];
}

/// <summary>Bounded active alert-dispatch health for one delivery channel.</summary>
public sealed class OpsAlertDispatchChannelView
{
    /// <summary>Gets the canonical channel identifier.</summary>
    [JsonPropertyName("channel")]
    public required string Channel { get; init; }

    /// <summary>Gets whether new delivery claims are paused for this channel.</summary>
    [JsonPropertyName("paused")]
    public required bool Paused { get; init; }

    /// <summary>Gets rows pending or currently being processed.</summary>
    [JsonPropertyName("pendingCount")]
    public required long PendingCount { get; init; }

    /// <summary>Gets failed rows scheduled for retry.</summary>
    [JsonPropertyName("retryingCount")]
    public required long RetryingCount { get; init; }

    /// <summary>Gets rows that exhausted their retry budget.</summary>
    [JsonPropertyName("deadLetteredCount")]
    public required long DeadLetteredCount { get; init; }

    /// <summary>Gets the age in whole seconds of the oldest non-delivered row.</summary>
    [JsonPropertyName("oldestItemAgeSeconds")]
    public required long OldestItemAgeSeconds { get; init; }
}

/// <summary>Coordinated-deploy readiness and platform-release skew summary view.</summary>
public sealed class OpsDeployReadinessView
{
    /// <summary>Gets the preflight status string (<c>ready</c>/<c>blocked</c>).</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Gets a value indicating whether the instance is ready for a coordinated deploy.</summary>
    [JsonPropertyName("readyForCoordinatedDeploy")]
    public required bool ReadyForCoordinatedDeploy { get; init; }

    /// <summary>Gets the count of pending (non-contract) migration scripts.</summary>
    [JsonPropertyName("pendingMigrationsCount")]
    public required int PendingMigrationsCount { get; init; }

    /// <summary>Gets the count of pending contract migration scripts (which hold coordinated-deploy readiness).</summary>
    [JsonPropertyName("pendingContractScriptsCount")]
    public required int PendingContractScriptsCount { get; init; }

    /// <summary>Gets the platform-release skew summary.</summary>
    [JsonPropertyName("platformRelease")]
    public required OpsPlatformReleaseView PlatformRelease { get; init; }
}

/// <summary>Platform-release co-versioning summary view.</summary>
public sealed class OpsPlatformReleaseView
{
    /// <summary>Gets the declared release version, when any.</summary>
    [JsonPropertyName("releaseVersion")]
    public string? ReleaseVersion { get; init; }

    /// <summary>Gets a value indicating whether a platform release is declared.</summary>
    [JsonPropertyName("releaseDeclared")]
    public required bool ReleaseDeclared { get; init; }

    /// <summary>Gets a value indicating whether all planes are co-versioned with the release.</summary>
    [JsonPropertyName("isCoVersioned")]
    public required bool IsCoVersioned { get; init; }

    /// <summary>Gets the identifiers of planes skewed from the declared release.</summary>
    [JsonPropertyName("skewedIds")]
    public required IReadOnlyList<string> SkewedIds { get; init; }
}

/// <summary>Database connection-pool + cache/error utilization view.</summary>
public sealed class OpsDatabaseView
{
    /// <summary>Gets the connection-pool utilization ratio (0.0 to 1.0), when available.</summary>
    [JsonPropertyName("connectionPoolUtilization")]
    public double? ConnectionPoolUtilization { get; init; }

    /// <summary>Gets a value indicating whether connection-pool utilization telemetry is available.</summary>
    [JsonPropertyName("hasConnectionPoolData")]
    public required bool HasConnectionPoolData { get; init; }

    /// <summary>Gets the number of active database connections.</summary>
    [JsonPropertyName("activeConnections")]
    public required int ActiveConnections { get; init; }

    /// <summary>Gets the connection acquisition timeout count.</summary>
    [JsonPropertyName("connectionAcquisitionTimeouts")]
    public required long ConnectionAcquisitionTimeouts { get; init; }

    /// <summary>Gets the connection acquisition failure count.</summary>
    [JsonPropertyName("connectionAcquisitionFailures")]
    public required long ConnectionAcquisitionFailures { get; init; }

    /// <summary>Gets the cache hit ratio (0.0 to 1.0).</summary>
    [JsonPropertyName("cacheHitRatio")]
    public required double CacheHitRatio { get; init; }

    /// <summary>Gets the live HTTP server error rate (0.0 to 1.0).</summary>
    [JsonPropertyName("errorRate")]
    public required double ErrorRate { get; init; }
}

/// <summary>
/// Response for <c>GET /api/v{version}/admin/observability/ops-health/history</c> (#2553). Returns a
/// cluster-aggregated (or per-replica) time series of serving latency and ops vitals at the requested
/// resolution over the requested window.
/// </summary>
/// <remarks>
/// This endpoint is also the reconnect gap-fill contract for realtime <c>ops-health</c> hub clients
/// (#2554): after a dropped connection a client backfills the missed interval by requesting the history
/// window rather than replaying a per-event cursor (there is no Last-Event-ID semantics).
/// </remarks>
public sealed class OpsHealthHistoryResponse
{
    /// <summary>Gets the UTC time the response was produced.</summary>
    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Gets the resolution label (<c>1m</c>/<c>5m</c>/<c>1h</c>).</summary>
    [JsonPropertyName("resolution")]
    public required string Resolution { get; init; }

    /// <summary>Gets the returned window length in seconds (clamped to the tier's retention cap).</summary>
    [JsonPropertyName("windowSeconds")]
    public required double WindowSeconds { get; init; }

    /// <summary>Gets the inclusive lower bound of the returned window.</summary>
    [JsonPropertyName("from")]
    public required DateTimeOffset From { get; init; }

    /// <summary>Gets the inclusive upper bound of the returned window.</summary>
    [JsonPropertyName("to")]
    public required DateTimeOffset To { get; init; }

    /// <summary>Gets a value indicating whether the series is broken down per replica rather than cluster-merged.</summary>
    [JsonPropertyName("perReplica")]
    public required bool PerReplica { get; init; }

    /// <summary>Gets the per-protocol serving-latency series.</summary>
    [JsonPropertyName("latency")]
    public required IReadOnlyList<OpsHealthHistoryLatencySeries> Latency { get; init; }

    /// <summary>Gets the ops-vitals series (one entry per bucket, or per bucket/replica when broken down).</summary>
    [JsonPropertyName("vitals")]
    public required IReadOnlyList<OpsHealthHistoryVitalsPoint> Vitals { get; init; }
}

/// <summary>A serving-latency series for one protocol (and optionally one replica).</summary>
public sealed class OpsHealthHistoryLatencySeries
{
    /// <summary>Gets the protocol family.</summary>
    [JsonPropertyName("protocol")]
    public required string Protocol { get; init; }

    /// <summary>Gets the replica identifier when broken down per replica; <c>null</c> for a cluster-merged series.</summary>
    [JsonPropertyName("replicaId")]
    public string? ReplicaId { get; init; }

    /// <summary>Gets the ordered time-series points.</summary>
    [JsonPropertyName("points")]
    public required IReadOnlyList<OpsHealthHistoryLatencyPoint> Points { get; init; }
}

/// <summary>A single serving-latency time-series point.</summary>
public sealed class OpsHealthHistoryLatencyPoint
{
    /// <summary>Gets the bucket start (UTC).</summary>
    [JsonPropertyName("bucketStart")]
    public required DateTimeOffset BucketStart { get; init; }

    /// <summary>Gets the request count.</summary>
    [JsonPropertyName("requestCount")]
    public required long RequestCount { get; init; }

    /// <summary>Gets the server-error count.</summary>
    [JsonPropertyName("errorCount")]
    public required long ErrorCount { get; init; }

    /// <summary>Gets the server-error rate (0.0 to 1.0).</summary>
    [JsonPropertyName("errorRate")]
    public required double ErrorRate { get; init; }

    /// <summary>Gets the 50th-percentile duration in milliseconds.</summary>
    [JsonPropertyName("p50Ms")]
    public required double P50Ms { get; init; }

    /// <summary>Gets the 95th-percentile duration in milliseconds.</summary>
    [JsonPropertyName("p95Ms")]
    public required double P95Ms { get; init; }

    /// <summary>Gets the 99th-percentile duration in milliseconds.</summary>
    [JsonPropertyName("p99Ms")]
    public required double P99Ms { get; init; }

    /// <summary>Gets the maximum duration in milliseconds.</summary>
    [JsonPropertyName("maxMs")]
    public required double MaxMs { get; init; }
}

/// <summary>A single ops-vitals time-series point.</summary>
public sealed class OpsHealthHistoryVitalsPoint
{
    /// <summary>Gets the bucket start (UTC).</summary>
    [JsonPropertyName("bucketStart")]
    public required DateTimeOffset BucketStart { get; init; }

    /// <summary>Gets the replica identifier when broken down per replica; <c>null</c> for a cluster-merged point.</summary>
    [JsonPropertyName("replicaId")]
    public string? ReplicaId { get; init; }

    /// <summary>Gets the overall health status.</summary>
    [JsonPropertyName("overallStatus")]
    public required string OverallStatus { get; init; }

    /// <summary>Gets the total active geoprocessing jobs.</summary>
    [JsonPropertyName("gpQueueTotal")]
    public required int GpQueueTotal { get; init; }

    /// <summary>
    /// Gets the GP queue depth broken down by status and backend, keyed
    /// <c>"&lt;status&gt;|&lt;backend&gt;"</c> (e.g. <c>"Queued|local"</c>) — the flattened form of the live
    /// snapshot's status×backend buckets. The GP queue is a GLOBAL view (every replica queries the same
    /// shared job store), so cluster-merged points dedup each key across replicas with a max — matching
    /// <c>gpQueueTotal</c> — and coarser resolutions carry the per-key peak within the bucket.
    /// </summary>
    [JsonPropertyName("gpQueueBreakdown")]
    public required IReadOnlyDictionary<string, int> GpQueueBreakdown { get; init; }

    /// <summary>Gets the alert-dispatch pending backlog, when available.</summary>
    [JsonPropertyName("alertPending")]
    public long? AlertPending { get; init; }

    /// <summary>Gets the alert-dispatch dead-lettered count, when available.</summary>
    [JsonPropertyName("alertDeadLettered")]
    public long? AlertDeadLettered { get; init; }

    /// <summary>Gets the database connection-pool utilization ratio, when available.</summary>
    [JsonPropertyName("dbPoolUtilization")]
    public double? DbPoolUtilization { get; init; }

    /// <summary>Gets the number of active database connections.</summary>
    [JsonPropertyName("dbActiveConnections")]
    public required int DbActiveConnections { get; init; }

    /// <summary>Gets the cache hit ratio (0.0 to 1.0).</summary>
    [JsonPropertyName("cacheHitRatio")]
    public required double CacheHitRatio { get; init; }

    /// <summary>Gets the live HTTP server error rate (0.0 to 1.0).</summary>
    [JsonPropertyName("errorRate")]
    public required double ErrorRate { get; init; }
}

/// <summary>Response for <c>GET /api/v{version}/admin/observability/findings</c>.</summary>
public sealed class OpsFindingsListResponse
{
    /// <summary>Gets the UTC time the findings were evaluated.</summary>
    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Gets the active findings, ordered by descending severity.</summary>
    [JsonPropertyName("findings")]
    public required IReadOnlyList<OpsFindingView> Findings { get; init; }
}

/// <summary>Wire view of a single deterministic ops finding.</summary>
public sealed class OpsFindingView
{
    /// <summary>Gets the deterministic finding identifier (used to propose its action).</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Gets the kebab-case rule identifier.</summary>
    [JsonPropertyName("rule")]
    public required string Rule { get; init; }

    /// <summary>Gets the severity (<c>Info</c>/<c>Warning</c>/<c>Critical</c>).</summary>
    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    /// <summary>Gets the short title.</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>Gets the plain-language explanation.</summary>
    [JsonPropertyName("explanation")]
    public required string Explanation { get; init; }

    /// <summary>Gets the detection (evaluation) time.</summary>
    [JsonPropertyName("detectedAt")]
    public required DateTimeOffset DetectedAt { get; init; }

    /// <summary>Gets the subject identifiers.</summary>
    [JsonPropertyName("subject")]
    public required OpsFindingSubjectView Subject { get; init; }

    /// <summary>Gets the supporting evidence references.</summary>
    [JsonPropertyName("evidenceRefs")]
    public required IReadOnlyList<string> EvidenceRefs { get; init; }

    /// <summary>Gets the recommended action summary, or <c>null</c> when the finding is informational.</summary>
    [JsonPropertyName("recommendedAction")]
    public OpsFindingActionView? RecommendedAction { get; init; }
}

/// <summary>Subject identifiers a finding pins to.</summary>
public sealed class OpsFindingSubjectView
{
    /// <summary>Gets the deploy target identifier, when applicable.</summary>
    [JsonPropertyName("targetId")]
    public string? TargetId { get; init; }

    /// <summary>Gets the execution workload identifier, when applicable.</summary>
    [JsonPropertyName("workloadId")]
    public string? WorkloadId { get; init; }

    /// <summary>Gets the notification channel identifier, when applicable.</summary>
    [JsonPropertyName("channel")]
    public string? Channel { get; init; }

    /// <summary>Gets the workflow/deploy operation identifier, when applicable.</summary>
    [JsonPropertyName("operationId")]
    public string? OperationId { get; init; }

    /// <summary>Gets the platform release version, when applicable.</summary>
    [JsonPropertyName("releaseVersion")]
    public string? ReleaseVersion { get; init; }

    /// <summary>Gets the GIS protocol family, when applicable.</summary>
    [JsonPropertyName("protocol")]
    public string? Protocol { get; init; }
}

/// <summary>
/// Wire view of a finding's recommended action. The opaque execution payload is intentionally not
/// surfaced — the console proposes the action by finding id, and the server materializes the payload.
/// </summary>
public sealed class OpsFindingActionView
{
    /// <summary>Gets the operation class the action routes through (<c>AdminConfigChange</c>/<c>Deploy</c>/<c>MetadataRelease</c>/<c>Seed</c>).</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>Gets the plain-language action summary.</summary>
    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    /// <summary>Gets the operator-facing reason recorded on the proposal.</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    /// <summary>Gets a value indicating whether the action is marked safe for autonomous apply.</summary>
    [JsonPropertyName("autoSafe")]
    public required bool AutoSafe { get; init; }

    /// <summary>Gets the bounded blast-radius estimate used by autonomy guardrails.</summary>
    [JsonPropertyName("blastRadius")]
    public required int BlastRadius { get; init; }
}

/// <summary>Response for <c>POST /api/v{version}/admin/observability/findings/{findingId}/propose</c>.</summary>
public sealed class OpsFindingProposeResponse
{
    /// <summary>Gets the finding identifier that was proposed.</summary>
    [JsonPropertyName("findingId")]
    public required string FindingId { get; init; }

    /// <summary>Gets the proposal outcome status (<c>Executed</c>/<c>ProposalCreated</c>/<c>Blocked</c>/<c>NotSupported</c>).</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Gets the created proposal identifier when the gateway required approval.</summary>
    [JsonPropertyName("proposalId")]
    public string? ProposalId { get; init; }

    /// <summary>Gets the executed operation identifier when the gateway executed directly.</summary>
    [JsonPropertyName("executionOperationId")]
    public string? ExecutionOperationId { get; init; }

    /// <summary>Gets an operator-facing message describing the outcome, when available.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>Response for <c>GET /api/v{version}/admin/observability/autonomy/policies</c>.</summary>
public sealed class OpsAutonomyPolicyListResponse
{
    /// <summary>Gets the UTC time the policy list was produced.</summary>
    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Gets the durable per-rule autonomy policies.</summary>
    [JsonPropertyName("policies")]
    public required IReadOnlyList<OpsAutonomyPolicyResponse> Policies { get; init; }
}

/// <summary>Wire response for one ops-finding autonomy policy.</summary>
public sealed class OpsAutonomyPolicyResponse
{
    /// <summary>Gets the kebab-case ops-finding rule identifier.</summary>
    [JsonPropertyName("rule")]
    public required string Rule { get; init; }

    /// <summary>
    /// Gets a value indicating whether this effective policy comes from an explicit durable override.
    /// False means the values are projected from current configuration/defaults.
    /// </summary>
    [JsonPropertyName("isPersisted")]
    public required bool IsPersisted { get; init; }

    /// <summary>Gets the effective autonomy mode (<c>ProposeOnly</c> or <c>AutoApply</c>).</summary>
    [JsonPropertyName("mode")]
    public required string Mode { get; init; }

    /// <summary>Gets the maximum autonomous actions allowed in the rolling window.</summary>
    [JsonPropertyName("maxAutoActionsPerWindow")]
    public required int MaxAutoActionsPerWindow { get; init; }

    /// <summary>Gets the rolling rate-limit window in seconds.</summary>
    [JsonPropertyName("windowSeconds")]
    public required int WindowSeconds { get; init; }

    /// <summary>Gets the maximum allowed blast-radius value.</summary>
    [JsonPropertyName("maxBlastRadius")]
    public required int MaxBlastRadius { get; init; }

    /// <summary>Gets the UTC time the durable policy was last updated.</summary>
    [JsonPropertyName("updatedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Gets the actor that last updated the durable policy.</summary>
    [JsonPropertyName("updatedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? UpdatedBy { get; init; }

    /// <summary>Gets the aggregate outcome counters for the rule.</summary>
    [JsonPropertyName("trackRecord")]
    public required OpsAutonomyTrackRecordResponse TrackRecord { get; init; }
}

/// <summary>Wire response for global ops-finding autonomy settings.</summary>
public sealed class OpsAutonomySettingsResponse
{
    /// <summary>Gets a value indicating whether all autonomous remediation is disabled.</summary>
    [JsonPropertyName("killSwitchEnabled")]
    public required bool KillSwitchEnabled { get; init; }

    /// <summary>Gets the UTC time the settings were last updated.</summary>
    [JsonPropertyName("updatedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Gets the actor that last updated the settings.</summary>
    [JsonPropertyName("updatedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? UpdatedBy { get; init; }
}

/// <summary>Wire response for aggregate per-rule autonomy counters.</summary>
public sealed class OpsAutonomyTrackRecordResponse
{
    /// <summary>Gets the number of proposals raised for the rule.</summary>
    [JsonPropertyName("proposalsRaised")]
    public required long ProposalsRaised { get; init; }

    /// <summary>Gets the number of proposals approved for the rule.</summary>
    [JsonPropertyName("proposalsApproved")]
    public required long ProposalsApproved { get; init; }

    /// <summary>Gets the number of proposals rejected for the rule.</summary>
    [JsonPropertyName("proposalsRejected")]
    public required long ProposalsRejected { get; init; }

    /// <summary>Gets the number of autonomous actions that succeeded.</summary>
    [JsonPropertyName("autoApplied")]
    public required long AutoApplied { get; init; }

    /// <summary>Gets the number of autonomous actions that rolled back.</summary>
    [JsonPropertyName("rolledBack")]
    public required long RolledBack { get; init; }

    /// <summary>Gets the number of autonomous actions that failed.</summary>
    [JsonPropertyName("failed")]
    public required long Failed { get; init; }

    /// <summary>Gets the first known activity timestamp.</summary>
    [JsonPropertyName("firstActivityAt")]
    public DateTimeOffset? FirstActivityAt { get; init; }

    /// <summary>Gets the most recent known activity timestamp.</summary>
    [JsonPropertyName("lastActivityAt")]
    public DateTimeOffset? LastActivityAt { get; init; }
}

/// <summary>Request body for upserting a per-rule autonomy policy.</summary>
public sealed class OpsAutonomyPolicyUpdateRequest
{
    /// <summary>Gets the requested mode (<c>ProposeOnly</c> or <c>AutoApply</c>).</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    /// <summary>Gets the maximum autonomous actions allowed in the rolling window.</summary>
    [JsonPropertyName("maxAutoActionsPerWindow")]
    public int? MaxAutoActionsPerWindow { get; init; }

    /// <summary>Gets the rolling rate-limit window in seconds.</summary>
    [JsonPropertyName("windowSeconds")]
    public int? WindowSeconds { get; init; }

    /// <summary>Gets the maximum allowed blast-radius value.</summary>
    [JsonPropertyName("maxBlastRadius")]
    public int? MaxBlastRadius { get; init; }

    /// <summary>Gets an optional operator reason persisted with the audit event.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>Request body for changing global autonomy settings.</summary>
public sealed class OpsAutonomySettingsUpdateRequest
{
    /// <summary>Gets a value indicating whether all autonomous remediation should be disabled.</summary>
    [JsonPropertyName("killSwitchEnabled")]
    public bool? KillSwitchEnabled { get; init; }

    /// <summary>Gets an optional operator reason persisted with the audit event.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}
