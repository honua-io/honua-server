// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Share.Domain;

namespace Honua.Core.Features.Share.Abstractions;

/// <summary>
/// Raised when a durable Share export write conflicts with an existing definition or run record.
/// </summary>
public sealed class ShareExportStoreConflictException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ShareExportStoreConflictException"/> class.</summary>
    /// <param name="message">Safe conflict message.</param>
    public ShareExportStoreConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ShareExportStoreConflictException"/> class.</summary>
    /// <param name="message">Safe conflict message.</param>
    /// <param name="innerException">Underlying provider exception.</param>
    public ShareExportStoreConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when the durable Share export store cannot be reached because of an infrastructure
/// outage (connection failure, timeout, resource exhaustion, or backend authorization failure)
/// rather than a request-level problem. Callers map this to a retryable 503 so a transient store
/// outage is not reported as a generic 500.
/// </summary>
public sealed class ShareExportStoreUnavailableException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ShareExportStoreUnavailableException"/> class.</summary>
    /// <param name="message">Safe, store-neutral unavailability message.</param>
    public ShareExportStoreUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ShareExportStoreUnavailableException"/> class.</summary>
    /// <param name="message">Safe, store-neutral unavailability message.</param>
    /// <param name="innerException">Underlying provider exception.</param>
    public ShareExportStoreUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Durable store for scheduled Share export definitions and their append-mostly run history.
/// Contains no scheduler logic; it only persists and queries server-owned records.
/// </summary>
public interface IShareExportStore
{
    /// <summary>
    /// Lists scheduled export definitions newest first with cursor pagination.
    /// </summary>
    /// <param name="query">Filter and cursor criteria.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A page of definitions.</returns>
    Task<ShareExportDefinitionPage> ListDefinitionsAsync(
        ShareExportDefinitionQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a single export definition.
    /// </summary>
    /// <param name="exportId">Export definition identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The definition, or null when not found.</returns>
    Task<ShareExportDefinition?> GetDefinitionAsync(
        string exportId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new export definition.
    /// </summary>
    /// <param name="definition">Definition to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored definition.</returns>
    /// <exception cref="ShareExportStoreConflictException">A definition with the same identifier already exists.</exception>
    Task<ShareExportDefinition> CreateDefinitionAsync(
        ShareExportDefinition definition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces an existing export definition.
    /// </summary>
    /// <param name="definition">Definition with updated fields.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored definition, or null when no definition with that identifier exists.</returns>
    Task<ShareExportDefinition?> UpdateDefinitionAsync(
        ShareExportDefinition definition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an export definition and its run history.
    /// </summary>
    /// <param name="exportId">Export definition identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when a definition was deleted; <c>false</c> when none existed.</returns>
    Task<bool> DeleteDefinitionAsync(
        string exportId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a run record to an export definition's history.
    /// </summary>
    /// <param name="run">Run record to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored run.</returns>
    /// <exception cref="ShareExportStoreConflictException">A run with the same identifier already exists.</exception>
    Task<ShareExportRun> AppendRunAsync(
        ShareExportRun run,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the lifecycle fields (status, timing, result artifacts, last error) of an existing
    /// run. Run identity fields (run id, export id, trigger kind, job run id, triggered-at) are not
    /// changed. Used to mark a run failed when its backing job cannot be dispatched, and to
    /// reconcile a run with its backing execution job's terminal state.
    /// </summary>
    /// <param name="run">Run carrying the identifier to match and the updated lifecycle fields.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored run, or null when no run with that identifier exists.</returns>
    Task<ShareExportRun?> UpdateRunAsync(
        ShareExportRun run,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists runs for an export definition newest first with cursor pagination.
    /// </summary>
    /// <param name="exportId">Export definition identifier.</param>
    /// <param name="cursor">Opaque continuation cursor, or null for the first page.</param>
    /// <param name="limit">Maximum number of runs to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A page of runs.</returns>
    Task<ShareExportRunPage> ListRunsAsync(
        string exportId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a single run for an export definition.
    /// </summary>
    /// <param name="exportId">Export definition identifier.</param>
    /// <param name="runId">Run identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The run, or null when not found.</returns>
    Task<ShareExportRun?> GetRunAsync(
        string exportId,
        string runId,
        CancellationToken cancellationToken = default);
}
