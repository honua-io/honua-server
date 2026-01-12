// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Universal progress store for tracking any type of operation progress.
/// Provides a unified interface for storing and retrieving progress across all operation types.
/// </summary>
public interface IUniversalProgressStore
{
    /// <summary>
    /// Store progress for any operation.
    /// </summary>
    /// <param name="operationId">Operation identifier</param>
    /// <param name="progress">Progress data implementing IOperationProgress</param>
    /// <param name="ttl">Time-to-live for the progress data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SetProgressAsync(string operationId, IOperationProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieve progress for any operation.
    /// </summary>
    /// <typeparam name="TProgress">Expected progress type</typeparam>
    /// <param name="operationId">Operation identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Progress data or null if not found</returns>
    Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
        where TProgress : class, IOperationProgress;

    /// <summary>
    /// Retrieve progress for any operation without type constraint.
    /// </summary>
    /// <param name="operationId">Operation identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Progress data or null if not found</returns>
    Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete progress data for an operation.
    /// </summary>
    /// <param name="operationId">Operation identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all active operation IDs (operations with progress data).
    /// </summary>
    /// <param name="operationType">Optional filter by operation type</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(OperationType? operationType = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all active operations of a specific type.
    /// </summary>
    /// <typeparam name="TProgress">Progress type to retrieve</typeparam>
    /// <param name="operationType">Operation type filter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of active operations</returns>
    Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(OperationType operationType, CancellationToken cancellationToken = default)
        where TProgress : class, IOperationProgress;
}
