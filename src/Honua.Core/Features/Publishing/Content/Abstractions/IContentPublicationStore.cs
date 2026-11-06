// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Publishing.Content.Domain;

namespace Honua.Core.Features.Publishing.Content.Abstractions;

// Version rows are append-only, and route-slug uniqueness plus the route-state and event writes are atomic.
public interface IContentPublicationStore
{
    Task<ContentPublicationRouteState?> GetRouteByPublicationIdAsync(
        string publicationId,
        CancellationToken cancellationToken = default);

    Task<ContentPublicationRouteState?> GetRouteBySlugAsync(
        string routeSlug,
        CancellationToken cancellationToken = default);

    Task<ContentPublicationVersion?> GetVersionByIdAsync(
        string publicationId,
        string versionId,
        CancellationToken cancellationToken = default);

    Task<ContentPublicationVersion?> GetVersionByRevisionAsync(
        string publicationId,
        long revision,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContentPublicationVersion>> ListVersionsAsync(
        string publicationId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, IReadOnlyList<ContentPublicationVersion>>> ListVersionsByPublicationIdsAsync(
        IReadOnlyCollection<string> publicationIds,
        int limit,
        CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<ContentPublicationRouteState> Routes, int Total)> ListRoutesAsync(
        ContentPublicationLifecycle? lifecycle,
        int offset,
        int limit,
        CancellationToken cancellationToken = default);

    Task<long> GetMaxRevisionAsync(
        string publicationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, ContentPublicationRouteState>> GetLatestRouteStatesBySourceContentIdsAsync(
        IReadOnlyCollection<string> sourceContentIds,
        CancellationToken cancellationToken = default);

    Task AppendVersionAndSetRouteAsync(
        ContentPublicationVersion version,
        ContentPublicationRouteState routeState,
        ContentPublicationEvent auditEvent,
        string? expectedEtag,
        CancellationToken cancellationToken = default);

    Task SetRouteAsync(
        ContentPublicationRouteState routeState,
        ContentPublicationEvent auditEvent,
        string? expectedEtag,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContentPublicationEvent>> ListEventsAsync(
        string publicationId,
        int limit,
        CancellationToken cancellationToken = default);
}
