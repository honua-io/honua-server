// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Operations.Status;

/// <summary>
/// Server-authoritative aggregated operational status returned by
/// <c>GET /api/v{version}/operate/status</c>. One request yields a server-computed overall verdict
/// plus per-domain rollups (deploys, jobs, alerts, migrations, findings, telemetry backends) and,
/// when configured, an availability SLO / error-budget snapshot — so a copilot no longer has to
/// stitch ~8 endpoints and invent its own "is the system healthy" verdict. The verdict logic lives
/// server-side (see <see cref="OperateStatusVerdictEvaluator"/>); each domain carries a
/// <c>source</c> hint so a caller can drill down to the authoritative endpoint.
/// </summary>
public sealed class OperateStatusResponse
{
    /// <summary>Gets the payload schema version (semantic; additive changes bump the minor).</summary>
    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; init; }

    /// <summary>Gets the UTC time the status was computed.</summary>
    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Gets the server-computed overall verdict: <c>healthy</c>, <c>degraded</c>, or <c>unhealthy</c>.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>
    /// Gets the documented, machine-readable reasons that drove the verdict (for example
    /// <c>health-rollup:Degraded</c>, <c>critical-finding:deploy-manual-intervention</c>,
    /// <c>deploy-parked</c>). Empty when the verdict is <c>healthy</c>.
    /// </summary>
    [JsonPropertyName("reasons")]
    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>Gets the per-domain operational rollups.</summary>
    [JsonPropertyName("domains")]
    public required OperateStatusDomains Domains { get; init; }

    /// <summary>Gets the availability SLO / error-budget snapshot (or its explicit not-configured state).</summary>
    [JsonPropertyName("slo")]
    public required OperateSloView Slo { get; init; }
}

/// <summary>Per-domain operational rollups composing the aggregated status.</summary>
public sealed class OperateStatusDomains
{
    /// <summary>Gets the deploy control-plane rollup.</summary>
    [JsonPropertyName("deploys")]
    public required OperateDeploysView Deploys { get; init; }

    /// <summary>Gets the durable jobs / batch-compute rollup.</summary>
    [JsonPropertyName("jobs")]
    public required OperateJobsView Jobs { get; init; }

    /// <summary>Gets the alert dispatch / outbox rollup.</summary>
    [JsonPropertyName("alerts")]
    public required OperateAlertsView Alerts { get; init; }

    /// <summary>Gets the schema-migration rollup.</summary>
    [JsonPropertyName("migrations")]
    public required OperateMigrationsView Migrations { get; init; }

    /// <summary>Gets the deterministic ops-findings rollup.</summary>
    [JsonPropertyName("findings")]
    public required OperateFindingsView Findings { get; init; }

    /// <summary>Gets the telemetry-backend posture rollup.</summary>
    [JsonPropertyName("telemetryBackends")]
    public required OperateTelemetryBackendsView TelemetryBackends { get; init; }
}

/// <summary>Deploy control-plane rollup: active/parked/awaiting-approval counts and last outcome.</summary>
public sealed class OperateDeploysView
{
    /// <summary>Gets the drill-down source endpoint hint.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>Gets a value indicating whether the durable workflow-operation store is wired (Redis control plane).</summary>
    [JsonPropertyName("available")]
    public required bool Available { get; init; }

    /// <summary>Gets the count of in-flight deploy operations (Submitted/Reconciling/RollbackRequested).</summary>
    [JsonPropertyName("active")]
    public required int Active { get; init; }

    /// <summary>Gets the count of deploy operations parked in ManualInterventionRequired.</summary>
    [JsonPropertyName("parked")]
    public required int Parked { get; init; }

    /// <summary>Gets the count of deploy operations blocked awaiting an approval step.</summary>
    [JsonPropertyName("awaitingApproval")]
    public required int AwaitingApproval { get; init; }

    /// <summary>Gets the active deploy-operation counts by workflow status.</summary>
    [JsonPropertyName("byStatus")]
    public required IReadOnlyDictionary<string, int> ByStatus { get; init; }

    /// <summary>Gets the status of the most-recently-updated active operation, or <c>null</c> when none are active.</summary>
    [JsonPropertyName("lastOutcome")]
    public string? LastOutcome { get; init; }
}

/// <summary>Durable jobs / batch-compute rollup: queued/running counts, backend kinds, substrate posture.</summary>
public sealed class OperateJobsView
{
    /// <summary>Gets the drill-down source endpoint hint.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>Gets a value indicating whether the durable execution-job store is wired.</summary>
    [JsonPropertyName("available")]
    public required bool Available { get; init; }

    /// <summary>Gets the total active jobs (queued + provisioning + running).</summary>
    [JsonPropertyName("totalActive")]
    public required int TotalActive { get; init; }

    /// <summary>Gets the count of queued jobs.</summary>
    [JsonPropertyName("queued")]
    public required int Queued { get; init; }

    /// <summary>Gets the count of provisioning/running jobs.</summary>
    [JsonPropertyName("running")]
    public required int Running { get; init; }

