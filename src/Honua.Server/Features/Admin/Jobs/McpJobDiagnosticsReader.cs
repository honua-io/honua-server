// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Geoprocessing;

namespace Honua.Server.Features.Admin.Jobs;

/// <summary>
/// Server-side adapter behind <c>honua_diagnose_job</c> and <c>honua_job_evidence</c>
/// (honua-server#6219). It reads the same <see cref="ConsoleJobDiagnostics"/> projection
/// as <c>GET /api/v1/admin/jobs/{jobId}/diagnostics</c> under the same Job Read gate, then
/// splits it into observed facts and inferred explanations so an agent cannot mistake the
/// server's reading of the evidence for the evidence itself.
/// </summary>
internal sealed class McpJobDiagnosticsReader(IConsoleJobService jobs) : IMcpJobDiagnosticsReader
{
    public const string DiagnosisSchemaId = "honua.mcp.job-diagnosis/v1";

    public const string EvidenceSchemaId = "honua.mcp.job-evidence/v1";

    public const string Caveat =
        "A returned diagnosis is the server's reading of recorded evidence. It is not proof that the job's "
        + "analysis succeeded (read observed.status) and inferred fields are hypotheses to verify against observed ones.";

    private readonly IConsoleJobService _jobs = jobs;

    public async Task<JsonElement> DiagnoseJobAsync(
        ClaimsPrincipal principal,
        McpDiagnoseJobArgument argument,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(argument);

        var projection = await ProjectAsync(principal, argument.JobId, argument.Events, argument.EventCursor, cancellationToken).ConfigureAwait(false);
        var diagnosis = new McpJobDiagnosis
        {
            JobId = projection.JobId,
            CorrelationId = projection.CorrelationId,
            TraceId = projection.TraceId,
            View = projection.View,
            GeneratedAt = projection.GeneratedAt,
            Observed = new McpJobDiagnosisObserved
            {
                Status = projection.Lifecycle.Status,
                CurrentPhase = projection.Lifecycle.CurrentPhase,
                PercentComplete = projection.Lifecycle.PercentComplete,
                CreatedAt = projection.Lifecycle.CreatedAt,
                ClaimedAt = projection.Lifecycle.ClaimedAt,
                UpdatedAt = projection.Lifecycle.UpdatedAt,
                CompletedAt = projection.Lifecycle.CompletedAt,
                CancellationRequestedAt = projection.Lifecycle.CancellationRequestedAt,
                QueueDelayMs = projection.Lifecycle.QueueDelayMs,
                ExecutionMs = projection.Lifecycle.ExecutionMs,
                DurationMs = projection.Lifecycle.DurationMs,
                AttemptNumber = projection.Attempt.Number,
                MaxAttempts = projection.Attempt.MaxAttempts,
                NextRetryAt = projection.Attempt.NextRetryAt,
                Identity = projection.Identity,
                Release = projection.Release,
                Worker = new McpJobDiagnosisWorkerObserved
                {
                    ClaimedBy = projection.Worker.ClaimedBy,
                    LastHeartbeatAt = projection.Worker.LastHeartbeatAt,
                    HeartbeatIntervalSeconds = projection.Worker.HeartbeatIntervalSeconds,
                    HeartbeatTimeoutSeconds = projection.Worker.HeartbeatTimeoutSeconds
                },
                Failure = projection.Failure is null
                    ? null
                    : new McpJobDiagnosisFailureObserved
                    {
                        Message = projection.Failure.Message,
                        Phase = projection.Failure.Phase,
                        StepOrdinal = projection.Failure.StepOrdinal,
                        OccurredAt = projection.Failure.OccurredAt,
                        ErrorCode = projection.Failure.ErrorCode,
                        NativeExitCode = projection.Failure.NativeExitCode,
                        StderrExcerpt = projection.Failure.StderrExcerpt
                    },
                Steps = projection.Steps,
                Events = projection.Events,
                Artifacts = projection.Artifacts
            },
            Inferred = new McpJobDiagnosisInferred
            {
                Phase = projection.Lifecycle.Phase,
                TimingSource = projection.Lifecycle.TimingSource,
                Liveness = projection.Worker.Liveness,
                LivenessReason = projection.Worker.LivenessReason,
                FailureClassification = projection.Failure?.Classification,
                Recovery = projection.Failure?.Recovery,
                Retry = projection.Attempt.Retry
            },
            Evidence = projection.Evidence,
            Links = projection.Links
        };

        return JsonSerializer.SerializeToElement(diagnosis, ConsoleJobJsonContext.Default.McpJobDiagnosis);
    }

