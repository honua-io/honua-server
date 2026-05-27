// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// Worker-side contract for executing a claimed job. Implementations are registered
/// per <see cref="ExecutionJobKind"/> and invoked by the worker host when a job
/// is claimed from the queue.
/// </summary>
public interface IJobExecutor
{
    /// <summary>
    /// The job kind this executor handles.
    /// </summary>
    ExecutionJobKind Kind { get; }

    /// <summary>
    /// The set of runtime profiles this executor is willing to run.
    /// </summary>
    /// <remarks>
    /// This is the worker-side half of the runtime-profile claim fence. The default
    /// implementation returns <see cref="RuntimeProfiles.DefaultAccepted"/>
    /// (managed/default only) so that every existing executor — and the lean
    /// dispatcher composed from them — is correctly fenced and can NEVER claim a
    /// <see cref="RuntimeProfiles.Native"/> job without opting in. An undeclared set
    /// therefore means "managed/default only", NOT "accept any profile". The
    /// heavyweight GDAL worker (added in a later stream) overrides this to declare
    /// <c>{ "native" }</c>.
    ///
    /// <para>
    /// A job whose <see cref="ExecutionJobSpec.RuntimeProfile"/> is <c>null</c> or
    /// empty is treated as the managed/default profile (see
    /// <see cref="RuntimeProfiles.Normalize(string?)"/>), so pre-profile jobs remain
    /// claimable by managed workers and are never starved.
    /// </para>
    /// </remarks>
    IReadOnlySet<string> AcceptedRuntimeProfiles => RuntimeProfiles.DefaultAccepted;

    /// <summary>
    /// Executes the job. The implementation should report progress, append structured
    /// logs, and publish artifact references through <paramref name="context"/>.
    /// </summary>
    /// <param name="job">The claimed execution job record.</param>
    /// <param name="context">
    /// Runtime context providing progress reporting, structured logging,
    /// artifact publication, and cooperative cancellation.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token that is triggered when the job is cancelled or
    /// the worker is shutting down.
    /// </param>
    /// <returns>
    /// The terminal status of the job. Implementations should return
    /// <see cref="ExecutionJobStatus.Succeeded"/> or <see cref="ExecutionJobStatus.Failed"/>.
    /// </returns>
    Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Result returned by an <see cref="IJobExecutor"/> after execution completes.
/// </summary>
public sealed record JobExecutionResult
{
    /// <summary>
    /// Terminal status of the execution.
    /// </summary>
    public required ExecutionJobStatus Status { get; init; }

    /// <summary>
    /// Error message when the execution failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Warnings collected during execution.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static JobExecutionResult Succeeded(IReadOnlyList<string>? warnings = null)
        => new()
        {
            Status = ExecutionJobStatus.Succeeded,
            Warnings = warnings ?? []
        };

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static JobExecutionResult Failed(string errorMessage, IReadOnlyList<string>? warnings = null)
        => new()
        {
            Status = ExecutionJobStatus.Failed,
            ErrorMessage = errorMessage,
            Warnings = warnings ?? []
        };
}

/// <summary>
/// Runtime context provided to <see cref="IJobExecutor"/> implementations during execution.
/// Mediates all side effects (progress, logs, artifacts) through the substrate rather than
/// requiring executors to depend on infrastructure directly.
/// </summary>
public interface IJobExecutionContext
{
    /// <summary>
    /// Stable identifier of the executing job.
    /// </summary>
    string OperationId { get; }

    /// <summary>
    /// Reports progress for the executing job.
    /// </summary>
    /// <param name="percentComplete">Completion percentage (0-100), or null if indeterminate.</param>
    /// <param name="phase">Human-readable phase description.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ReportProgressAsync(
        double? percentComplete,
        string? phase,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a structured log entry for the executing job.
    /// </summary>
    /// <param name="entry">Log entry to append.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AppendLogAsync(
        ExecutionLogEntry entry,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers an artifact reference produced by the executing job.
    /// </summary>
    /// <param name="artifactReference">Stable artifact reference string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PublishArtifactAsync(
        string artifactReference,
        CancellationToken cancellationToken = default);
}
