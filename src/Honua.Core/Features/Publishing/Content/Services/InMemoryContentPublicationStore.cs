// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Publishing.Content.Abstractions;
using Honua.Core.Features.Publishing.Content.Domain;

namespace Honua.Core.Features.Publishing.Content.Services;

/// <summary>
/// In-memory <see cref="IContentPublicationStore"/> for unit tests and non-durable
/// deployments. Enforces route-slug uniqueness, append-only version rows, and
/// atomic route-state plus event writes under a single lock. Durable deployments
/// use the Postgres-backed store instead.
/// </summary>
public sealed class InMemoryContentPublicationStore : IContentPublicationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ContentPublicationRouteState> _routesByPublicationId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _publicationIdBySlug = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ContentPublicationVersion>> _versionsByPublicationId = new(StringComparer.Ordinal);
    private readonly List<ContentPublicationEvent> _events = [];
    private long _eventSequence;

    /// <inheritdoc />
    public Task<ContentPublicationRouteState?> GetRouteByPublicationIdAsync(string publicationId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_routesByPublicationId.TryGetValue(publicationId, out var route) ? route : null);
        }
    }

    /// <inheritdoc />
    public Task<ContentPublicationRouteState?> GetRouteBySlugAsync(string routeSlug, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_publicationIdBySlug.TryGetValue(routeSlug, out var publicationId)
                && _routesByPublicationId.TryGetValue(publicationId, out var route))
            {
                return Task.FromResult<ContentPublicationRouteState?>(route);
            }

            return Task.FromResult<ContentPublicationRouteState?>(null);
        }
    }

    /// <inheritdoc />
    public Task<ContentPublicationVersion?> GetVersionByIdAsync(string publicationId, string versionId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_versionsByPublicationId.TryGetValue(publicationId, out var versions))
            {
                foreach (var version in versions)
                {
                    if (string.Equals(version.VersionId, versionId, StringComparison.Ordinal))
                    {
                        return Task.FromResult<ContentPublicationVersion?>(version);
                    }
                }
            }

            return Task.FromResult<ContentPublicationVersion?>(null);
        }
    }

    /// <inheritdoc />
    public Task<ContentPublicationVersion?> GetVersionByRevisionAsync(string publicationId, long revision, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_versionsByPublicationId.TryGetValue(publicationId, out var versions))
            {
                foreach (var version in versions)
                {
                    if (version.Revision == revision)
                    {
                        return Task.FromResult<ContentPublicationVersion?>(version);
                    }
                }
            }

            return Task.FromResult<ContentPublicationVersion?>(null);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentPublicationVersion>> ListVersionsAsync(string publicationId, int limit, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_versionsByPublicationId.TryGetValue(publicationId, out var versions))
            {
                return Task.FromResult<IReadOnlyList<ContentPublicationVersion>>([]);
            }

            var result = versions
                .OrderByDescending(v => v.Revision)
                .Take(Math.Max(0, limit))
                .ToArray();
            return Task.FromResult<IReadOnlyList<ContentPublicationVersion>>(result);
        }
    }

    /// <inheritdoc />
    public Task<long> GetMaxRevisionAsync(string publicationId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_versionsByPublicationId.TryGetValue(publicationId, out var versions) && versions.Count > 0)
            {
                return Task.FromResult(versions.Max(v => v.Revision));
            }

            return Task.FromResult(0L);
        }
    }

    /// <inheritdoc />
    public Task AppendVersionAndSetRouteAsync(
        ContentPublicationVersion version,
        ContentPublicationRouteState routeState,
        ContentPublicationEvent auditEvent,
        string? expectedEtag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(routeState);
        ArgumentNullException.ThrowIfNull(auditEvent);

        lock (_gate)
        {
            var publicationId = routeState.PublicationId;
            var hasExistingRoute = _routesByPublicationId.TryGetValue(publicationId, out var existingRoute);

            if (expectedEtag is null)
            {
                // Publish-new: the publication and slug must be free.
                if (hasExistingRoute)
                {
                    throw new ContentPublicationConflictException("Publication already exists; use republish.");
                }

                if (_publicationIdBySlug.ContainsKey(routeState.RouteSlug))
                {
                    throw new ContentPublicationConflictException(
                        $"Route slug '{routeState.RouteSlug}' is already claimed by another publication.");
                }
            }
            else
            {
                // Republish-existing: the route must exist and the etag must match.
                if (!hasExistingRoute)
                {
                    throw new ContentPublicationConflictException("Publication not found for republish.");
                }

                if (!string.Equals(existingRoute!.Etag, expectedEtag, StringComparison.Ordinal))
                {
                    throw new ContentPublicationConflictException("Route was modified concurrently (etag mismatch).");
                }
            }

            if (!_versionsByPublicationId.TryGetValue(publicationId, out var versions))
            {
                versions = [];
                _versionsByPublicationId[publicationId] = versions;
            }

            foreach (var existing in versions)
            {
                if (existing.Revision == version.Revision
                    || string.Equals(existing.VersionId, version.VersionId, StringComparison.Ordinal))
                {
                    throw new ContentPublicationConflictException("Version revision or id already exists.");
                }
            }

            versions.Add(version);
            _routesByPublicationId[publicationId] = routeState;
            _publicationIdBySlug[routeState.RouteSlug] = publicationId;
            AppendEvent(auditEvent);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetRouteAsync(
        ContentPublicationRouteState routeState,
        ContentPublicationEvent auditEvent,
        string? expectedEtag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routeState);
        ArgumentNullException.ThrowIfNull(auditEvent);

        lock (_gate)
        {
            if (!_routesByPublicationId.TryGetValue(routeState.PublicationId, out var existingRoute))
            {
                throw new ContentPublicationConflictException("Publication not found.");
            }

            if (expectedEtag is not null && !string.Equals(existingRoute.Etag, expectedEtag, StringComparison.Ordinal))
            {
                throw new ContentPublicationConflictException("Route was modified concurrently (etag mismatch).");
            }

            _routesByPublicationId[routeState.PublicationId] = routeState;
            AppendEvent(auditEvent);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentPublicationEvent>> ListEventsAsync(string publicationId, int limit, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var result = _events
                .Where(e => string.Equals(e.PublicationId, publicationId, StringComparison.Ordinal))
                .OrderByDescending(e => e.Sequence)
                .Take(Math.Max(0, limit))
                .ToArray();
            return Task.FromResult<IReadOnlyList<ContentPublicationEvent>>(result);
        }
    }

    private void AppendEvent(ContentPublicationEvent auditEvent)
    {
        _eventSequence++;
        _events.Add(auditEvent with { Sequence = _eventSequence });
    }
}
