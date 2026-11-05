// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Operations.Status;

public sealed class OperateStatusResponse
{
    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("reasons")]
    public required IReadOnlyList<string> Reasons { get; init; }

    [JsonPropertyName("domains")]
    public required OperateStatusDomains Domains { get; init; }

    [JsonPropertyName("slo")]
    public required OperateSloView Slo { get; init; }
}

public sealed class OperateStatusDomains
{
    [JsonPropertyName("deploys")]
    public required OperateDeploysView Deploys { get; init; }

    [JsonPropertyName("jobs")]
    public required OperateJobsView Jobs { get; init; }

    [JsonPropertyName("alerts")]
    public required OperateAlertsView Alerts { get; init; }

    [JsonPropertyName("migrations")]
    public required OperateMigrationsView Migrations { get; init; }

    [JsonPropertyName("findings")]
    public required OperateFindingsView Findings { get; init; }

    [JsonPropertyName("telemetryBackends")]
    public required OperateTelemetryBackendsView TelemetryBackends { get; init; }
}

public sealed class OperateDeploysView
{
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("available")]
    public required bool Available { get; init; }

    [JsonPropertyName("active")]
    public required int Active { get; init; }

    [JsonPropertyName("parked")]
    public required int Parked { get; init; }

    [JsonPropertyName("awaitingApproval")]
    public required int AwaitingApproval { get; init; }

    [JsonPropertyName("byStatus")]
    public required IReadOnlyDictionary<string, int> ByStatus { get; init; }

    [JsonPropertyName("lastOutcome")]
    public string? LastOutcome { get; init; }
}

public sealed class OperateJobsView
{
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("available")]
    public required bool Available { get; init; }

    [JsonPropertyName("totalActive")]
    public required int TotalActive { get; init; }

    [JsonPropertyName("queued")]
    public required int Queued { get; init; }

    [JsonPropertyName("running")]
    public required int Running { get; init; }

    [JsonPropertyName("backends")]
    public required IReadOnlyList<string> Backends { get; init; }

    [JsonPropertyName("byStatusBackend")]
    public required IReadOnlyList<OperateJobBucketView> ByStatusBackend { get; init; }

    [JsonPropertyName("substrate")]
    public required OperateSubstrateView Substrate { get; init; }
}

public sealed class OperateJobBucketView
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("backend")]
    public required string Backend { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }
}

public sealed class OperateSubstrateView
{
    [JsonPropertyName("evaluated")]
    public required bool Evaluated { get; init; }

    [JsonPropertyName("profile")]
    public string? Profile { get; init; }

    [JsonPropertyName("compatible")]
    public bool? Compatible { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

public sealed class OperateAlertsView
{
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("enabled")]
    public required bool Enabled { get; init; }

    [JsonPropertyName("dispatcherRunning")]
    public required bool DispatcherRunning { get; init; }

    [JsonPropertyName("storagePollFailing")]
    public required bool StoragePollFailing { get; init; }

    [JsonPropertyName("open")]
    public long? Open { get; init; }

    [JsonPropertyName("deadLettered")]
    public long? DeadLettered { get; init; }

    [JsonPropertyName("lastPollAt")]
    public DateTimeOffset? LastPollAt { get; init; }

    [JsonPropertyName("channelCircuits")]
    public required IReadOnlyList<OperateChannelCircuitView> ChannelCircuits { get; init; }
}

public sealed class OperateChannelCircuitView
{
    [JsonPropertyName("channel")]
    public required string Channel { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }
}

public sealed class OperateMigrationsView
{
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("pending")]
    public required int Pending { get; init; }

    [JsonPropertyName("pendingContract")]
    public required int PendingContract { get; init; }

    [JsonPropertyName("upgradeRequired")]
    public required bool UpgradeRequired { get; init; }

    [JsonPropertyName("classification")]
    public required string Classification { get; init; }
}

public sealed class OperateFindingsView
{
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("total")]
    public required int Total { get; init; }

    [JsonPropertyName("bySeverity")]
    public required IReadOnlyDictionary<string, int> BySeverity { get; init; }

    [JsonPropertyName("top")]
    public required IReadOnlyList<OperateFindingSummaryView> Top { get; init; }
}

public sealed class OperateFindingSummaryView
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("rule")]
    public required string Rule { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }
}