    /// <summary>Gets the distinct batch-compute backend kinds observed in the active queue (e.g. <c>local</c>, <c>honua-aws-batch</c>).</summary>
    [JsonPropertyName("backends")]
    public required IReadOnlyList<string> Backends { get; init; }

    /// <summary>Gets the active-job counts bucketed by status and backend.</summary>
    [JsonPropertyName("byStatusBackend")]
    public required IReadOnlyList<OperateJobBucketView> ByStatusBackend { get; init; }

    /// <summary>Gets the local-backend substrate-compatibility posture.</summary>
    [JsonPropertyName("substrate")]
    public required OperateSubstrateView Substrate { get; init; }
}

/// <summary>A single active-job status/backend bucket.</summary>
public sealed class OperateJobBucketView
{
    /// <summary>Gets the non-terminal job status (Queued/Provisioning/Running).</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Gets the batch-compute backend identifier.</summary>
    [JsonPropertyName("backend")]
    public required string Backend { get; init; }

    /// <summary>Gets the active-job count in this bucket.</summary>
    [JsonPropertyName("count")]
    public required int Count { get; init; }
}

/// <summary>Local batch-compute substrate-compatibility posture.</summary>
public sealed class OperateSubstrateView
{
    /// <summary>
    /// Gets a value indicating whether the substrate compatibility of the local backends has been
    /// evaluated. v1 reports <c>false</c> (not evaluated) until the substrate-profile resolver lands
    /// on this branch; the field is present so the payload shape is stable across that follow-up.
    /// </summary>
    [JsonPropertyName("evaluated")]
    public required bool Evaluated { get; init; }

    /// <summary>Gets the resolved substrate profile when evaluated (<c>SingleHost</c>/<c>MultiNode</c>/<c>Serverless</c>), otherwise <c>null</c>.</summary>
    [JsonPropertyName("profile")]
    public string? Profile { get; init; }

    /// <summary>Gets a value indicating whether the local batch-compute backends are compatible with the substrate, when evaluated.</summary>
    [JsonPropertyName("compatible")]
    public bool? Compatible { get; init; }

    /// <summary>Gets an operator-facing note about the substrate posture.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

/// <summary>Alert dispatch / outbox rollup: open/dead-lettered counts, dispatcher state, channel circuits.</summary>
public sealed class OperateAlertsView
{
    /// <summary>Gets the drill-down source endpoint hint.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>Gets a value indicating whether the alert pipeline is enabled by configuration.</summary>
    [JsonPropertyName("enabled")]
    public required bool Enabled { get; init; }

    /// <summary>Gets a value indicating whether the dispatcher loop is running.</summary>
    [JsonPropertyName("dispatcherRunning")]
    public required bool DispatcherRunning { get; init; }

    /// <summary>Gets a value indicating whether the most recent dispatch pass failed to reach the backlog store.</summary>
    [JsonPropertyName("storagePollFailing")]
    public required bool StoragePollFailing { get; init; }

    /// <summary>Gets the pending (open, undelivered) dispatch backlog count, when a snapshot is available.</summary>
    [JsonPropertyName("open")]
    public long? Open { get; init; }

    /// <summary>Gets the dead-lettered dispatch count (exhausted retries), when a snapshot is available.</summary>
    [JsonPropertyName("deadLettered")]
    public long? DeadLettered { get; init; }

    /// <summary>Gets the timestamp of the most recent successful dispatch pass, when known.</summary>
    [JsonPropertyName("lastPollAt")]
    public DateTimeOffset? LastPollAt { get; init; }

    /// <summary>
    /// Gets the per-channel delivery circuit-breaker states. Empty on this branch — the per-channel
    /// circuit breaker lands separately; the field is present so the shape is stable when it wires in.
    /// </summary>
    [JsonPropertyName("channelCircuits")]
    public required IReadOnlyList<OperateChannelCircuitView> ChannelCircuits { get; init; }
}

/// <summary>Per-channel delivery circuit-breaker state.</summary>
public sealed class OperateChannelCircuitView
{
    /// <summary>Gets the notification channel identifier.</summary>
    [JsonPropertyName("channel")]
    public required string Channel { get; init; }

    /// <summary>Gets the circuit state (<c>open</c>/<c>half-open</c>/<c>closed</c>).</summary>
    [JsonPropertyName("state")]
    public required string State { get; init; }
}

/// <summary>Schema-migration rollup: pending counts and classification.</summary>
public sealed class OperateMigrationsView
{
    /// <summary>Gets the drill-down source endpoint hint.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>Gets the count of pending (non-contract) migration scripts.</summary>
    [JsonPropertyName("pending")]
    public required int Pending { get; init; }

    /// <summary>Gets the count of pending contract migration scripts (which hold coordinated-deploy readiness).</summary>
    [JsonPropertyName("pendingContract")]
    public required int PendingContract { get; init; }

    /// <summary>Gets a value indicating whether a schema upgrade is required to reach the target version.</summary>
    [JsonPropertyName("upgradeRequired")]
    public required bool UpgradeRequired { get; init; }

