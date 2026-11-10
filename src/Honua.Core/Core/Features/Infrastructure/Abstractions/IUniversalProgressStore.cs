// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Infrastructure.Abstractions;

public interface IUniversalProgressStore
{
    Task SetProgressAsync(string operationId, IOperationProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default);

    // The status check and the write are atomic, so a cancellation cannot overwrite a terminal state that landed at the same time.
    Task<ProgressCompareAndSetResult> TrySetProgressAsync(
        string operationId,
        IOperationProgress progress,
        OperationStatus expectedStatus,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    // A store that cannot compare the whole snapshot must refuse the write instead of falling back to a status check.
    Task<ProgressCompareAndSetResult> TryReplaceProgressAsync(
        string operationId,
        IOperationProgress progress,
        IOperationProgress expectedProgress,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Atomic progress snapshot comparison is required.");

    Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
        where TProgress : class, IOperationProgress;

    Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default);

    Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(OperationType? operationType = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(OperationType operationType, CancellationToken cancellationToken = default)
        where TProgress : class, IOperationProgress;
}
