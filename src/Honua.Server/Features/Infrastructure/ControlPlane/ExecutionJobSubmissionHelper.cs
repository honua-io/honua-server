// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

internal static class ExecutionJobSubmissionHelper
{
    internal const string SubmissionFailurePhase = "Failed (submission)";
    internal const string SubmissionFailureMessage = "Submission failed: progress or queue persistence error.";

    public static async Task TryRollbackCreatedJobAsync(
        IExecutionJobStore jobStore,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        try
        {
            var current = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (current == null || current.Status != ExecutionJobStatus.Queued)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var failedJob = current with
            {
                Status = ExecutionJobStatus.Failed,
                UpdatedAt = now,
                CompletedAt = now,
                ErrorMessage = SubmissionFailureMessage,
                CurrentPhase = SubmissionFailurePhase
            };

            await jobStore.TrySetAsync(failedJob, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort rollback; job TTL or manual intervention will repair.
        }
    }

    public static bool IsSubmissionRollback(ExecutionJobRecord job)
        => job.Status == ExecutionJobStatus.Failed
            && string.Equals(job.CurrentPhase, SubmissionFailurePhase, StringComparison.Ordinal);
}
