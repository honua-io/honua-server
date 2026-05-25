// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.OpenData.Domain;

namespace Honua.Core.Features.OpenData.Abstractions;

/// <summary>
/// Persistence contract for Console open-data page state and publication controls.
/// </summary>
public interface IOpenDataStore
{
    /// <summary>
    /// Gets the open-data page record for a Console content item.
    /// </summary>
    /// <param name="itemId">Console content item identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page record, or null when no page state exists.</returns>
    Task<OpenDataPageRecord?> GetPageRecordAsync(string itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or replaces the open-data page record for a Console content item.
    /// </summary>
    /// <param name="record">Page record to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored page record.</returns>
    Task<OpenDataPageRecord> SetPageRecordAsync(OpenDataPageRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all open-data page records.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All stored page records.</returns>
    Task<IReadOnlyList<OpenDataPageRecord>> ListPageRecordsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a STAC publication record by collection identifier.
    /// </summary>
    /// <param name="collectionId">STAC collection identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The publication record, or null when no record exists.</returns>
    Task<OpenDataStacPublicationRecord?> GetStacPublicationAsync(string collectionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the latest STAC publication record for a Console content item.
    /// </summary>
    /// <param name="itemId">Console content item identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The publication record, or null when no record exists.</returns>
    Task<OpenDataStacPublicationRecord?> GetStacPublicationByItemAsync(string itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or replaces a STAC publication record.
    /// </summary>
    /// <param name="record">Publication record to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored publication record.</returns>
    Task<OpenDataStacPublicationRecord> SetStacPublicationAsync(
        OpenDataStacPublicationRecord record,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all STAC publication records.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All stored STAC publication records.</returns>
    Task<IReadOnlyList<OpenDataStacPublicationRecord>> ListStacPublicationsAsync(
        CancellationToken cancellationToken = default);
}