public sealed class OperateTelemetryBackendsView
{
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("configured")]
    public required int Configured { get; init; }

    [JsonPropertyName("backends")]
    public required IReadOnlyList<OperateTelemetryBackendView> Backends { get; init; }
}

public sealed class OperateTelemetryBackendView
{
    [JsonPropertyName("connectionId")]
    public required string ConnectionId { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("reachabilityPosture")]
    public required string ReachabilityPosture { get; init; }
}

public sealed class OperateSloView
{
    [JsonPropertyName("configured")]
    public required bool Configured { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    // The replica-local retained tail is never projected as the platform availability evaluation.
    [JsonPropertyName("availability")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public OperateSloAvailabilityView? Availability { get; init; }

    [JsonPropertyName("nodeLocalRetainedTail")]
    public required OperateNodeLocalRetainedTailView NodeLocalRetainedTail { get; init; }
}

public sealed class OperateSloAvailabilityView
{
    [JsonPropertyName("target")]
    public required double Target { get; init; }

    [JsonPropertyName("windowSeconds")]
    public required double WindowSeconds { get; init; }

    [JsonPropertyName("requestCount")]
    public required long RequestCount { get; init; }

    [JsonPropertyName("errorCount")]
    public required long ErrorCount { get; init; }

    [JsonPropertyName("observed")]
    public double? Observed { get; init; }

    [JsonPropertyName("burnRate")]
    public double? BurnRate { get; init; }

    [JsonPropertyName("errorBudgetRemaining")]
    public double? ErrorBudgetRemaining { get; init; }

    [JsonPropertyName("evaluationSource")]
    public required string EvaluationSource { get; init; }
}

public sealed class OperateNodeLocalRetainedTailView
{
    [JsonPropertyName("scope")]
    public required string Scope { get; init; }

    // This retained tail is never a platform service-level indicator.
    [JsonPropertyName("isPlatformSli")]
    public required bool IsPlatformSli { get; init; }

    [JsonPropertyName("configuredTarget")]
    public double? ConfiguredTarget { get; init; }

    [JsonPropertyName("configuredWindowSeconds")]
    public required int ConfiguredWindowSeconds { get; init; }

    [JsonPropertyName("retentionWindowSeconds")]
    public required double RetentionWindowSeconds { get; init; }

    [JsonPropertyName("oldestRetainedSampleAgeSeconds")]
    public double? OldestRetainedSampleAgeSeconds { get; init; }

    [JsonPropertyName("newestRetainedSampleAgeSeconds")]
    public double? NewestRetainedSampleAgeSeconds { get; init; }

    [JsonPropertyName("retainedRequestCount")]
    public required long RetainedRequestCount { get; init; }

    [JsonPropertyName("retainedHttpServerErrorCount")]
    public required long RetainedHttpServerErrorCount { get; init; }

    [JsonPropertyName("retainedHttpSuccessRatio")]
    public double? RetainedHttpSuccessRatio { get; init; }

    [JsonPropertyName("totalRecordedSinceReset")]
    public required long TotalRecordedSinceReset { get; init; }

    [JsonPropertyName("overwrittenSampleCount")]
    public required long OverwrittenSampleCount { get; init; }

    [JsonPropertyName("includesInBandErrors")]
    public required bool IncludesInBandErrors { get; init; }

    [JsonPropertyName("resetBehavior")]
    public required string ResetBehavior { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("protocols")]
    public required IReadOnlyList<OperateNodeLocalProtocolTailView> Protocols { get; init; }
}

public sealed class OperateNodeLocalProtocolTailView
{
    [JsonPropertyName("protocol")]
    public required string Protocol { get; init; }

    [JsonPropertyName("retainedRequestCount")]
    public required long RetainedRequestCount { get; init; }

    [JsonPropertyName("retentionCapacity")]
    public required int RetentionCapacity { get; init; }

    [JsonPropertyName("totalRecordedSinceReset")]
    public required long TotalRecordedSinceReset { get; init; }

    [JsonPropertyName("overwrittenSampleCount")]
    public required long OverwrittenSampleCount { get; init; }

    [JsonPropertyName("oldestRetainedSampleAgeSeconds")]
    public double? OldestRetainedSampleAgeSeconds { get; init; }

    [JsonPropertyName("newestRetainedSampleAgeSeconds")]
    public double? NewestRetainedSampleAgeSeconds { get; init; }
}