    public async Task<JsonElement> GetJobEvidenceAsync(
        ClaimsPrincipal principal,
        McpJobEvidenceArgument argument,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(argument);

        var projection = await ProjectAsync(principal, argument.JobId, argument.Events, argument.EventCursor, cancellationToken).ConfigureAwait(false);
        var evidence = new McpJobEvidence
        {
            JobId = projection.JobId,
            CorrelationId = projection.CorrelationId,
            View = projection.View,
            GeneratedAt = projection.GeneratedAt,
            Steps = projection.Steps,
            Events = projection.Events,
            Artifacts = projection.Artifacts,
            Evidence = projection.Evidence,
            Links = projection.Links
        };

        return JsonSerializer.SerializeToElement(evidence, ConsoleJobJsonContext.Default.McpJobEvidence);
    }

    private async Task<ConsoleJobDiagnostics> ProjectAsync(
        ClaimsPrincipal principal,
        string? jobId,
        int? events,
        string? eventCursor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new GeoprocessingValidationException("jobId is required.");
        }

        ConsoleJobDiagnostics? projection;
        try
        {
            projection = await _jobs
                .GetDiagnosticsAsync(principal, pathBase: string.Empty, jobId.Trim(), events, eventCursor, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            throw new GeoprocessingValidationException("Invalid diagnostic event cursor.");
        }

        // Unreadable and unknown jobs are indistinguishable on purpose: the REST route answers
        // 404 for both so a caller cannot probe for job existence, and the tool keeps that.
        return projection ?? throw new GeoprocessingNotFoundException($"Job '{jobId.Trim()}' was not found.");
    }
}

internal sealed record McpJobDiagnosis
{
    public string Schema { get; init; } = McpJobDiagnosticsReader.DiagnosisSchemaId;
    public required string JobId { get; init; }
    public string? CorrelationId { get; init; }
    public string? TraceId { get; init; }
    public required string View { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required McpJobDiagnosisObserved Observed { get; init; }
    public required McpJobDiagnosisInferred Inferred { get; init; }
    public required ConsoleJobDiagnosticEvidence Evidence { get; init; }
    public required ConsoleJobLinks Links { get; init; }
    public string Caveat { get; init; } = McpJobDiagnosticsReader.Caveat;
}

internal sealed record McpJobDiagnosisObserved
{
    public required string Status { get; init; }
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
    public required int AttemptNumber { get; init; }
    public required int MaxAttempts { get; init; }
    public DateTimeOffset? NextRetryAt { get; init; }
    public required ConsoleJobDiagnosticIdentity Identity { get; init; }
    public required ConsoleJobDiagnosticRelease Release { get; init; }
    public required McpJobDiagnosisWorkerObserved Worker { get; init; }
    public McpJobDiagnosisFailureObserved? Failure { get; init; }
    public required ConsoleJobStep[] Steps { get; init; }
    public required ConsoleJobDiagnosticEvents Events { get; init; }
    public required ConsoleJobDiagnosticArtifacts Artifacts { get; init; }
}

internal sealed record McpJobDiagnosisWorkerObserved
{
    public string? ClaimedBy { get; init; }
    public DateTimeOffset? LastHeartbeatAt { get; init; }
    public required double HeartbeatIntervalSeconds { get; init; }
    public required double HeartbeatTimeoutSeconds { get; init; }
}

internal sealed record McpJobDiagnosisFailureObserved
{
    public required string Message { get; init; }
    public string? Phase { get; init; }
    public int? StepOrdinal { get; init; }
    public DateTimeOffset? OccurredAt { get; init; }
    public string? ErrorCode { get; init; }
    public int? NativeExitCode { get; init; }
    public string? StderrExcerpt { get; init; }
}

internal sealed record McpJobDiagnosisInferred
{
    public required string Phase { get; init; }
    public required string TimingSource { get; init; }
    public required string Liveness { get; init; }
    public required string LivenessReason { get; init; }
    public string? FailureClassification { get; init; }
    public string? Recovery { get; init; }
    public required ConsoleJobDiagnosticRetry Retry { get; init; }
}

internal sealed record McpJobEvidence
{
    public string Schema { get; init; } = McpJobDiagnosticsReader.EvidenceSchemaId;
    public required string JobId { get; init; }
    public string? CorrelationId { get; init; }
    public required string View { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required ConsoleJobStep[] Steps { get; init; }
    public required ConsoleJobDiagnosticEvents Events { get; init; }
    public required ConsoleJobDiagnosticArtifacts Artifacts { get; init; }
    public required ConsoleJobDiagnosticEvidence Evidence { get; init; }
    public required ConsoleJobLinks Links { get; init; }
}
