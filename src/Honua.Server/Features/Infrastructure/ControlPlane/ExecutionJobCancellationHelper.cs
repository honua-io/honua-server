// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

internal enum ExecutionJobCancellationState
{
    Cancelled,
    CancellationRequested,
    TerminalConflict,
    Missing,
    Unconfirmed
}

internal readonly record struct ExecutionJobCancellationResult(
    ExecutionJobCancellationState State,
    ExecutionJobRecord? Job);

internal static class ExecutionJobCancellationHelper
{
    public static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded
            or ExecutionJobStatus.Failed
            or ExecutionJobStatus.Cancelled;

    public static async Task<ExecutionJobCancellationResult> TryApplyAsync(
        IExecutionJobStore jobStore,
        string operationId,
        ExecutionJobRecord initialJob,
        string cancelledPhase,
        int maxAttempts = 3,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        var current = initialJob;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (current.Status == ExecutionJobStatus.Cancelled)
            {
                return new(ExecutionJobCancellationState.Cancelled, current);
            }

            if (IsTerminal(current.Status))
            {
                return new(ExecutionJobCancellationState.TerminalConflict, current);
            }

            var now = DateTimeOffset.UtcNow;
            if (current.ClaimedBy != null)
            {
                if (current.CancellationRequestedAt.HasValue)
                {
                    return new(ExecutionJobCancellationState.CancellationRequested, current);
                }

                var requested = current with
                {
                    CancellationRequestedAt = now,
                    UpdatedAt = now
                };

                if (await jobStore.TrySetAsync(requested, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    return new(ExecutionJobCancellationState.CancellationRequested, requested);
                }
            }
            else
            {
                var cancelled = current with
                {
                    Status = ExecutionJobStatus.Cancelled,
                    UpdatedAt = now,
                    CompletedAt = now,
                    CurrentPhase = cancelledPhase
                };

                if (await jobStore.TrySetAsync(cancelled, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    return new(ExecutionJobCancellationState.Cancelled, cancelled);
                }
            }

            current = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (current == null)
            {
                return new(ExecutionJobCancellationState.Missing, null);
            }
        }

        if (current.Status == ExecutionJobStatus.Cancelled)
        {
            return new(ExecutionJobCancellationState.Cancelled, current);
        }

        if (IsTerminal(current.Status))
        {
            return new(ExecutionJobCancellationState.TerminalConflict, current);
        }

        if (current.ClaimedBy != null && current.CancellationRequestedAt.HasValue)
        {
            return new(ExecutionJobCancellationState.CancellationRequested, current);
        }

        return new(ExecutionJobCancellationState.Unconfirmed, current);
    }
}
