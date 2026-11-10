// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

// The jobs each reconciliation loop has work for, so a loop does not read the whole backlog of
// queued jobs on every pass. Each loop still applies its own checks to what it is given.
internal interface IReconcilableJobSource
{
    // Claimed jobs whose heartbeat or execution timeout may have expired, and local queued jobs
    // that are not in the queue.
    Task<IReadOnlyList<ExecutionJobRecord>> ListExpiryAndDispatchCandidatesAsync(CancellationToken cancellationToken);

    // Non-terminal jobs a batch compute backend has to observe, start, retry or cancel: every one
    // except first-attempt local jobs waiting in the queue with no cancellation request.
    Task<IReadOnlyList<string>> ListBackendReconciliationCandidateIdsAsync(CancellationToken cancellationToken);
}

// Candidates of a store that can only list its active jobs, such as an in-memory store.
internal sealed class ActiveJobsReconcilableJobSource(IExecutionJobStore jobStore) : IReconcilableJobSource
{
    public Task<IReadOnlyList<ExecutionJobRecord>> ListExpiryAndDispatchCandidatesAsync(CancellationToken cancellationToken)
        => jobStore.ListActiveAsync(cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<string>> ListBackendReconciliationCandidateIdsAsync(CancellationToken cancellationToken)
        => (await jobStore.ListActiveAsync(kind: null, cancellationToken: cancellationToken).ConfigureAwait(false))
            .Select(job => job.OperationId)
            .ToArray();
}
