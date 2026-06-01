// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Console.Domain;

namespace Honua.Core.Features.Console.Abstractions;

/// <summary>
/// Persistence contract for Console open-data state: the editable open-data page
/// and the item's STAC publication lifecycle. Open-data state is intentionally
/// separate from <see cref="IConsoleContentStore"/> (item identity/visibility)
/// and <see cref="IConsoleShareStore"/> (the share access tier); eligibility and
/// anonymous-read policy are applied by the endpoint/service layer, not the store.
/// </summary>
public interface IConsoleOpenDataStore
{
    /// <summary>
    /// Returns the stored open-data page for an item, or <see langword="null"/>
    /// when no page has been authored yet.
    /// </summary>
    Task<ConsoleOpenDataPage?> GetPageAsync(string itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or replaces the open-data page for an item, stamping the audit
    /// fields. Returns the stored page.
    /// </summary>
    Task<ConsoleOpenDataPage> SavePageAsync(ConsoleOpenDataPage page, string? principalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the STAC publication state for an item. Implementations return an
    /// <see cref="ConsoleStacPublicationStatus.Unpublished"/> state (never null)
    /// for an item that has no recorded publication history.
    /// </summary>
    Task<ConsoleStacPublicationState> GetStacPublicationAsync(string itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Transitions the item's STAC publication to
    /// <see cref="ConsoleStacPublicationStatus.Published"/>, assigning a stable
    /// collection id on first publish and incrementing the revision. Idempotent
    /// re-publish/update increments the revision and refreshes the audit stamp.
    /// </summary>
    Task<ConsoleStacPublicationState> PublishStacAsync(string itemId, string? principalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Transitions the item's STAC publication to
    /// <see cref="ConsoleStacPublicationStatus.Unpublished"/>. Returns the updated
    /// state, or <see langword="null"/> when the item was not published.
    /// </summary>
    Task<ConsoleStacPublicationState?> UnpublishStacAsync(string itemId, string? principalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the item ids that currently have
    /// <see cref="ConsoleStacPublicationStatus.Published"/> STAC publication state,
    /// ordered by a stable key. Backs the anonymous STAC catalog root projection.
    /// </summary>
    Task<IReadOnlyList<string>> ListPublishedStacItemIdsAsync(CancellationToken cancellationToken = default);
}
