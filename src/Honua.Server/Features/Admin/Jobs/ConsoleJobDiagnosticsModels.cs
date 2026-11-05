// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Jobs;

// Evidence the server does not hold is named as missing and is never synthesised.
internal sealed record ConsoleJobDiagnostics
{
    public const string SchemaId = "honua.job-diagnostics/v1";

    public string Schema { get; init; } = SchemaId;
    public required string JobId { get; init; }
    public string? CorrelationId { get; init; }
    public string? TraceId { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required string View { get; init; }
    public required ConsoleJobDiagnosticIdentity Identity { get; init; }
    public required ConsoleJobDiagnosticRelease Release { get; init; }
    public required ConsoleJobDiagnosticLifecycle Lifecycle { get; init; }
    public required ConsoleJobDiagnosticAttempt Attempt { get; init; }
    public required ConsoleJobDiagnosticWorker Worker { get; init; }
    public ConsoleJobDiagnosticFailure? Failure { get; init; }
    public required ConsoleJobStep[] Steps { get; init; }
    public required ConsoleJobDiagnosticEvents Events { get; init; }
    public required ConsoleJobDiagnosticArtifacts Artifacts { get; init; }
    public required ConsoleJobDiagnosticEvidence Evidence { get; init; }
    public required ConsoleJobLinks Links { get; init; }
}

internal sealed record ConsoleJobDiagnosticIdentity
{
    public required string Kind { get; init; }
    public required string Backend { get; init; }
    public required string TargetKind { get; init; }
    public required string WorkloadName { get; init; }
    public string? WorkloadId { get; init; }
    public string? DefinitionId { get; init; }
    public string? Queue { get; init; }
    public required string Priority { get; init; }
    public string? RequestedBy { get; init; }
    public string? ProviderOperationId { get; init; }
    public string? RuntimeProfile { get; init; }
    public int ContractVersion { get; init; }
    public required string[] ResourceRefs { get; init; }
}

internal sealed record ConsoleJobDiagnosticRelease
{
    public required string State { get; init; }
    public required string ServerVersion { get; init; }
    public string? Runtime { get; init; }
    public string? SourceRepository { get; init; }
    public string? SourceRef { get; init; }
    public string? Entrypoint { get; init; }
    public string? DependencyManifest { get; init; }
    public string? ProcessRelease { get; init; }
    public string? ProcessVersion { get; init; }
    public string? LockDigest { get; init; }
    public string? BindingVersion { get; init; }
    public string? WorkflowVersion { get; init; }
    public string? DependencyLockDigest { get; init; }
    public string? WorkerImage { get; init; }
    public string? PlanId { get; init; }
    public required string[] ProcessDefinitions { get; init; }
    public string? Environment { get; init; }
    public string? ReleaseId { get; init; }
    public string? ChangeSetId { get; init; }
    public required string[] Missing { get; init; }
}

internal sealed record ConsoleJobDiagnosticLifecycle
{
    public required string Status { get; init; }
    public required string Phase { get; init; }
    public string? CurrentPhase { get; init; }
    public double? PercentComplete { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ClaimedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? CancellationRequestedAt { get; init; }
    public long? QueueDelayMs { get; init; }
    public long? ExecutionMs { get; init; }
    public long? DurationMs { get; init; }
    public required string TimingSource { get; init; }
}

internal sealed record ConsoleJobDiagnosticAttempt
{
    public required int Number { get; init; }
    public required int MaxAttempts { get; init; }
    public DateTimeOffset? NextRetryAt { get; init; }
    public required ConsoleJobDiagnosticRetry Retry { get; init; }
}

internal sealed record ConsoleJobDiagnosticRetry
{
    public required bool Allowed { get; init; }
    public string? DisabledReason { get; init; }
    public bool RequiresApproval { get; init; }
    public required string WouldRepeat { get; init; }
}

internal sealed record ConsoleJobDiagnosticWorker
{
    public string? ClaimedBy { get; init; }
    public DateTimeOffset? LastHeartbeatAt { get; init; }
    public required double HeartbeatIntervalSeconds { get; init; }
    public required double HeartbeatTimeoutSeconds { get; init; }
    public required string Liveness { get; init; }
    public required string LivenessReason { get; init; }
}

internal sealed record ConsoleJobDiagnosticFailure
{
    public required string Message { get; init; }
    public required string Classification { get; init; }
    public string? Phase { get; init; }
    public int? StepOrdinal { get; init; }
    public DateTimeOffset? OccurredAt { get; init; }
    public string? ErrorCode { get; init; }
    public int? NativeExitCode { get; init; }
    public string? StderrExcerpt { get; init; }
    public required string Recovery { get; init; }
}

internal sealed record ConsoleJobDiagnosticEvents
{
    public required string State { get; init; }
    public required ConsoleJobLogEntry[] Items { get; init; }
    public required int TotalCount { get; init; }
    public required int Limit { get; init; }
    public string? FirstCursor { get; init; }
    public string? NextCursor { get; init; }
    public string Mode { get; init; } = "tail";
    public required bool Truncated { get; init; }
}

internal sealed record ConsoleJobDiagnosticArtifacts
{
    public required string State { get; init; }
    public required ConsoleJobArtifact[] Items { get; init; }
    public required int AvailableCount { get; init; }
    public required int ExpiredCount { get; init; }
    public required int UnavailableCount { get; init; }
}

internal sealed record ConsoleJobDiagnosticEvidence
{
    public required string[] Missing { get; init; }
    public required string[] Truncated { get; init; }
    public required string[] Notes { get; init; }
}
