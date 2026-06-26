// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

/// <summary>
/// Outcome of an <see cref="IWfsStoredQueryStore.TryCreateAsync"/> attempt.
/// </summary>
internal enum WfsStoredQueryCreateStatus
{
    /// <summary>The stored query was created.</summary>
    Created,

    /// <summary>A stored query with the same identifier already exists.</summary>
    DuplicateId,

    /// <summary>The store already holds <see cref="IWfsStoredQueryStore.MaxManagedStoredQueryCount"/> stored queries.</summary>
    CapacityExceeded,
}

/// <summary>
/// Store for managed WFS 2.0 stored queries (CreateStoredQuery / DropStoredQuery).
/// The default registration is an in-memory, process-local store; Redis-enabled
/// deployments substitute a durable shared store so all replicas observe the same
/// set and the set survives restarts. Identifiers are case-insensitive.
/// </summary>
internal interface IWfsStoredQueryStore
{
    /// <summary>
    /// Hard cap on managed stored queries to prevent unbounded growth.
    /// WFS 2.0 CITE Manage-Stored-Queries conformance requires mutation operations to
    /// succeed but does not mandate unlimited cardinality. CreateStoredQuery is rejected
    /// with <see cref="WfsStoredQueryCreateStatus.CapacityExceeded"/> once the shared
    /// count reaches this threshold.
    /// </summary>
    public const int MaxManagedStoredQueryCount = 1000;

    /// <summary>
    /// Gets the stored query with the given identifier, or <see langword="null"/> when it does not exist.
    /// </summary>
    /// <param name="id">The stored query identifier (case-insensitive).</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<WfsStoredQueryDefinition?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all managed stored queries ordered by identifier (ordinal).
    /// </summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    Task<IReadOnlyList<WfsStoredQueryDefinition>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current number of managed stored queries.
    /// </summary>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically creates the stored query unless its identifier is already taken or
    /// the store is at <see cref="MaxManagedStoredQueryCount"/> capacity.
    /// </summary>
    /// <param name="definition">The stored query definition to persist.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<WfsStoredQueryCreateStatus> TryCreateAsync(
        WfsStoredQueryDefinition definition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the stored query with the given identifier.
    /// </summary>
    /// <param name="id">The stored query identifier (case-insensitive).</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when an entry was removed; otherwise <see langword="false"/>.</returns>
    Task<bool> TryRemoveAsync(string id, CancellationToken cancellationToken = default);
}