    /// <summary>Gets the coarse classification of the pending set: <c>none</c>, <c>expand-only</c>, or <c>contract-pending</c>.</summary>
    [JsonPropertyName("classification")]
    public required string Classification { get; init; }
}

/// <summary>Deterministic ops-findings rollup: count by severity and the top items.</summary>
public sealed class OperateFindingsView
{
    /// <summary>Gets the drill-down source endpoint hint.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>Gets the total number of active findings.</summary>
    [JsonPropertyName("total")]
    public required int Total { get; init; }

    /// <summary>Gets the finding counts keyed by severity (<c>critical</c>/<c>warning</c>/<c>info</c>).</summary>
    [JsonPropertyName("bySeverity")]
    public required IReadOnlyDictionary<string, int> BySeverity { get; init; }

    /// <summary>Gets the highest-severity findings (capped), each with its drill-down id.</summary>
    [JsonPropertyName("top")]
    public required IReadOnlyList<OperateFindingSummaryView> Top { get; init; }
}

/// <summary>A compact summary of a single ops finding for the aggregated status.</summary>
public sealed class OperateFindingSummaryView
{
    /// <summary>Gets the deterministic finding identifier (used to drill down / propose its action).</summary>
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
}

/// <summary>Telemetry-backend posture rollup: configured backends, kind, reachability posture.</summary>
public sealed class OperateTelemetryBackendsView
{
    /// <summary>Gets the drill-down source hint (the configuration section).</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>Gets the count of configured telemetry connections.</summary>
    [JsonPropertyName("configured")]
    public required int Configured { get; init; }

    /// <summary>Gets the configured telemetry backends.</summary>
    [JsonPropertyName("backends")]
    public required IReadOnlyList<OperateTelemetryBackendView> Backends { get; init; }
}

/// <summary>A single configured telemetry backend.</summary>
public sealed class OperateTelemetryBackendView
{
    /// <summary>Gets the connection identifier.</summary>
    [JsonPropertyName("connectionId")]
    public required string ConnectionId { get; init; }

    /// <summary>Gets the provider kind (<c>prometheus</c>/<c>cloudwatch</c>/<c>azure-monitor</c>).</summary>
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    /// <summary>
    /// Gets the reachability posture. v1 reports <c>configured-not-probed</c> — the backend is a
    /// deploy-gating query provider, not liveness-probed on the status path; the field documents that
    /// honestly rather than asserting reachability the server has not verified.
    /// </summary>
    [JsonPropertyName("reachabilityPosture")]
    public required string ReachabilityPosture { get; init; }
}

/// <summary>Availability SLO / error-budget snapshot, or its explicit not-configured state.</summary>
public sealed class OperateSloView
{
    /// <summary>Gets a value indicating whether an availability SLO target is configured.</summary>
    [JsonPropertyName("configured")]
    public required bool Configured { get; init; }

    /// <summary>Gets an operator-facing reason when the SLO is not configured, otherwise <c>null</c>.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>Gets the availability SLO evaluation when configured, otherwise <c>null</c>.</summary>
    [JsonPropertyName("availability")]
    public OperateSloAvailabilityView? Availability { get; init; }
}

/// <summary>Evaluated availability SLO: target, observed availability, burn rate, remaining budget.</summary>
public sealed class OperateSloAvailabilityView
{
    /// <summary>Gets the configured target availability (fraction in (0, 1)).</summary>
    [JsonPropertyName("target")]
    public required double Target { get; init; }

    /// <summary>Gets the evaluation window in seconds (the in-process serving-latency aggregator's window).</summary>
    [JsonPropertyName("windowSeconds")]
    public required double WindowSeconds { get; init; }

    /// <summary>Gets the request count observed over the window.</summary>
    [JsonPropertyName("requestCount")]
    public required long RequestCount { get; init; }

    /// <summary>Gets the server-error count (HTTP status &gt;= 500) observed over the window.</summary>
    [JsonPropertyName("errorCount")]
    public required long ErrorCount { get; init; }

    /// <summary>Gets the observed availability over the window (1 - errorRate), or <c>null</c> when there is no traffic to evaluate.</summary>
    [JsonPropertyName("observed")]
    public double? Observed { get; init; }

    /// <summary>
    /// Gets the error-budget burn rate: observed error fraction divided by the allowed error budget
    /// (1 - target). <c>1.0</c> means the budget is being consumed exactly at the sustainable rate;
    /// &gt; <c>1.0</c> means it is burning too fast. <c>null</c> when there is no traffic to evaluate.
    /// </summary>
    [JsonPropertyName("burnRate")]
    public double? BurnRate { get; init; }

    /// <summary>
    /// Gets the fraction of the error budget still remaining over the window (0.0 = exhausted,
    /// 1.0 = untouched), or <c>null</c> when there is no traffic to evaluate.
    /// </summary>
    [JsonPropertyName("errorBudgetRemaining")]
    public double? ErrorBudgetRemaining { get; init; }

    /// <summary>Gets a short label naming what the SLO actually evaluated against.</summary>
    [JsonPropertyName("evaluationSource")]
    public required string EvaluationSource { get; init; }
}
