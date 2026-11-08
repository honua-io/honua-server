// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.Extensions.DependencyInjection;
using AccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Infrastructure.Validation;

internal static class LayerValidationHelpers
{
    internal static Func<HttpContext, string, string, int, IResult>? ODataErrorFactoryOverride { get; set; }

    internal static Func<HttpContext, CancellationToken>? ODataCancellationTokenResolverOverride { get; set; }
    internal enum ValidationProtocol
    {
        OData,
        OgcFeatures,

        // A tile route must not use the path-classified GeoServices formatter, which would wrap its error as HTTP 200.
        ProblemJson
    }

    internal readonly record struct MetadataV2ValidationResult(
        bool IsValid,
        MetadataV2Publication? Publication,
        MetadataV2Resource? Resource,
        MetadataV2Service? Service,
        IResult? ErrorResult,
        MetadataV2GraphSnapshot? Snapshot = null);

    private static IResult CreateODataError(HttpContext context, string message, int statusCode)
    {
        var errorCode = statusCode switch
        {
            400 => "InvalidRequest",
            404 => "ResourceNotFound",
            _ => "Error"
        };

        var factory = ODataErrorFactoryOverride;
        if (factory is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, message);
        }
        return factory(context, errorCode, message, statusCode);
    }

    private static IResult CreateOgcError(HttpContext context, string message, int statusCode)
    {
        return statusCode switch
        {
            400 => StandardErrorHelpers.CreateBadRequest(context, message),
            404 => StandardErrorHelpers.CreateNotFound(context, message),
            _ => StandardErrorHelpers.CreateInternalServerError(context, message)
        };
    }

    private static IResult CreateProblemJsonError(HttpContext context, string message, int statusCode)
    {
        var title = statusCode switch
        {
            StatusCodes.Status400BadRequest => "Bad Request",
            StatusCodes.Status401Unauthorized => "Unauthorized",
            StatusCodes.Status403Forbidden => "Forbidden",
            StatusCodes.Status404NotFound => "Not Found",
            _ => "Error"
        };
        return ProblemDetailsHelpers.CreateProblem(context, "about:blank", statusCode, title, message);
    }

    private static async Task<IResult?> RequireResourceAccessAsProblemJsonAsync(
        HttpContext context,
        MetadataV2Resource resource,
        MetadataV2Service? service,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        var decision = await AccessPolicyHelpers.EvaluateResourceAccessAsync(
            context,
            resource,
            service,
            AccessPolicyHelpers.DefaultOperationForScope(scope),
            cancellationToken).ConfigureAwait(false);
        if (decision.IsAllowed)
        {
            return null;
        }

        return decision.RequiresAuthentication
            ? CreateProblemJsonError(context, AccessPolicyHelpers.AuthRequiredMessage, StatusCodes.Status401Unauthorized)
            : CreateProblemJsonError(context, AccessPolicyHelpers.AccessForbiddenMessage, StatusCodes.Status403Forbidden);
    }

    // ---- Metadata v2 validation. Resolves a (publication, resource, service) triple
    // from the V2 graph snapshot for the given layer id (or collection id) and applies
    // the same access-policy + protocol-enablement checks as the v1 methods above.

    public static async Task<MetadataV2ValidationResult> ValidateLayerWithAccessV2Async(
        HttpContext context,
        int layerId,
        ValidationProtocol protocol,
        AccessScope scope = AccessScope.Read,
        string? requiredProtocol = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveToken = protocol == ValidationProtocol.OData && ODataCancellationTokenResolverOverride is not null
            ? ODataCancellationTokenResolverOverride(context)
            : cancellationToken;

        var snapshot = await GetV2SnapshotAsync(context, effectiveToken).ConfigureAwait(false);
        var (publication, resource, service) = ResolveV2Triple(context, snapshot, layerId, requiredProtocol);

        if (publication is null || !snapshot.IsRoutable(publication))
        {
            if (ChallengeTenantHiddenLayer(context, snapshot, layerId, requiredProtocol, protocol) is { } challenge)
            {
                return new MetadataV2ValidationResult(false, null, null, null, challenge);
            }

            var msg = $"Layer {layerId} not found";
            var error = protocol switch
            {
                ValidationProtocol.OData => CreateODataError(context, msg, StatusCodes.Status404NotFound),
                ValidationProtocol.OgcFeatures => CreateOgcError(context, msg, StatusCodes.Status404NotFound),
                ValidationProtocol.ProblemJson => CreateProblemJsonError(context, msg, StatusCodes.Status404NotFound),
                _ => StandardErrorHelpers.CreateNotFound(context, msg),
            };
            return new MetadataV2ValidationResult(false, null, null, null, error);
        }

        // Per-operation RBAC grants are consulted first (#1376) via the shared
        // seam; the coarse AccessPolicy is the no-grant fallback. This central
        // validator is the enforcement point for OData / OGC API Features / WFS /
        // tiles / STAC layer reads & mutations, so wiring it here re-routes those
        // adapters through the resolver consistently.
        // /tiles' ProblemJson opt-in must also govern authorization failures, not just
        // not-found: AccessPolicyHelpers.RequireResourceAccessAsync formats denials via
        // StandardErrorHelpers, which the path-classified StandardErrorResponseFormatter
        // would otherwise wrap as a GeoServices 200-envelope for /tiles (honua-server#2945
        // review). Evaluate the decision directly and format it as problem+json here.
        var accessError = protocol == ValidationProtocol.ProblemJson
            ? await RequireResourceAccessAsProblemJsonAsync(context, resource!, service, scope, effectiveToken).ConfigureAwait(false)
            : await AccessPolicyHelpers.RequireResourceAccessAsync(
                context, resource!, service, scope, effectiveToken).ConfigureAwait(false);
        if (accessError != null)
        {
            return new MetadataV2ValidationResult(false, publication, resource, service, accessError);
        }

        if (!string.IsNullOrWhiteSpace(requiredProtocol) && service is not null &&
            !MetadataV2ServiceProtocols.IsProtocolEnabled(service, requiredProtocol))
        {
            var msg = $"{requiredProtocol} is not enabled for this service.";
            var error = protocol switch
            {
                ValidationProtocol.OData => CreateODataError(context, msg, StatusCodes.Status404NotFound),
                ValidationProtocol.OgcFeatures => CreateOgcError(context, msg, StatusCodes.Status404NotFound),
                ValidationProtocol.ProblemJson => CreateProblemJsonError(context, msg, StatusCodes.Status404NotFound),
                _ => StandardErrorHelpers.CreateNotFound(context, msg),
            };
            return new MetadataV2ValidationResult(false, publication, resource, service, error);
        }

        ValidatedMetadataSnapshot.Remember(context, resource!, snapshot);
        return new MetadataV2ValidationResult(true, publication, resource, service, null, snapshot);
    }

    // An unauthenticated request for a layer another tenant owns is a challenge rather than 404, so the client can tell that authenticating would help; an authenticated other-tenant principal still gets concealment.
    private static IResult? ChallengeTenantHiddenLayer(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot,
        int layerId,
        string? requiredProtocol,
        ValidationProtocol? protocol)
    {
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            return null;
        }

        // Challenge only what authenticating could open. Resolution falls back to a publication
        // whose service does not serve the requested protocol, and its own tenant would still
        // get 404 there, so that layer stays 404 and its existence is not disclosed.
        var (hidden, _, hiddenService) = ResolveV2TripleForTenant(snapshot, layerId, requiredProtocol, tenantId: null, applyTenantScope: false);
        if (hidden is null
            || (!string.IsNullOrWhiteSpace(requiredProtocol) && hiddenService is not null
                && !MetadataV2ServiceProtocols.IsProtocolEnabled(hiddenService, requiredProtocol)))
        {
            return null;
        }

        if (protocol == ValidationProtocol.ProblemJson)
        {
            AccessPolicyHelpers.AppendAuthenticationChallenge(context);
            return CreateProblemJsonError(context, AccessPolicyHelpers.AuthRequiredMessage, StatusCodes.Status401Unauthorized);
        }

        return AccessPolicyHelpers.CreateAccessDeniedResult(context, AccessDecision.RequiresAuth());
    }

    public static Task<MetadataV2ValidationResult> ValidateLayerWithAccessV2Async(
        HttpContext context,
        int layerId,
        AccessScope scope = AccessScope.Read,
        string? requiredProtocol = null,
        CancellationToken cancellationToken = default)
        => ValidateLayerWithAccessV2Async(context, layerId, AccessPolicyHelpers.DefaultOperationForScope(scope),
            scope, requiredProtocol, cancellationToken);

    public static async Task<MetadataV2ValidationResult> ValidateLayerWithAccessV2Async(
        HttpContext context,
        int layerId,
        AuthorizationOperation operation,
        AccessScope scope = AccessScope.Read,
        string? requiredProtocol = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetV2SnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        var (publication, resource, service) = ResolveV2Triple(context, snapshot, layerId, requiredProtocol);

        if (publication is null || !snapshot.IsRoutable(publication))
        {
            var error = ChallengeTenantHiddenLayer(context, snapshot, layerId, requiredProtocol, protocol: null)
                ?? StandardErrorHelpers.CreateNotFound(context, $"Layer {layerId} not found");
            return new MetadataV2ValidationResult(false, null, null, null, error);
        }

        var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
            context, resource!, operation,
            service, cancellationToken).ConfigureAwait(false);
        if (accessError != null)
        {
            return new MetadataV2ValidationResult(false, publication, resource, service, accessError);
        }

        if (!string.IsNullOrWhiteSpace(requiredProtocol) && service is not null &&
            !MetadataV2ServiceProtocols.IsProtocolEnabled(service, requiredProtocol))
        {
            var error = StandardErrorHelpers.CreateNotFound(
                context,
                $"{requiredProtocol} is not enabled for this service.");
            return new MetadataV2ValidationResult(false, publication, resource, service, error);
        }

        ValidatedMetadataSnapshot.Remember(context, resource!, snapshot);
        return new MetadataV2ValidationResult(true, publication, resource, service, null, snapshot);
    }

    public static async Task<MetadataV2ValidationResult> ValidateStorageLayerWithAccessV2Async(
        HttpContext context,
        int storageLayerId,
        AuthorizationOperation operation)
    {
        var snapshot = await GetV2SnapshotAsync(context, context.RequestAborted).ConfigureAwait(false);
        var resource = snapshot.ResolveStorageResource(storageLayerId,
            publication => ResolveVisibleProtocolService(context, snapshot, publication, null) is not null);
        if (resource is null)
        {
            var error = context.User.Identity?.IsAuthenticated != true && snapshot.PublicationsForStorageLayer(storageLayerId).Any()
                ? AccessPolicyHelpers.CreateAccessDeniedResult(context, AccessDecision.RequiresAuth())
                : StandardErrorHelpers.CreateNotFound(context, $"Storage layer {storageLayerId} not found.");
            return new MetadataV2ValidationResult(false, null, null, null, error);
        }

        var publication = snapshot.PublicationsForStorageLayer(storageLayerId)
            .Where(candidate => candidate.ResourceId == resource.Metadata.Id &&
                ResolveVisibleProtocolService(context, snapshot, candidate, null) is not null)
            .OrderByDescending(static candidate => candidate.IsPrimary)
            .First();
        var service = snapshot.Index.ServicesById[publication.ServiceId];
        var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
            context, resource, operation, service, context.RequestAborted).ConfigureAwait(false);
        if (accessError is null)
        {
            ValidatedMetadataSnapshot.Remember(context, resource, snapshot);
        }

        return new MetadataV2ValidationResult(accessError is null, publication, resource, service, accessError, snapshot);
    }

    public static Task<MetadataV2ValidationResult> ValidateCollectionWithAccessV2Async(
        HttpContext context,
        string collectionId,
        AccessScope scope = AccessScope.Read,
        string? requiredProtocol = MetadataV2ServiceProtocols.OgcFeatures,
        bool preferExactPublicationId = false,
        bool preferAdvertisedCollectionId = false,
        CancellationToken cancellationToken = default)
        => ValidateCollectionWithAccessV2CoreAsync(
            context,
            collectionId,
            false,
            scope,
            requiredProtocol,
            preferExactPublicationId: preferExactPublicationId,
            preferAdvertisedCollectionId: preferAdvertisedCollectionId,
            cancellationToken: cancellationToken);

    public static Task<MetadataV2ValidationResult> ValidateCollectionWithAccessV2Async(
        HttpContext context,
        string collectionId,
        MetadataV2GraphSnapshot snapshot,
        bool preferExactPublicationId = false,
        AccessScope scope = AccessScope.Read,
        string? requiredProtocol = MetadataV2ServiceProtocols.OgcFeatures,
        bool preferAdvertisedCollectionId = false,
        CancellationToken cancellationToken = default)
        => ValidateCollectionWithAccessV2CoreAsync(
            context,
            collectionId,
            false,
            scope,
            requiredProtocol,
            snapshot,
            preferExactPublicationId,
            preferAdvertisedCollectionId,
            cancellationToken);

    public static Task<MetadataV2ValidationResult> ValidateStorageCollectionWithAccessV2Async(
        HttpContext context,
        string collectionId,
        AccessScope scope = AccessScope.Read,
        string? requiredProtocol = null,
        CancellationToken cancellationToken = default)
        => ValidateCollectionWithAccessV2CoreAsync(
            context, collectionId, true, scope, requiredProtocol, cancellationToken: cancellationToken);

    internal static string? AdvertisedCollectionId(MetadataV2Publication publication, MetadataV2Resource? resource)
    {
        var collectionId = publication.ServiceLocalId ?? publication.Path ?? resource?.Metadata.Name;
        return string.IsNullOrWhiteSpace(collectionId) ? resource?.Metadata.Id : collectionId;
    }

    internal static int QueryCacheLayerId(MetadataV2Publication publication, int storageLayerId)
        => publication.LayerIndex ?? storageLayerId;

    internal static MetadataV2Publication? SelectMatchingCollectionPublication(
        IEnumerable<MetadataV2Publication> publications,
        Func<MetadataV2Publication, bool> matches,
        Func<MetadataV2Publication, bool> isTenantVisible,
        Func<MetadataV2Publication, bool> isProtocolEnabled,
        Func<MetadataV2Publication, bool> isPreferredType)
    {
        ArgumentNullException.ThrowIfNull(publications);
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(isTenantVisible);
        ArgumentNullException.ThrowIfNull(isProtocolEnabled);
        ArgumentNullException.ThrowIfNull(isPreferredType);

        MetadataV2Publication? firstVisible = null;
        MetadataV2Publication? bestEnabled = null;
        var bestRank = int.MinValue;
        foreach (var publication in publications)
        {
            if (!matches(publication) || !isTenantVisible(publication))
            {
                continue;
            }

            firstVisible ??= publication;
            if (!isProtocolEnabled(publication))
            {
                continue;
            }

            var rank = (isPreferredType(publication) ? 2 : 0) + (publication.IsPrimary ? 1 : 0);
            if (bestEnabled is null || rank > bestRank)
            {
                bestEnabled = publication;
                bestRank = rank;
            }
        }

        return bestEnabled ?? firstVisible;
    }

    private static async Task<MetadataV2ValidationResult> ValidateCollectionWithAccessV2CoreAsync(
        HttpContext context,
        string collectionId,
        bool resolveByStorageLayerId,
        AccessScope scope = AccessScope.Read,
        string? requiredProtocol = MetadataV2ServiceProtocols.OgcFeatures,
        MetadataV2GraphSnapshot? snapshot = null,
        bool preferExactPublicationId = false,
        bool preferAdvertisedCollectionId = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(collectionId))
        {
            return new MetadataV2ValidationResult(
                false,
                null,
                null,
                null,
                StandardErrorHelpers.CreateBadRequest(context, "Collection id is required."));
        }

        snapshot ??= await GetV2SnapshotAsync(context, cancellationToken).ConfigureAwait(false);

        if (resolveByStorageLayerId &&
            int.TryParse(collectionId, NumberStyles.None, CultureInfo.InvariantCulture, out var storageLayerId))
        {
            var collections = await ResolveStorageCollectionsWithAccessV2Async(
                context, snapshot, requiredProtocol, scope, storageLayerId, cancellationToken).ConfigureAwait(false);
            return collections.TryGetValue(storageLayerId, out var collection)
                ? collection
                : new MetadataV2ValidationResult(false, null, null, null,
                    StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found."));
        }

        bool MatchesCollectionId(MetadataV2Publication p)
        {
            if (string.Equals(p.ServiceLocalId, collectionId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Path, collectionId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Metadata.Name, collectionId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Metadata.Title, collectionId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Metadata.Id, collectionId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var resource = snapshot.ResolveResource(p);
            return resource is not null &&
                (string.Equals(resource.Metadata.Name, collectionId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(resource.Metadata.Title, collectionId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(resource.Metadata.Id, collectionId, StringComparison.OrdinalIgnoreCase));
        }

        MetadataV2Publication? publication = null;

        // When a protocol is specified, prefer the matching publication so a STAC
        // collection request that shares a serviceLocalId with another protocol's
        // publication (e.g. an Esri Feature Service publication with the same local id)
        // doesn't get dispatched to the wrong protocol. Among protocol-enabled candidates,
        // prefer the one whose publication type is the canonical surface for the protocol
        // (e.g. an ogc-collection for OGC API Features); a single layer index is commonly
        // published through several surfaces (feature, map, image, OGC, STAC) and every
        // service enables every protocol, so without the publication-type preference the
        // first publication in document order wins — which routes feature reads to the
        // raster image binding and 500s.
        bool IsTenantVisible(MetadataV2Publication p)
        {
            var matchedResource = snapshot.ResolveResource(p);
            var matchedService = snapshot.Index.ServicesById.TryGetValue(p.ServiceId, out var s) ? s : null;
            return snapshot.IsRoutable(p) &&
                TenantScopeHelpers.IsPublicationVisible(context, p, matchedResource, matchedService);
        }

        // The id written into a Features or Tiles collection document
        // (service-local id, else path, else resource name) must bind that publication
        // even when another publication's title or name reuses it. A visible advertised
        // match does not fall through to an alias. Maps does not set this flag.
        if (preferAdvertisedCollectionId)
        {
            publication = SelectMatchingCollectionPublication(
                snapshot.Graph.Publications,
                publication => string.Equals(
                    AdvertisedCollectionId(publication, snapshot.ResolveResource(publication)),
                    collectionId,
                    StringComparison.OrdinalIgnoreCase),
                IsTenantVisible,
                publication => string.IsNullOrWhiteSpace(requiredProtocol) ||
                    (snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var serviceForProtocol) &&
                     MetadataV2ServiceProtocols.IsProtocolEnabled(serviceForProtocol, requiredProtocol)),
                publication => !string.IsNullOrWhiteSpace(requiredProtocol) &&
                    MetadataV2ServiceProtocols.IsPreferredPublicationType(requiredProtocol, publication.PublicationType));
        }

        // Stable IDs must remain addressable even when another publication's display
        // alias matches the ID. Access and protocol checks below still apply to the
        // exact publication; a denied exact ID must never fall through to an alias.
        if (publication is null && preferExactPublicationId)
        {
            publication = snapshot.Graph.Publications.FirstOrDefault(p =>
                string.Equals(p.Metadata.Id, collectionId, StringComparison.OrdinalIgnoreCase) && IsTenantVisible(p));
        }

        if (publication is null && !string.IsNullOrWhiteSpace(requiredProtocol))
        {
            var protocolMatches = snapshot.Graph.Publications.Where(p =>
                MatchesCollectionId(p) &&
                IsTenantVisible(p) &&
                snapshot.Index.ServicesById.TryGetValue(p.ServiceId, out var s) &&
                MetadataV2ServiceProtocols.IsProtocolEnabled(s, requiredProtocol));
            publication = protocolMatches
                .OrderByDescending(p => MetadataV2ServiceProtocols.IsPreferredPublicationType(requiredProtocol, p.PublicationType))
                .ThenByDescending(p => p.IsPrimary)
                .FirstOrDefault();
        }
        publication ??= snapshot.Graph.Publications.FirstOrDefault(p => MatchesCollectionId(p) && IsTenantVisible(p));

        if (publication is null || !snapshot.IsRoutable(publication))
        {
            return new MetadataV2ValidationResult(
                false,
                null,
                null,
                null,
                StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found."));
        }

        var resource = snapshot.ResolveResource(publication);
        var service = snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var resolvedService)
            ? resolvedService
            : null;

        if (!TenantScopeHelpers.IsPublicationVisible(context, publication, resource, service))
        {
            return new MetadataV2ValidationResult(
                false,
                null,
                null,
                null,
                StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found."));
        }

        if (!snapshot.IsRoutable(publication))
        {
            return new MetadataV2ValidationResult(
                false,
                publication,
                null,
                service,
                StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found."));
        }

        var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
            context, resource!, service, scope, cancellationToken).ConfigureAwait(false);
        if (accessError != null)
        {
            return new MetadataV2ValidationResult(false, publication, resource, service, accessError);
        }

        if (!string.IsNullOrWhiteSpace(requiredProtocol) && service is not null &&
            !MetadataV2ServiceProtocols.IsProtocolEnabled(service, requiredProtocol))
        {
            var error = StandardErrorHelpers.CreateNotFound(
                context,
                $"{requiredProtocol} is not enabled for this service.");
            return new MetadataV2ValidationResult(false, publication, resource, service, error);
        }

        ValidatedMetadataSnapshot.Remember(context, resource!, snapshot);
        return new MetadataV2ValidationResult(true, publication, resource, service, null, snapshot);
    }

    public static async Task<IReadOnlyDictionary<int, MetadataV2ValidationResult>> ResolveStorageCollectionsWithAccessV2Async(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot,
        string? requiredProtocol,
        AccessScope scope = AccessScope.Read,
        int? storageLayerId = null,
        CancellationToken cancellationToken = default)
    {
        var collections = new Dictionary<int, MetadataV2ValidationResult>();
        foreach (var publication in snapshot.Graph.Publications)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = snapshot.ResolveStorageLayerId(publication);
            if (!id.HasValue || (storageLayerId.HasValue && id.Value != storageLayerId.Value))
            {
                continue;
            }

            var service = ResolveVisibleProtocolService(context, snapshot, publication, requiredProtocol);
            if (service is null)
            {
                continue;
            }

            var resource = snapshot.ResolveResource(publication)!;
            var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
                context, resource, service, scope, cancellationToken).ConfigureAwait(false);
            var candidate = new MetadataV2ValidationResult(
                accessError is null, publication, resource, service, accessError, snapshot);
            if (!collections.TryGetValue(id.Value, out var existing) ||
                (candidate.IsValid && !existing.IsValid) ||
                (candidate.IsValid == existing.IsValid && publication.IsPrimary && !existing.Publication!.IsPrimary))
            {
                collections[id.Value] = candidate;
            }
        }

        return collections;
    }

    internal readonly record struct LayerReadAccessResult(
        bool NotFound,
        AccessDecision Decision,
        MetadataV2Resource? Resource,
        MetadataV2Service? Service);

    public static async Task<LayerReadAccessResult> EvaluateLayerReadAccessV2Async(
        HttpContext context,
        int layerId,
        string? requiredProtocol = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetV2SnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        var (publication, resource, service) = ResolveV2Triple(context, snapshot, layerId, requiredProtocol);

        if (publication is null || !snapshot.IsRoutable(publication))
        {
            return new LayerReadAccessResult(NotFound: true, default, null, null);
        }

        var decision = await AccessPolicyHelpers.EvaluateResourceAccessAsync(
            context,
            resource!,
            service,
            AccessPolicyHelpers.DefaultOperationForScope(AccessScope.Read),
            cancellationToken).ConfigureAwait(false);

        return new LayerReadAccessResult(NotFound: false, decision, resource, service);
    }

    public static IReadOnlyDictionary<int, MetadataV2Service> BuildPrimaryServiceMapV2(
        MetadataV2GraphSnapshot snapshot,
        string? requiredProtocol = null,
        IEnumerable<MetadataV2Publication>? publications = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Track the chosen publication alongside the service so subsequent tie-break
        // decisions can consult publication.IsPrimary.
        var byLayer = new Dictionary<int, (MetadataV2Publication Publication, MetadataV2Service Service)>();
        foreach (var pub in publications ?? snapshot.Graph.Publications)
        {
            if (!pub.LayerIndex.HasValue || !snapshot.IsRoutable(pub))
            {
                continue;
            }
            if (!snapshot.Index.ServicesById.TryGetValue(pub.ServiceId, out var service))
            {
                continue;
            }
            if (!byLayer.TryGetValue(pub.LayerIndex.Value, out var existing))
            {
                byLayer[pub.LayerIndex.Value] = (pub, service);
                continue;
            }

            // Tie-break:
            //   1. requiredProtocol match
            //   2. publication.IsPrimary
            //   3. lexicographically earliest service name
            var hasProtocolGate = !string.IsNullOrWhiteSpace(requiredProtocol);
            var existingMatches = hasProtocolGate && MetadataV2ServiceProtocols.IsProtocolEnabled(existing.Service, requiredProtocol!);
            var candidateMatches = hasProtocolGate && MetadataV2ServiceProtocols.IsProtocolEnabled(service, requiredProtocol!);
            if (candidateMatches && !existingMatches)
            {
                byLayer[pub.LayerIndex.Value] = (pub, service);
                continue;
            }
            if (existingMatches != candidateMatches)
            {
                continue;
            }

            if (pub.IsPrimary && !existing.Publication.IsPrimary)
            {
                byLayer[pub.LayerIndex.Value] = (pub, service);
                continue;
            }
            if (existing.Publication.IsPrimary != pub.IsPrimary)
            {
                continue;
            }

            if (string.Compare(service.Metadata.Name, existing.Service.Metadata.Name, StringComparison.OrdinalIgnoreCase) < 0)
            {
                byLayer[pub.LayerIndex.Value] = (pub, service);
            }
        }
        return byLayer.ToDictionary(kv => kv.Key, kv => kv.Value.Service);
    }

    public static async Task<MetadataV2Service?> ResolvePrimaryServiceV2Async(
        HttpContext context,
        int layerId,
        string? requiredProtocol = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetV2SnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        var (_, _, service) = ResolveV2Triple(context, snapshot, layerId, requiredProtocol);
        return service;
    }

    public static async Task<string?> ResolvePrimaryServiceNameV2Async(
        HttpContext context,
        int layerId,
        string? requiredProtocol = null,
        CancellationToken cancellationToken = default)
    {
        var service = await ResolvePrimaryServiceV2Async(context, layerId, requiredProtocol, cancellationToken)
            .ConfigureAwait(false);
        return service?.Metadata.Name;
    }

    public static async Task<string?> ResolvePrimaryServiceNameByProtocolV2Async(
        HttpContext context,
        int layerId,
        string? requiredProtocol,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetV2SnapshotAsync(context, cancellationToken).ConfigureAwait(false);

        // Prefer the publication whose type is the canonical surface for the protocol
        // before falling back to the lexicographically earliest service name. LINQ ordering
        // is stable, preserving the first graph entry when names compare equal.
        return snapshot.Graph.Publications
            .Where(publication => publication.LayerIndex == layerId && snapshot.IsRoutable(publication))
            .Select(publication => (
                Publication: publication,
                Service: ResolveVisibleProtocolService(
                    context,
                    snapshot,
                    publication,
                    requiredProtocol)))
            .Where(candidate => candidate.Service is not null)
            .OrderByDescending(candidate =>
                MetadataV2ServiceProtocols.IsPreferredPublicationType(
                    requiredProtocol,
                    candidate.Publication.PublicationType))
            .ThenBy(
                candidate => candidate.Service!.Metadata.Name,
                StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Service!.Metadata.Name)
            .FirstOrDefault();
    }

    private static MetadataV2Service? ResolveVisibleProtocolService(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Publication publication,
        string? requiredProtocol)
    {
        if (!snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service))
        {
            return null;
        }

        var resource = snapshot.ResolveResource(publication);
        if (!snapshot.IsRoutable(publication) ||
            !TenantScopeHelpers.IsPublicationVisible(context, publication, resource, service))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(requiredProtocol) ||
               MetadataV2ServiceProtocols.IsProtocolEnabled(service, requiredProtocol)
            ? service
            : null;
    }

    // A retired publication is missing, so a disabled layer answers 404 instead of being served.
    internal static bool IsRetired(MetadataV2Resource resource)
        => resource.Status.Lifecycle == MetadataV2LifecycleStatus.Retired;

    private static async Task<MetadataV2GraphSnapshot> GetV2SnapshotAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var provider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        return await provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
    }

    private static (MetadataV2Publication? Publication, MetadataV2Resource? Resource, MetadataV2Service? Service)
        ResolveV2Triple(
            HttpContext context,
            MetadataV2GraphSnapshot snapshot,
            int layerId,
            string? requiredProtocol)
        => ResolveV2TripleForTenant(
            snapshot,
            layerId,
            requiredProtocol,
            TenantScopeHelpers.ResolveRequestTenantId(context),
            applyTenantScope: true);

    internal static (MetadataV2Publication? Publication, MetadataV2Resource? Resource, MetadataV2Service? Service)
        ResolveV2TripleForTenant(
            MetadataV2GraphSnapshot snapshot,
            int layerId,
            string? requiredProtocol,
            string? tenantId,
            bool applyTenantScope)
    {
        // Resolution order (now deterministic):
        //   1. requiredProtocol matches AND publication.IsPrimary
        //   2. requiredProtocol matches (any)
        //   3. publication.IsPrimary
        //   4. lexicographically earliest by service name
        var candidatePublications = snapshot.Graph.Publications
            .Where(p => p.LayerIndex == layerId && snapshot.IsRoutable(p))
            .Where(p =>
            {
                if (!applyTenantScope)
                {
                    return true;
                }

                var resource = snapshot.ResolveResource(p);
                var service = snapshot.Index.ServicesById.TryGetValue(p.ServiceId, out var resolvedService)
                    ? resolvedService
                    : null;
                return MetadataV2TenantVisibility.IsVisibleToTenant(p, resource, service, tenantId);
            })
            .ToList();

        if (!string.IsNullOrWhiteSpace(requiredProtocol))
        {
            var preferred = candidatePublications
                .Where(p =>
                    snapshot.Index.ServicesById.TryGetValue(p.ServiceId, out var s) &&
                    MetadataV2ServiceProtocols.IsProtocolEnabled(s, requiredProtocol))
                .OrderByDescending(p => MetadataV2ServiceProtocols.IsPreferredPublicationType(requiredProtocol, p.PublicationType))
                .ThenByDescending(p => p.IsPrimary)
                .FirstOrDefault();
            if (preferred is not null)
            {
                var preferredResource = snapshot.ResolveResource(preferred);
                var preferredService = snapshot.Index.ServicesById.TryGetValue(preferred.ServiceId, out var resolvedPreferredService)
                    ? resolvedPreferredService
                    : null;
                return (preferred, preferredResource, preferredService);
            }
        }

        var publication = candidatePublications
            .OrderByDescending(p => p.IsPrimary)
            .ThenBy(p =>
                snapshot.Index.ServicesById.TryGetValue(p.ServiceId, out var s)
                    ? s.Metadata.Name
                    : p.ServiceId,
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (publication is null)
        {
            return (null, null, null);
        }

        var resource = snapshot.ResolveResource(publication);
        var service = snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var resolvedService)
            ? resolvedService
            : null;
        return (publication, resource, service);
    }

    // A grant for one edit operation does not authorize another; a null operation keeps the coarse any-write check for a mixed batch.
    public static async Task<MetadataV2ValidationResult> ValidateCollectionWriteAccessV2Async(
        HttpContext context,
        string collectionId,
        string? requiredProtocol = MetadataV2ServiceProtocols.OgcFeatures,
        AuthorizationOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        var validation = await ValidateCollectionWithAccessV2Async(
            context,
            collectionId,
            scope: AccessScope.Write,
            requiredProtocol: requiredProtocol,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return validation;
        }

        var rbacError = operation is { } op
            ? await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
                context,
                validation.Resource!,
                validation.Service,
                op,
                cancellationToken).ConfigureAwait(false)
            : await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
                context,
                validation.Resource!,
                validation.Service,
                cancellationToken).ConfigureAwait(false);
        if (rbacError != null)
        {
            return new MetadataV2ValidationResult(
                false,
                validation.Publication,
                validation.Resource,
                validation.Service,
                rbacError);
        }

        // Canonical-service boundary: a resource can be published through multiple services
        // but writes belong to the canonical (IsPrimary) publication. If the validated
        // publication is the secondary one, require the caller ALSO be authorized as
        // data-editor on the canonical service.
        var canonicalError = await EnforceCanonicalServiceWriteAccessAsync(
            context,
            validation,
            operation,
            cancellationToken).ConfigureAwait(false);
        if (canonicalError != null)
        {
            return new MetadataV2ValidationResult(
                false,
                validation.Publication,
                validation.Resource,
                validation.Service,
                canonicalError);
        }

        // Declared capabilities are an editing contract, not advisory text. The Esri
        // GeoServices edits handler already refuses an edit kind the publication never
        // declared (#4073); this surface did not, so a layer published with the default
        // ["Query","Extract"] capabilities accepted OGC API Features writes it never
        // advertised (#4707). Both surfaces now resolve capabilities the same way.
        var capabilityError = ValidateDeclaredEditCapability(context, validation, operation);
        if (capabilityError != null)
        {
            return new MetadataV2ValidationResult(
                false,
                validation.Publication,
                validation.Resource,
                validation.Service,
                capabilityError);
        }

        // Storage-routing contract: refuse a mutation the managed feature writer cannot
        // land where the serving protocols read, instead of acknowledging a write that is
        // never readable back (#4707).
        var routingError = await ValidateWriteRoutingAsync(
            context, validation, cancellationToken).ConfigureAwait(false);
        if (routingError != null)
        {
            return new MetadataV2ValidationResult(
                false,
                validation.Publication,
                validation.Resource,
                validation.Service,
                routingError);
        }

        return validation;
    }

    private static IResult? ValidateDeclaredEditCapability(
        HttpContext context,
        MetadataV2ValidationResult validation,
        AuthorizationOperation? operation)
    {
        if (operation is not { } requested)
        {
            return null;
        }

        var capability = FindUndeclaredEditCapability(validation.Service, validation.Publication, requested);
        return capability is null
            ? null
            : ValidateDeclaredEditCapability(context, validation.Service!, validation.Publication, capability);
    }

    // A publication that declares no capabilities keeps the legacy allow; a declared set that omits this operation rejects the mutation.
    internal static string? FindUndeclaredEditCapability(
        MetadataV2Service? service,
        MetadataV2Publication? publication,
        AuthorizationOperation operation)
    {
        if (service is null || MapToEditCapability(operation) is not { } capability)
        {
            return null;
        }

        return MetadataV2EditCapabilities.SupportsDeclared(service, publication, capability) == false
            ? capability
            : null;
    }

    internal static string DescribeUndeclaredEditCapability(string capability)
        => $"{capability} edits are not enabled for this collection.";

    internal static IResult? ValidateDeclaredEditCapability(
        HttpContext context,
        MetadataV2Service service,
        MetadataV2Publication? publication,
        string capability)
    {
        // Only a declared capability set that omits the operation is a denial. Metadata that
        // declares nothing at all (the v1-compatibility snapshot projects no capabilities onto
        // OGC API Features publications) makes no statement, and treating that silence as a
        // denial would take editing away from deployments that never opted out of it.
        if (MetadataV2EditCapabilities.SupportsDeclared(service, publication, capability) != false)
        {
            return null;
        }

        // 405 rather than 403: the collection is not writable in this way for anybody, so
        // this is a property of the resource and not of the caller's credentials. The Allow
        // header advertises the methods that remain available.
        context.Response.Headers.Allow = "GET, HEAD, OPTIONS";
        return StandardErrorHelpers.CreateMethodNotAllowed(
            context,
            DescribeUndeclaredEditCapability(capability),
            [$"Declare the {capability} capability on the publication before submitting {capability.ToLowerInvariant()} edits."]);
    }

    private static string? MapToEditCapability(AuthorizationOperation operation)
        => operation switch
        {
            AuthorizationOperation.Insert => MetadataV2EditCapabilities.Create,
            AuthorizationOperation.Update => MetadataV2EditCapabilities.Update,
            AuthorizationOperation.Delete => MetadataV2EditCapabilities.Delete,
            _ => null
        };

    // A write whose storage is not the shared features table returns 501, because the write would land where it cannot be read back.
    private static async Task<IResult?> ValidateWriteRoutingAsync(
        HttpContext context,
        MetadataV2ValidationResult validation,
        CancellationToken cancellationToken)
    {
        if (validation.Resource is null || validation.Publication is null)
        {
            return null;
        }

        var snapshot = validation.Snapshot
            ?? await GetV2SnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        var storageBinding = snapshot.ResolveStorageBinding(validation.Publication);
        if (storageBinding is null || storageBinding.StorageType != MetadataV2StorageType.RelationalTable)
        {
            // Non-relational (or unresolvable) storage is out of this guard's scope: the
            // read paths raise their own diagnostics for a binding they cannot map.
            return null;
        }

        FeatureStorageMapping mapping;
        try
        {
            mapping = FeatureStorageMapping.FromMetadata(validation.Resource, storageBinding);
        }
        catch (InvalidOperationException)
        {
            // An unmappable binding is a metadata defect the read paths already report;
            // do not convert it into a write-specific error here.
            return null;
        }

        if (mapping.SupportsManagedWrites)
        {
            return null;
        }

        return StandardErrorHelpers.CreateNotImplemented(
            context,
            "This collection is published over an external source table, which this server cannot write through.",
            [
                $"Features are served from '{mapping.QualifiedName}'; the managed feature writer cannot modify it.",
                "Edit the source table directly and refresh the published layer, or republish the data into the managed feature store."
            ]);
    }

    // A write through a non-primary publication also requires data-editor authorization on the canonical service.
    private static async Task<IResult?> EnforceCanonicalServiceWriteAccessAsync(
        HttpContext context,
        MetadataV2ValidationResult validation,
        AuthorizationOperation? operation,
        CancellationToken cancellationToken)
    {
        if (!validation.IsValid || validation.Resource is null || validation.Publication is null)
        {
            return null;
        }

        // If the matched publication IS the canonical primary, no extra check needed.
        if (validation.Publication.IsPrimary)
        {
            return null;
        }

        var snapshot = await GetV2SnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        MetadataV2Publication? canonical = null;
        // Not converted to `.FirstOrDefault(...)`: the predicate needs two intermediate
        // values computed per candidate (the resolved resource and the resolved service),
        // reused across the equality and visibility checks — inlining them into a single
        // LINQ predicate would duplicate the lookups or require a less readable local
        // function.
        foreach (var pub in snapshot.Graph.Publications.Where(pub => pub.IsPrimary && snapshot.IsRoutable(pub)))
        {
            var pubResource = snapshot.ResolveResource(pub);
            if (pubResource is null) continue;
            if (string.Equals(pubResource.Metadata.Id, validation.Resource.Metadata.Id, StringComparison.OrdinalIgnoreCase))
            {
                var pubService = snapshot.Index.ServicesById.TryGetValue(pub.ServiceId, out var resolvedPubService)
                    ? resolvedPubService
                    : null;
                if (!TenantScopeHelpers.IsPublicationVisible(context, pub, pubResource, pubService))
                {
                    continue;
                }
                canonical = pub;
                break;
            }
        }

        if (canonical is null)
        {
            // No primary publication declared — nothing to enforce.
            return null;
        }

        MetadataV2Service? canonicalService = snapshot.Index.ServicesById.TryGetValue(canonical.ServiceId, out var cs)
            ? cs
            : null;

        // Re-evaluate data-editor authorization against the canonical service, preserving
        // the per-operation narrowing (BH3-001/BH3-014) when the caller supplied an operation.
        return operation is { } op
            ? await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
                context,
                validation.Resource,
                canonicalService,
                op,
                cancellationToken).ConfigureAwait(false)
            : await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
                context,
                validation.Resource,
                canonicalService,
                cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MetadataV2ValidationResult> ValidateLayerWriteAccessV2Async(
        HttpContext context,
        int layerId,
        ValidationProtocol protocol,
        string? requiredProtocol = null,
        AuthorizationOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        var validation = await ValidateLayerWithAccessV2Async(
            context,
            layerId,
            protocol,
            AccessScope.Write,
            requiredProtocol,
            cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return validation;
        }

        // Per-operation authorization (BH3-001/BH3-014): when the caller supplies the specific
        // CRUD operation (OData POST=Insert / PATCH/PUT=Update / DELETE=Delete), the RBAC
        // data-editor gate bypass is narrowed to that operation so an insert-only grantee cannot
        // Update or Delete and vice-versa. Absent an operation, any write grant satisfies the gate.
        var rbacError = operation is { } op
            ? await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
                context,
                validation.Resource!,
                validation.Service,
                op,
                cancellationToken).ConfigureAwait(false)
            : await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
                context,
                validation.Resource!,
                validation.Service,
                cancellationToken).ConfigureAwait(false);
        if (rbacError != null)
        {
            return new MetadataV2ValidationResult(
                false,
                validation.Publication,
                validation.Resource,
                validation.Service,
                rbacError);
        }

        var canonicalError = await EnforceCanonicalServiceWriteAccessAsync(
            context,
            validation,
            operation,
            cancellationToken).ConfigureAwait(false);
        if (canonicalError != null)
        {
            return new MetadataV2ValidationResult(
                false,
                validation.Publication,
                validation.Resource,
                validation.Service,
                canonicalError);
        }

        // Declared capabilities bound single-entity writes here exactly as they bound the
        // OGC API Features surface above (SEC-5). Callers that pass no operation (for example
        // the geoprocessing sink) keep the coarse any-write behaviour.
        var capabilityError = ValidateDeclaredEditCapability(context, validation, operation);
        if (capabilityError != null)
        {
            return new MetadataV2ValidationResult(
                false,
                validation.Publication,
                validation.Resource,
                validation.Service,
                capabilityError);
        }

        return validation;
    }

    public static async Task<MetadataV2ServiceValidationResult> ValidateServiceWithAccessV2Async(
        HttpContext context,
        string serviceName,
        string? requiredProtocol = null,
        AccessScope scope = AccessScope.Read,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return new MetadataV2ServiceValidationResult(
                false, null, [], [],
                StandardErrorHelpers.CreateBadRequest(context, "Service name is required."));
        }

        var snapshot = await GetV2SnapshotAsync(context, cancellationToken).ConfigureAwait(false);

        // Prefer a protocol match; otherwise fall back to name match.
        MetadataV2Service? service = null;
        if (!string.IsNullOrWhiteSpace(requiredProtocol))
        {
            service = snapshot.Graph.Services.FirstOrDefault(s =>
                MetadataV2ServiceProtocols.IsProtocolEnabled(s, requiredProtocol) &&
                string.Equals(s.Metadata.Name, serviceName, StringComparison.OrdinalIgnoreCase));
        }
        service ??= snapshot.Index.ServicesByName.TryGetValue(serviceName, out var s) ? s : null;

        if (service is null || !TenantScopeHelpers.IsTenantVisible(context, resource: null, service))
        {
            return new MetadataV2ServiceValidationResult(
                false, null, [], [],
                StandardErrorHelpers.CreateNotFound(context, $"Service '{serviceName}' not found."));
        }

        if (!string.IsNullOrWhiteSpace(requiredProtocol) &&
            !MetadataV2ServiceProtocols.IsProtocolEnabled(service, requiredProtocol))
        {
            return new MetadataV2ServiceValidationResult(
                false, service, [], [],
                StandardErrorHelpers.CreateNotFound(
                    context, $"{requiredProtocol} is not enabled for service '{serviceName}'."));
        }

        var serviceAccessError = await AccessPolicyHelpers.RequireServiceAccessAsync(
            context, service, AccessPolicyHelpers.DefaultOperationForScope(scope), cancellationToken).ConfigureAwait(false);
        if (serviceAccessError != null)
        {
            return new MetadataV2ServiceValidationResult(false, service, [], [], serviceAccessError);
        }

        var publications = snapshot.PublicationsForService(service.Metadata.Id);
        var visiblePublications = new List<MetadataV2Publication>(publications.Count);
        var resources = new List<MetadataV2Resource>(publications.Count);
        foreach (var pub in publications)
        {
            var resource = snapshot.ResolveResource(pub);
            if (!snapshot.IsRoutable(pub))
            {
                continue;
            }
            if (!TenantScopeHelpers.IsPublicationVisible(context, pub, resource!, service))
            {
                continue;
            }
            // Drop resources the caller can't read; capability documents normally hide them.
            if (!AccessPolicyHelpers.IsResourceAccessible(context, resource!, service, scope))
            {
                continue;
            }
            visiblePublications.Add(pub);
            resources.Add(resource!);
        }

        return new MetadataV2ServiceValidationResult(true, service, visiblePublications, resources, null);
    }
}

internal readonly record struct MetadataV2ServiceValidationResult(
    bool IsValid,
    MetadataV2Service? Service,
    IReadOnlyList<MetadataV2Publication> Publications,
    IReadOnlyList<MetadataV2Resource> AccessibleResources,
    IResult? ErrorResult);
