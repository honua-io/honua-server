// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Abstractions;

/// <summary>
/// Editing surface for the addressable network-dataset registry (#1882). Where
/// <see cref="INetworkDatasetResolver"/> is the read-only solve-path projection, this
/// store is the admin CRUD surface: list / get / register / update / delete a dataset's
/// source-table mapping and metadata. It deliberately edits the dataset <em>registry</em>
/// (the mapping + metadata) and does not touch edge/vertex features or rebuild topology
/// — those remain deferred to a later phase.
/// </summary>
public interface INetworkDatasetStore
{
    /// <summary>
    /// Lists registered network datasets.
    /// </summary>
    /// <param name="includeDisabled">
    /// When <c>false</c> (default), <c>disabled</c> datasets are omitted.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<NetworkDatasetRecord>> ListAsync(
        bool includeDisabled = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a single dataset by id, or <c>null</c> when it does not exist.
    /// </summary>
    Task<NetworkDatasetRecord?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a new dataset. Throws <see cref="NetworkDatasetAlreadyExistsException"/>
    /// when a dataset with the same id is already registered.
    /// </summary>
    Task<NetworkDatasetRecord> RegisterAsync(
        NetworkDatasetRecord dataset,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the mutable fields of an existing dataset (name, edge/vertex tables, srid,
    /// status, description, attribution). Returns the saved record, or <c>null</c> when
    /// no dataset with that id exists.
    /// </summary>
    Task<NetworkDatasetRecord?> UpdateAsync(
        NetworkDatasetRecord dataset,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a dataset by id. Returns <c>true</c> when a row was removed.
    /// </summary>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Thrown when registering a network dataset whose id is already registered.
/// </summary>
public sealed class NetworkDatasetAlreadyExistsException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkDatasetAlreadyExistsException"/> class.
    /// </summary>
    public NetworkDatasetAlreadyExistsException(string id)
        : base($"A network dataset with id '{id}' is already registered.")
        => Id = id;

    /// <summary>Gets the conflicting dataset id.</summary>
    public string Id { get; }
}
