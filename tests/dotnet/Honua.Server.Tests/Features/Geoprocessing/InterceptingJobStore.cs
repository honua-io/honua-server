// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Tests.Features.Geoprocessing;

internal sealed class InterceptingJobStore(IExecutionJobStore inner) : IExecutionJobStore
{
    public Func<Task>? BeforeCreate { get; init; }

    public Func<Task>? AfterListActive { get; init; }

    public async Task<bool> TryCreateAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        if (BeforeCreate is not null)
        {
            await BeforeCreate();
        }

        return await inner.TryCreateAsync(job, ttl, cancellationToken);
    }

    public async Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(
        ExecutionJobKind? kind = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        var active = await inner.ListActiveAsync(kind, limit, cancellationToken);
        if (AfterListActive is not null)
        {
            await AfterListActive();
        }

        return active;
    }

    public Task<ExecutionJobRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
        => inner.GetAsync(operationId, cancellationToken);

    public Task SetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        => inner.SetAsync(job, ttl, cancellationToken);

    public Task<bool> TrySetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        => inner.TrySetAsync(job, ttl, cancellationToken);

    public Task<bool> TrySetIfLeaseOwnedAsync(
        ExecutionJobRecord job,
        OwnedJobLease lease,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
        => inner.TrySetIfLeaseOwnedAsync(job, lease, ttl, cancellationToken);

    public Task<ExecutionJobPage> QueryAsync(ExecutionJobQuery query, CancellationToken cancellationToken = default)
        => inner.QueryAsync(query, cancellationToken);

    public Task<bool> TryAcquireLeaseAsync(
        string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => inner.TryAcquireLeaseAsync(operationId, ownerId, leaseDuration, cancellationToken);

    public Task<bool> RenewLeaseAsync(
        string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => inner.RenewLeaseAsync(operationId, ownerId, leaseDuration, cancellationToken);

    public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
        => inner.ReleaseLeaseAsync(operationId, ownerId, cancellationToken);
}
