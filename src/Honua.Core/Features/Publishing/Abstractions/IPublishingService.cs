// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Publishing.Domain;

namespace Honua.Core.Features.Publishing.Abstractions;

/// <summary>
/// Store for persisting and querying publish intents.
/// </summary>
public interface IPublishIntentStore
{
    /// <summary>
    /// Attempts to create a new publish intent.
    /// </summary>
    /// <param name="intent">Publish intent to create.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when created; false if an intent with the same ID already exists.</returns>
    Task<bool> TryCreateAsync(
        PublishIntent intent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a publish intent by identifier.
    /// </summary>
    /// <param name="intentId">Publish intent identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The publish intent or null when not found.</returns>
    Task<PublishIntent?> GetAsync(
        string intentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the latest publish intent state.
    /// </summary>
    /// <param name="intent">Updated publish intent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetAsync(
        PublishIntent intent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists publish intents associated with a specific source.
    /// </summary>
    /// <param name="sourceId">Source identifier to filter by.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Publish intents for the given source.</returns>
    Task<IReadOnlyList<PublishIntent>> ListBySourceAsync(
        string sourceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for persisting and querying published service records.
/// </summary>
public interface IPublishedServiceStore
{
    /// <summary>
    /// Attempts to create a new published service record.
    /// </summary>
    /// <param name="service">Published service record to create.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when created; false if a service with the same ID already exists.</returns>
    Task<bool> TryCreateAsync(
        PublishedServiceRecord service,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a published service record by identifier.
    /// </summary>
    /// <param name="serviceId">Published service identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The published service record or null when not found.</returns>
    Task<PublishedServiceRecord?> GetAsync(
        string serviceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the latest published service record state.
    /// </summary>
    /// <param name="service">Updated published service record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetAsync(
        PublishedServiceRecord service,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists published service records that are not decommissioned.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Active published service records.</returns>
    Task<IReadOnlyList<PublishedServiceRecord>> ListActiveAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists published service records associated with a specific source.
    /// </summary>
    /// <param name="sourceId">Source identifier to filter by.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Published services for the given source.</returns>
    Task<IReadOnlyList<PublishedServiceRecord>> ListBySourceAsync(
        string sourceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes the publishing pipeline, materializing artifacts from a publish intent
/// into a managed published service.
/// </summary>
public interface IPublishExecutor
{
    /// <summary>
    /// Executes the publishing pipeline for the given intent.
    /// </summary>
    /// <param name="intent">Validated and approved publish intent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resulting published service record.</returns>
    Task<PublishedServiceRecord> ExecuteAsync(
        PublishIntent intent,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes a one-way refresh operation, pulling source content into a published service.
/// Refresh is strictly one-way from the authoritative source; bidirectional sync and CDC
/// are out of scope.
/// </summary>
public interface IRefreshExecutor
{
    /// <summary>
    /// Executes a refresh for the given request.
    /// </summary>
    /// <param name="request">Refresh request specifying the service and scope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the refresh operation.</returns>
    Task<RefreshResult> ExecuteAsync(
        RefreshRequest request,
        CancellationToken cancellationToken = default);
}
