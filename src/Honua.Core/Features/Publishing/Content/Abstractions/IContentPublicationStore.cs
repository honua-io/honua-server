// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Publishing.Content.Domain;

namespace Honua.Core.Features.Publishing.Content.Abstractions;

/// <summary>
/// Durable storage for the content publication registry. Version rows are
/// append-only; the route-state row is the only mutable pointer. Implementations
/// must enforce route-slug uniqueness and atomic route-state plus event writes.
/// </summary>
public interface IContentPublicationStore
{
    /// <summary>Returns the route state for a publication, or null when not found.</summary>
    Task<ContentPublicationRouteState?> GetRouteByPublicationIdAsync(
        string publicationId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the route state for a slug, or null when not found.</summary>
    Task<ContentPublicationRouteState?> GetRouteBySlugAsync(
        string routeSlug,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a specific immutable version by id, or null when not found.</summary>
    Task<ContentPublicationVersion?> GetVersionByIdAsync(
        string publicationId,
        string versionId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a specific immutable version by revision, or null when not found.</summary>
    Task<ContentPublicationVersion?> GetVersionByRevisionAsync(
        string publicationId,
        long revision,
        CancellationToken cancellationToken = default);

    /// <summary>Lists immutable versions for a publication, newest first, bounded by <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<ContentPublicationVersion>> ListVersionsAsync(
        string publicationId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the highest revision for a publication, or 0 when none exist.</summary>
    Task<long> GetMaxRevisionAsync(
        string publicationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically appends a new immutable version, upserts the route-state pointer,
    /// and appends an event. Used by publish (new route) and republish (existing route).
    /// </summary>
    /// <param name="version">The new immutable version row.</param>
    /// <param name="routeState">The route-state pointer to upsert.</param>
    /// <param name="auditEvent">The event to append.</param>
    /// <param name="expectedEtag">
    /// Expected current route etag for an existing route (republish); null when the
    /// route is being created for the first time (publish).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ContentPublicationConflictException">
    /// Thrown when the slug is already claimed by another publication, when the
    /// expected etag does not match, or when the revision/version id already exists.
    /// </exception>
    Task AppendVersionAndSetRouteAsync(
        ContentPublicationVersion version,
        ContentPublicationRouteState routeState,
        ContentPublicationEvent auditEvent,
        string? expectedEtag,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically updates the route-state pointer (without creating a version) and
    /// appends an event. Used by rollback and policy updates.
    /// </summary>
    /// <param name="routeState">The updated route-state pointer.</param>
    /// <param name="auditEvent">The event to append.</param>
    /// <param name="expectedEtag">Expected current route etag for optimistic concurrency.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ContentPublicationConflictException">Thrown on etag mismatch or missing route.</exception>
    Task SetRouteAsync(
        ContentPublicationRouteState routeState,
        ContentPublicationEvent auditEvent,
        string? expectedEtag,
        CancellationToken cancellationToken = default);

    /// <summary>Lists events for a publication, newest first, bounded by <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<ContentPublicationEvent>> ListEventsAsync(
        string publicationId,
        int limit,
        CancellationToken cancellationToken = default);
}
