// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Admin.Jobs;

internal sealed record ConsoleJobQueryRequest
{
    public IReadOnlyList<ExecutionJobStatus> Statuses { get; init; } = Array.Empty<ExecutionJobStatus>();
    public ExecutionJobKind? Kind { get; init; }
    public string? Backend { get; init; }
    public string? RequestedBy { get; init; }
    public string? CorrelationId { get; init; }
    public string? TraceId { get; init; }
    public string? DefinitionId { get; init; }
    public string? ResourceRef { get; init; }
    public string? Environment { get; init; }
    public string? Server { get; init; }
    public string? ReleaseId { get; init; }
    public string? ChangeSetId { get; init; }
    public string? AlertId { get; init; }
    public DateTimeOffset? CreatedFrom { get; init; }
    public DateTimeOffset? CreatedTo { get; init; }
    public string? Cursor { get; init; }
    public int Limit { get; init; } = 50;
}

internal sealed record ConsoleJobListResponse
{
    public required ConsoleJobSummary[] Items { get; init; }
    public string? NextCursor { get; init; }
}

internal record ConsoleJobSummary
{
    public required string JobId { get; init; }
    public required string Kind { get; init; }
    public string? Queue { get; init; }
    public required string Backend { get; init; }
    public required string TargetKind { get; init; }
    public required string WorkloadName { get; init; }
    public string? DefinitionId { get; init; }
    public required string Status { get; init; }
    public required string Priority { get; init; }
    public string? RequestedBy { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public long? DurationMs { get; init; }
    public double? PercentComplete { get; init; }
    public string? CurrentPhase { get; init; }
    public int AttemptCount { get; init; }
    public int MaxAttempts { get; init; }
    public DateTimeOffset? NextRetryAt { get; init; }
    public string? CorrelationId { get; init; }
    public string? TraceId { get; init; }
    public required string[] ResourceRefs { get; init; }
    public int ArtifactCount { get; init; }
    public required ConsoleJobLatestEvent LatestEvent { get; init; }
    public required ConsoleJobLinks Links { get; init; }
}

internal sealed record ConsoleJobDetail : ConsoleJobSummary
{
    public string? ProviderOperationId { get; init; }
    public string? ClaimedBy { get; init; }
    public DateTimeOffset? ClaimedAt { get; init; }
    public DateTimeOffset? LastHeartbeatAt { get; init; }
    public DateTimeOffset? CancellationRequestedAt { get; init; }
    public required string[] Warnings { get; init; }
    public ConsoleJobFailure? Failure { get; init; }
    public required ConsoleJobRetryPolicy RetryPolicy { get; init; }
    public string? ParentJobId { get; init; }
    public required string[] ChildJobIds { get; init; }
    public required Dictionary<string, string> SelectedMetadata { get; init; }
    public required ConsoleJobStage[] Stages { get; init; }
    public required ConsoleJobActionDescriptor[] Actions { get; init; }
}

internal sealed record ConsoleJobLatestEvent
{
    public required string Type { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required string Status { get; init; }
    public string? Phase { get; init; }
    public string? Message { get; init; }
}

internal sealed record ConsoleJobLinks
{
    public required string Self { get; init; }
    public required string Logs { get; init; }
    public required string Artifacts { get; init; }
    public required string Actions { get; init; }
    public string? Cancel { get; init; }
    public string? Retry { get; init; }
    public string? EventsByJob { get; init; }
    public string? EventsByCorrelation { get; init; }
}

internal sealed record ConsoleJobFailure
{
    public required string Message { get; init; }
    public required string Classification { get; init; }
}

internal sealed record ConsoleJobRetryPolicy
{
    public int MaxAttempts { get; init; }
    public required string Strategy { get; init; }
    public long BaseDelayMs { get; init; }
    public long MaxDelayMs { get; init; }
}

internal sealed record ConsoleJobStage
{
    public required string Name { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public double? PercentComplete { get; init; }
}

internal sealed record ConsoleJobActionDescriptor
{
    public required string Name { get; init; }
    public required string Method { get; init; }
    public required string Href { get; init; }
    public bool Allowed { get; init; }
    public string? DisabledReason { get; init; }
    public bool RequiresApproval { get; init; }
}

internal sealed record ConsoleJobActionsResponse
{
    public required string JobId { get; init; }
    public string? CorrelationId { get; init; }
    public required ConsoleJobActionDescriptor[] Actions { get; init; }
}

internal sealed record ConsoleJobLogPageResponse
{
    public required string JobId { get; init; }
    public string? CorrelationId { get; init; }
    public required string State { get; init; }
    public required ConsoleJobLogEntry[] Items { get; init; }
    public string? NextCursor { get; init; }
}

internal sealed record ConsoleJobLogEntry
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string Level { get; init; }
    public required string Message { get; init; }
    public string? Phase { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}

internal sealed record ConsoleJobArtifactPageResponse
{
    public required string JobId { get; init; }
    public string? CorrelationId { get; init; }
    public required ConsoleJobArtifact[] Items { get; init; }
    public string? NextCursor { get; init; }
}

internal sealed record ConsoleJobArtifact
{
    public required string ArtifactId { get; init; }
    public required string Availability { get; init; }
    public string? Kind { get; init; }
    public string? Label { get; init; }
    public string? ContentType { get; init; }
    public long? SizeBytes { get; init; }
    public string? ProviderLink { get; init; }
    public string? Message { get; init; }
}

internal sealed record ConsoleJobControlResponse
{
    public required string JobId { get; init; }
    public string? CorrelationId { get; init; }
    public required string Action { get; init; }
    public required string Status { get; init; }
    public required string Message { get; init; }
}
