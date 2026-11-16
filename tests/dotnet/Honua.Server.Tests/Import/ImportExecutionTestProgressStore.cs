// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Migration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Import;

internal sealed class ImportExecutionTestProgressStore(IExecutionJobStore jobs) : IUniversalProgressStore
{
    private readonly UniversalProgressStore _inner = new(null, NullLogger<UniversalProgressStore>.Instance);

    public Task SetProgressAsync(string operationId, IOperationProgress progress, TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
        => _inner.SetProgressAsync(operationId, progress, ttl, cancellationToken);

    public async Task<ProgressCompareAndSetResult> TrySetProgressAsync(OperationProgressWrite write,
        OperationStatus expectedStatus, CancellationToken cancellationToken = default)
    {
        await ValidateFenceAsync(write, cancellationToken);
        return await _inner.TrySetProgressAsync(write with { ExecutionFence = null }, expectedStatus, cancellationToken);
    }

    public async Task<ProgressCompareAndSetResult> TryReplaceProgressAsync(OperationProgressWrite write,
        IOperationProgress? expectedProgress, CancellationToken cancellationToken = default)
    {
        await ValidateFenceAsync(write, cancellationToken);
        return await _inner.TryReplaceProgressAsync(write with { ExecutionFence = null }, expectedProgress, cancellationToken);
    }

    public Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
        where TProgress : class, IOperationProgress
        => _inner.GetProgressAsync<TProgress>(operationId, cancellationToken);

    public Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default)
        => _inner.GetProgressAsync(operationId, cancellationToken);

    public Task QuarantineProgressAsync(string operationId, OperationType operationType,
        CancellationToken cancellationToken = default)
        => _inner.QuarantineProgressAsync(operationId, operationType, cancellationToken);

    public Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default)
        => _inner.DeleteProgressAsync(operationId, cancellationToken);

    public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(OperationType? operationType = null,
        CancellationToken cancellationToken = default)
        => _inner.GetActiveOperationIdsAsync(operationType, cancellationToken);

    public Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(OperationType operationType,
        CancellationToken cancellationToken = default) where TProgress : class, IOperationProgress
        => _inner.GetActiveOperationsAsync<TProgress>(operationType, cancellationToken);

    private async Task ValidateFenceAsync(OperationProgressWrite write, CancellationToken cancellationToken)
    {
        if (write.ExecutionFence is not { } fence)
        {
            return;
        }

        // Transactional ownership changes are exercised by PostgresUniversalProgressStoreTests.
        var job = await jobs.GetAsync(write.OperationId, cancellationToken);
        if (job is null || job.ClaimedBy != fence.WorkerId || job.AttemptCount != fence.AttemptCount ||
            job.Status is not (ExecutionJobStatus.Running or ExecutionJobStatus.Provisioning))
        {
            throw new OperationCanceledException("The execution attempt no longer owns progress writes.", cancellationToken);
        }
    }
}
