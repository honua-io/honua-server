// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

public interface IJobExecutor
{
    ExecutionJobKind Kind { get; }

    IReadOnlySet<string> AcceptedRuntimeProfiles => RuntimeProfiles.DefaultAccepted;

    Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken);
}

public sealed record JobExecutionResult
{
    public required ExecutionJobStatus Status { get; init; }

    public string? ErrorMessage { get; init; }

    public bool IsRetryable { get; init; } = true;

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool CompletedWithCommittedEffects { get; init; }

    public static JobExecutionResult Succeeded(IReadOnlyList<string>? warnings = null)
        => new()
        {
            Status = ExecutionJobStatus.Succeeded,
            Warnings = warnings ?? []
        };

    public static JobExecutionResult Failed(string errorMessage, IReadOnlyList<string>? warnings = null)
        => new()
        {
            Status = ExecutionJobStatus.Failed,
            ErrorMessage = errorMessage,
            Warnings = warnings ?? []
        };
}

public interface IJobExecutionContext
{
    string OperationId { get; }

    Task ReportProgressAsync(
        double? percentComplete,
        string? phase,
        CancellationToken cancellationToken = default);

    Task AppendLogAsync(
        ExecutionLogEntry entry,
        CancellationToken cancellationToken = default);

    Task PublishArtifactAsync(
        string artifactReference,
        CancellationToken cancellationToken = default);

    async Task<bool> TryPublishArtifactAsync(
        string artifactReference,
        CancellationToken cancellationToken = default)
    {
        await PublishArtifactAsync(artifactReference, cancellationToken).ConfigureAwait(false);
        return true;
    }

    async Task RecordCommittedEffectAsync(string artifactReference, CancellationToken cancellationToken = default)
    {
        if (!await TryPublishArtifactAsync(artifactReference, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The execution fence rejected a committed-effect receipt.");
        }
    }

    Task ThrowIfExecutionLeaseLostAsync(
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
