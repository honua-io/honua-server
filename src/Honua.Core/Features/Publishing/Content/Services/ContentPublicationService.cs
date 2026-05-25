// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Core.Features.Publishing.Content.Abstractions;
using Honua.Core.Features.Publishing.Content.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Publishing.Content.Services;

/// <summary>
/// Default <see cref="IContentPublicationService"/>. Validates requests, normalizes
/// route slugs, allocates monotonic revisions, hashes content, validates dependency
/// references against registered stores, and writes route-state changes atomically
/// alongside append-only events. Optional dependency stores are injected as nullable
/// so the slice degrades gracefully when those features are not registered.
/// </summary>
public sealed class ContentPublicationService : IContentPublicationService
{
    private const int VersionPageSize = 100;
    private const string RoutePathPrefix = "/api/v1/published/";

    private readonly IContentPublicationStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly IMetadataV2GraphProvider? _graphProvider;
    private readonly IPublishedServiceStore? _publishedServiceStore;
    private readonly ILogger<ContentPublicationService> _logger;

    /// <summary>Initializes a new instance.</summary>
    public ContentPublicationService(
        IContentPublicationStore store,
        TimeProvider timeProvider,
        IMetadataV2GraphProvider? graphProvider,
        IPublishedServiceStore? publishedServiceStore,
        ILogger<ContentPublicationService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _graphProvider = graphProvider;
        _publishedServiceStore = publishedServiceStore;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ContentPublicationDetail> PublishAsync(
        PublishContentRequest request,
        string actor,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateKind(request.Kind);
        ValidateBbox(request.DefaultViewBbox);
        ValidatePolicyShape(request.Policy);

        var slug = ContentPublicationSlug.Normalize(request.RouteSlug, request.Title);

        var existing = await _store.GetRouteBySlugAsync(slug, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            throw new ContentPublicationConflictException($"Route slug '{slug}' is already claimed by another publication.");
        }

        await ValidateDependenciesAsync(request.Dependencies, cancellationToken).ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        var publicationId = Guid.NewGuid().ToString("D");
        var versionId = Guid.NewGuid().ToString("D");
        var routePath = RoutePathPrefix + slug;
        var policy = request.Policy ?? new ContentPublicationPolicy();
        var contentHash = ResolveContentHash(request.ContentPayload, request.ContentHash);

        var version = new ContentPublicationVersion
        {
            PublicationId = publicationId,
            VersionId = versionId,
            Revision = 1,
            Kind = request.Kind,
            RouteSlug = slug,
            RoutePath = routePath,
            Title = request.Title,
            SourceContentId = request.SourceContentId,
            SourcePackageId = request.SourcePackageId,
            ContentHash = contentHash,
            ContentVersionId = request.ContentVersionId,
            SourceMetadataRevision = request.SourceMetadataRevision,
            SourceMetadataEtag = request.SourceMetadataEtag,
            AppManifestId = request.AppManifestId,
            AppBundleArtifactId = request.AppBundleArtifactId,
            DefaultViewBbox = request.DefaultViewBbox,
            Policy = policy,
            Dependencies = request.Dependencies ?? [],
            Provenance = request.Provenance ?? [],
            JobId = request.JobId,
            OperationId = request.OperationId,
            CorrelationId = correlationId,
            CreatedBy = actor,
            CreatedAt = now,
        };

        var route = new ContentPublicationRouteState
        {
            PublicationId = publicationId,
            RouteSlug = slug,
            RoutePath = routePath,
            Kind = request.Kind,
            ActiveVersionId = versionId,
            ActiveRevision = 1,
            PreviousVersionId = null,
            RollbackTargetVersionId = null,
            Lifecycle = ContentPublicationLifecycle.Active,
            Policy = policy,
            Generation = 1,
            Etag = ContentPublicationCrypto.NewEtag(),
            UpdatedBy = actor,
            UpdatedAt = now,
            CreatedAt = now,
        };

        var @event = BuildEvent(publicationId, ContentPublicationOperation.Publish, versionId, 1, slug, actor, correlationId, now, detail: null);
        await _store.AppendVersionAndSetRouteAsync(version, route, @event, expectedEtag: null, cancellationToken).ConfigureAwait(false);

        ContentPublicationServiceLog.Published(_logger, publicationId, 1, request.Kind, slug, versionId);
        return new ContentPublicationDetail { Route = route, Versions = [version] };
    }

    /// <inheritdoc />
    public async Task<ContentPublicationDetail?> GetAsync(string publicationId, CancellationToken cancellationToken = default)
    {
        ValidatePublicationId(publicationId);

        var route = await _store.GetRouteByPublicationIdAsync(publicationId, cancellationToken).ConfigureAwait(false);
        if (route is null)
        {
            return null;
        }

        var versions = await _store.ListVersionsAsync(publicationId, VersionPageSize, cancellationToken).ConfigureAwait(false);
        return new ContentPublicationDetail { Route = route, Versions = versions };
    }

    /// <inheritdoc />
    public async Task<ContentPublicationVersion?> GetVersionAsync(string publicationId, string versionSelector, CancellationToken cancellationToken = default)
    {
        ValidatePublicationId(publicationId);

        if (string.IsNullOrWhiteSpace(versionSelector))
        {
            throw new ContentPublicationValidationException("A version selector is required.");
        }

        if (TryParseRevision(versionSelector, out var revision))
        {
            return await _store.GetVersionByRevisionAsync(publicationId, revision, cancellationToken).ConfigureAwait(false);
        }

        if (Guid.TryParse(versionSelector, out _))
        {
            return await _store.GetVersionByIdAsync(publicationId, versionSelector, cancellationToken).ConfigureAwait(false);
        }

        throw new ContentPublicationValidationException("Version selector must be a revision number, 'v{n}', or a version id.");
    }

    /// <inheritdoc />
    public async Task<ContentPublicationDetail> RepublishAsync(
        string publicationId,
        RepublishContentRequest request,
        string actor,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePublicationId(publicationId);
        ValidateBbox(request.DefaultViewBbox);

        var route = await _store.GetRouteByPublicationIdAsync(publicationId, cancellationToken).ConfigureAwait(false)
            ?? throw new ContentPublicationNotFoundException("Publication not found.");
        EnsureEtagMatches(route, request.ExpectedEtag);

        await ValidateDependenciesAsync(request.Dependencies, cancellationToken).ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        var maxRevision = await _store.GetMaxRevisionAsync(publicationId, cancellationToken).ConfigureAwait(false);
        var newRevision = maxRevision + 1;
        var versionId = Guid.NewGuid().ToString("D");
        var contentHash = ResolveContentHash(request.ContentPayload, request.ContentHash);

        var version = new ContentPublicationVersion
        {
            PublicationId = publicationId,
            VersionId = versionId,
            Revision = newRevision,
            Kind = route.Kind,
            RouteSlug = route.RouteSlug,
            RoutePath = route.RoutePath,
            Title = request.Title,
            SourceContentId = request.SourceContentId,
            SourcePackageId = request.SourcePackageId,
            ContentHash = contentHash,
            ContentVersionId = request.ContentVersionId,
            SourceMetadataRevision = request.SourceMetadataRevision,
            SourceMetadataEtag = request.SourceMetadataEtag,
            AppManifestId = request.AppManifestId,
            AppBundleArtifactId = request.AppBundleArtifactId,
            DefaultViewBbox = request.DefaultViewBbox,
            Policy = route.Policy,
            Dependencies = request.Dependencies ?? [],
            Provenance = request.Provenance ?? [],
            JobId = request.JobId,
            OperationId = request.OperationId,
            CorrelationId = correlationId,
            CreatedBy = actor,
            CreatedAt = now,
        };

        var updatedRoute = route with
        {
            ActiveVersionId = versionId,
            ActiveRevision = newRevision,
            PreviousVersionId = route.ActiveVersionId,
            RollbackTargetVersionId = null,
            Generation = route.Generation + 1,
            Etag = ContentPublicationCrypto.NewEtag(),
            UpdatedBy = actor,
            UpdatedAt = now,
        };

        var @event = BuildEvent(publicationId, ContentPublicationOperation.Republish, versionId, newRevision, route.RouteSlug, actor, correlationId, now, detail: null);
        await _store.AppendVersionAndSetRouteAsync(version, updatedRoute, @event, expectedEtag: route.Etag, cancellationToken).ConfigureAwait(false);

        ContentPublicationServiceLog.Republished(_logger, publicationId, newRevision, route.RouteSlug, versionId);
        var versions = await _store.ListVersionsAsync(publicationId, VersionPageSize, cancellationToken).ConfigureAwait(false);
        return new ContentPublicationDetail { Route = updatedRoute, Versions = versions };
    }

    /// <inheritdoc />
    public async Task<ContentPublicationDetail> RollbackAsync(
        string publicationId,
        RollbackContentRequest request,
        string actor,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePublicationId(publicationId);

        var route = await _store.GetRouteByPublicationIdAsync(publicationId, cancellationToken).ConfigureAwait(false)
            ?? throw new ContentPublicationNotFoundException("Publication not found.");
        EnsureEtagMatches(route, request.ExpectedEtag);

        ContentPublicationVersion? target;
        if (!string.IsNullOrWhiteSpace(request.TargetVersionId))
        {
            target = await _store.GetVersionByIdAsync(publicationId, request.TargetVersionId!, cancellationToken).ConfigureAwait(false);
        }
        else if (request.TargetRevision is { } targetRevision)
        {
            target = await _store.GetVersionByRevisionAsync(publicationId, targetRevision, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new ContentPublicationValidationException("A rollback target (targetVersionId or targetRevision) is required.");
        }

        if (target is null)
        {
            throw new ContentPublicationValidationException("The rollback target version was not found.");
        }

        if (string.Equals(target.VersionId, route.ActiveVersionId, StringComparison.Ordinal))
        {
            throw new ContentPublicationValidationException("The rollback target is already the active version.");
        }

        var now = _timeProvider.GetUtcNow();
        var updatedRoute = route with
        {
            ActiveVersionId = target.VersionId,
            ActiveRevision = target.Revision,
            PreviousVersionId = route.ActiveVersionId,
            RollbackTargetVersionId = target.VersionId,
            Generation = route.Generation + 1,
            Etag = ContentPublicationCrypto.NewEtag(),
            UpdatedBy = actor,
            UpdatedAt = now,
        };

        var @event = BuildEvent(publicationId, ContentPublicationOperation.Rollback, target.VersionId, target.Revision, route.RouteSlug, actor, correlationId, now, detail: null);
        await _store.SetRouteAsync(updatedRoute, @event, expectedEtag: route.Etag, cancellationToken).ConfigureAwait(false);

        ContentPublicationServiceLog.RolledBack(_logger, publicationId, target.Revision, route.RouteSlug, target.VersionId);
        var versions = await _store.ListVersionsAsync(publicationId, VersionPageSize, cancellationToken).ConfigureAwait(false);
        return new ContentPublicationDetail { Route = updatedRoute, Versions = versions };
    }

    /// <inheritdoc />
    public async Task<ContentPublicationPolicyUpdateResult> UpdatePolicyAsync(
        string publicationId,
        UpdatePublicationPolicyRequest request,
        string actor,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePublicationId(publicationId);

        var route = await _store.GetRouteByPublicationIdAsync(publicationId, cancellationToken).ConfigureAwait(false)
            ?? throw new ContentPublicationNotFoundException("Publication not found.");
        EnsureEtagMatches(route, request.ExpectedEtag);

        if (request.Embed is not null)
        {
            ValidateEmbedPolicy(request.Embed);
        }

        var now = _timeProvider.GetUtcNow();
        var current = route.Policy;

        // Apply scalar policy deltas.
        var share = request.Share ?? current.Share;
        var embed = request.Embed ?? current.Embed;
        var service = request.Service ?? current.Service;
        var visibility = request.Visibility ?? current.Visibility;
        var access = request.Access ?? current.Access;

        // Public-link mutations operate on a copy of the existing link list.
        var links = current.PublicLink.Links.ToList();
        var publicLinkEnabled = request.PublicLinkEnabled ?? current.PublicLink.Enabled;
        string? createdLinkId = null;
        string? createdRawToken = null;
        var operation = ContentPublicationOperation.PolicyUpdate;

        if (request.RevokePublicLinkId is { Length: > 0 } revokeId)
        {
            var index = links.FindIndex(l => string.Equals(l.LinkId, revokeId, StringComparison.Ordinal));
            if (index < 0)
            {
                throw new ContentPublicationValidationException("The public link to revoke was not found.");
            }

            links[index] = links[index] with { Revoked = true, RevokedAt = now };
            operation = ContentPublicationOperation.PublicLinkRevoke;
        }

        if (request.CreatePublicLink is { } linkRequest)
        {
            createdLinkId = Guid.NewGuid().ToString("N");
            string? tokenHash = null;
            string? algorithm = null;
            if (!string.IsNullOrEmpty(linkRequest.Token))
            {
                createdRawToken = linkRequest.Token;
                tokenHash = ContentPublicationCrypto.HashToken(linkRequest.Token!);
                algorithm = ContentPublicationCrypto.TokenHashAlgorithm;
            }

            links.Add(new ContentPublicLink
            {
                LinkId = createdLinkId,
                TokenHash = tokenHash,
                TokenHashAlgorithm = algorithm,
                Label = linkRequest.Label,
                CreatedBy = actor,
                CreatedAt = now,
                ExpiresAt = linkRequest.ExpiresAt,
                Revoked = false,
            });
            // Creating a link implicitly enables the feature for the route.
            publicLinkEnabled = true;
            operation = ContentPublicationOperation.PublicLinkCreate;
        }

        var newPolicy = current with
        {
            Visibility = visibility,
            Access = access,
            Share = share,
            Embed = embed,
            Service = service,
            PublicLink = new ContentPublicLinkPolicy { Enabled = publicLinkEnabled, Links = links },
        };

        var updatedRoute = route with
        {
            Policy = newPolicy,
            Generation = route.Generation + 1,
            Etag = ContentPublicationCrypto.NewEtag(),
            UpdatedBy = actor,
            UpdatedAt = now,
        };

        var @event = BuildEvent(publicationId, operation, route.ActiveVersionId, route.ActiveRevision, route.RouteSlug, actor, correlationId, now, detail: null);
        await _store.SetRouteAsync(updatedRoute, @event, expectedEtag: route.Etag, cancellationToken).ConfigureAwait(false);

        ContentPublicationServiceLog.PolicyUpdated(_logger, publicationId, route.RouteSlug, operation);
        var versions = await _store.ListVersionsAsync(publicationId, VersionPageSize, cancellationToken).ConfigureAwait(false);
        return new ContentPublicationPolicyUpdateResult
        {
            Detail = new ContentPublicationDetail { Route = updatedRoute, Versions = versions },
            CreatedPublicLinkId = createdLinkId,
            CreatedPublicLinkToken = createdRawToken,
        };
    }

    /// <inheritdoc />
    public async Task<ContentPublicationRouteState?> ResolveRouteAsync(string routeSlug, CancellationToken cancellationToken = default)
    {
        if (!ContentPublicationSlug.TryNormalize(routeSlug, out var slug))
        {
            return null;
        }

        return await _store.GetRouteBySlugAsync(slug, cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureEtagMatches(ContentPublicationRouteState route, string? expectedEtag)
    {
        if (expectedEtag is { Length: > 0 } && !string.Equals(route.Etag, expectedEtag, StringComparison.Ordinal))
        {
            throw new ContentPublicationConflictException("Route was modified concurrently (etag mismatch).");
        }
    }

    private static void ValidatePublicationId(string publicationId)
    {
        if (!Guid.TryParse(publicationId, out _))
        {
            throw new ContentPublicationValidationException("publicationId must be a valid GUID.");
        }
    }

    private static string? ResolveContentHash(string? payload, string? precomputedHash)
    {
        if (!string.IsNullOrEmpty(payload))
        {
            return ContentPublicationCrypto.Sha256Hex(payload);
        }

        return string.IsNullOrWhiteSpace(precomputedHash) ? null : precomputedHash;
    }

    private static void ValidateKind(ContentPublicationKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ContentPublicationValidationException("kind is not a defined content publication kind.");
        }
    }

    private static void ValidatePolicyShape(ContentPublicationPolicy? policy)
    {
        if (policy?.Embed is { } embed)
        {
            ValidateEmbedPolicy(embed);
        }
    }

    private static void ValidateEmbedPolicy(ContentEmbedPolicy embed)
    {
        if (!embed.AllowEmbedding)
        {
            return;
        }

        if (embed.AllowedOrigins is { } origins)
        {
            foreach (var origin in origins)
            {
                if (string.IsNullOrWhiteSpace(origin) || origin.Contains(' ', StringComparison.Ordinal))
                {
                    throw new ContentPublicationValidationException("embed.allowedOrigins entries must be non-empty origins without whitespace.");
                }
            }
        }
    }

    private static void ValidateBbox(ContentPublicationBbox? bbox)
    {
        if (bbox is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(bbox.Crs))
        {
            throw new ContentPublicationValidationException("defaultViewBbox.crs must be declared.");
        }

        if (bbox.MinX > bbox.MaxX || bbox.MinY > bbox.MaxY)
        {
            throw new ContentPublicationValidationException("defaultViewBbox minimum must not exceed maximum.");
        }

        if (IsWgs84(bbox.Crs)
            && (bbox.MinX < -180 || bbox.MaxX > 180 || bbox.MinY < -90 || bbox.MaxY > 90))
        {
            throw new ContentPublicationValidationException("defaultViewBbox is out of range for EPSG:4326 lon/lat.");
        }
    }

    private static bool IsWgs84(string crs)
        => crs.Contains("4326", StringComparison.OrdinalIgnoreCase)
            || crs.Contains("CRS84", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseRevision(string selector, out long revision)
    {
        var trimmed = selector.Trim();
        if (trimmed.Length > 1 && (trimmed[0] == 'v' || trimmed[0] == 'V'))
        {
            trimmed = trimmed[1..];
        }

        return long.TryParse(trimmed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out revision)
            && revision > 0;
    }

    private async Task ValidateDependenciesAsync(IReadOnlyList<ContentPublicationDependencyRef>? dependencies, CancellationToken cancellationToken)
    {
        if (dependencies is null || dependencies.Count == 0)
        {
            return;
        }

        MetadataV2GraphSnapshot? snapshot = null;
        foreach (var dependency in dependencies)
        {
            if (string.IsNullOrWhiteSpace(dependency.RefId))
            {
                throw new ContentPublicationValidationException("Each dependency must carry a non-empty refId.");
            }

            switch (dependency.Kind)
            {
                case ContentPublicationDependencyKind.Service:
                case ContentPublicationDependencyKind.Resource:
                case ContentPublicationDependencyKind.Publication:
                    if (_graphProvider is null)
                    {
                        // Graph not registered in this deployment: cannot validate, accept opaquely.
                        break;
                    }

                    snapshot ??= await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
                    if (!GraphContains(snapshot, dependency.Kind, dependency.RefId))
                    {
                        throw new ContentPublicationDependencyException(
                            $"Dependency '{dependency.Kind}' '{dependency.RefId}' was not found in the Metadata v2 graph.");
                    }

                    break;

                case ContentPublicationDependencyKind.PublishedService:
                    if (_publishedServiceStore is null)
                    {
                        throw new ContentPublicationDependencyException(
                            "Published service registry is unavailable; cannot validate published-service dependency.",
                            statusCode: 503);
                    }

                    var record = await _publishedServiceStore.GetAsync(dependency.RefId, cancellationToken).ConfigureAwait(false);
                    if (record is null)
                    {
                        throw new ContentPublicationDependencyException($"Published service '{dependency.RefId}' was not found.");
                    }

                    break;

                default:
                    // Deployment / map-package / app-package / result-package / report / provenance-item:
                    // no canonical store is wired in this ticket, so references are accepted opaquely
                    // rather than fabricating package state.
                    break;
            }
        }
    }

    private static bool GraphContains(MetadataV2GraphSnapshot snapshot, ContentPublicationDependencyKind kind, string refId)
        => kind switch
        {
            ContentPublicationDependencyKind.Service => snapshot.Index.ServicesById.ContainsKey(refId),
            ContentPublicationDependencyKind.Resource => snapshot.Index.ResourcesById.ContainsKey(refId),
            ContentPublicationDependencyKind.Publication => snapshot.Index.PublicationsById.ContainsKey(refId),
            _ => true,
        };

    private static ContentPublicationEvent BuildEvent(
        string publicationId,
        ContentPublicationOperation operation,
        string? versionId,
        long? revision,
        string routeSlug,
        string actor,
        string? correlationId,
        DateTimeOffset now,
        string? detail)
        => new()
        {
            EventId = Guid.NewGuid().ToString("D"),
            PublicationId = publicationId,
            Operation = operation,
            VersionId = versionId,
            Revision = revision,
            RouteSlug = routeSlug,
            Actor = actor,
            CorrelationId = correlationId,
            Detail = detail,
            CreatedAt = now,
        };
}
