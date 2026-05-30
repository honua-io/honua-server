// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.Extensions.DependencyInjection;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Infrastructure.Validation;

/// <summary>
/// Shared helper methods for layer validation and access checking across CRUD handlers.
/// Eliminates DRY violations by providing a unified validation pattern for all protocols.
/// </summary>
/// <remarks>
/// Audit-A1: protocol-specific error formatting and cancellation-token resolution
/// plug in via the static <see cref="ODataErrorFactoryOverride"/> and
/// <see cref="ODataCancellationTokenResolverOverride"/> delegates so this
/// Honua.Infrastructure.Validation file does not need a using-clause dependency on
/// any protocol assembly. <c>ODataServiceCollectionExtensions.AddOData</c>
/// installs both delegates at startup.
/// </remarks>
internal static class LayerValidationHelpers
{
    /// <summary>
    /// Builds an OData-formatted error result. Set by
    /// <c>ODataServiceCollectionExtensions.AddOData</c>; when null, OData
    /// validation errors fall through to the generic StandardErrorHelpers
    /// path. Signature: (context, errorCode, message, statusCode) → IResult.
    /// </summary>
    internal static Func<HttpContext, string, string, int, IResult>? ODataErrorFactoryOverride { get; set; }

    /// <summary>
    /// Resolves the OData timeout-aware cancellation token from the request
    /// pipeline. Set by <c>ODataServiceCollectionExtensions.AddOData</c>;
    /// when null, the caller's cancellation token is used unchanged.
    /// </summary>
    internal static Func<HttpContext, CancellationToken>? ODataCancellationTokenResolverOverride { get; set; }
    /// <summary>
    /// Protocol-specific error format options for layer validation.
    /// </summary>
    internal enum ValidationProtocol
    {
        OData,
        OgcFeatures
    }

    /// <summary>
    /// Result of V2 publication validation + access checking. Carries the publication,
    /// the canonical resource, and the resolved service so callers have everything
    /// needed for downstream operations.
    /// </summary>
    /// <param name="IsValid">Whether validation succeeded.</param>
    /// <param name="Publication">The matched V2 publication, if any.</param>
    /// <param name="Resource">The canonical resource backing the publication.</param>
    /// <param name="Service">The service through which the resource is published.</param>
    /// <param name="ErrorResult">IResult to return when validation failed.</param>
    internal readonly record struct MetadataV2ValidationResult(
        bool IsValid,
        MetadataV2Publication? Publication,
        MetadataV2Resource? Resource,
        MetadataV2Service? Service,
        IResult? ErrorResult);

    /// <summary>
    /// Creates OData-formatted error response with correct status code mapping.
    /// Preserves existing OData error message formats and status code logic.
    /// </summary>
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

    /// <summary>
    /// Creates OGC-formatted error response with appropriate status code.
    /// Preserves existing OGC error message formats.
    /// </summary>
    private static IResult CreateOgcError(HttpContext context, string message, int statusCode)
    {
        return statusCode switch
        {
            400 => StandardErrorHelpers.CreateBadRequest(context, message),
            404 => StandardErrorHelpers.CreateNotFound(context, message),
            _ => StandardErrorHelpers.CreateInternalServerError(context, message)
        };
    }

    // ---- Metadata v2 validation. Resolves a (publication, resource, service) triple
    // from the V2 graph snapshot for the given layer id (or collection id) and applies
    // the same access-policy + protocol-enablement checks as the v1 methods above.

    /// <summary>
    /// Validates a layer index against the V2 graph snapshot and returns the matched
    /// publication + resource + service, gated on access policy.
    /// </summary>
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
        var (publication, resource, service) = ResolveV2Triple(snapshot, layerId, requiredProtocol);

        if (publication is null || resource is null || IsRetired(publication) || IsRetired(resource))
        {
            var msg = $"Layer {layerId} not found";
            var error = protocol switch
            {
                ValidationProtocol.OData => CreateODataError(context, msg, StatusCodes.Status404NotFound),
                ValidationProtocol.OgcFeatures => CreateOgcError(context, msg, StatusCodes.Status404NotFound),
                _ => StandardErrorHelpers.CreateNotFound(context, msg),
            };
            return new MetadataV2ValidationResult(false, null, null, null, error);
        }

        var accessError = AccessPolicyHelpers.RequireResourceAccess(context, resource, service, scope);
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
                _ => StandardErrorHelpers.CreateNotFound(context, msg),
            };
            return new MetadataV2ValidationResult(false, publication, resource, service, error);
        }

        return new MetadataV2ValidationResult(true, publication, resource, service, null);
    }

    /// <summary>
    /// Validates a layer index against the V2 graph snapshot using standard error
    /// responses (no protocol-specific formatting).
    /// </summary>
    public static async Task<MetadataV2ValidationResult> ValidateLayerWithAccessV2Async(
        HttpContext context,
        int layerId,
        AccessScope scope = AccessScope.Read,
        string? requiredProtocol = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetV2SnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        var (publication, resource, service) = ResolveV2Triple(snapshot, layerId, requiredProtocol);

        if (publication is null || resource is null || IsRetired(publication) || IsRetired(resource))
        {
            var error = StandardErrorHelpers.CreateNotFound(context, $"Layer {layerId} not found");
            return new MetadataV2ValidationResult(false, null, null, null, error);
        }

        var accessError = AccessPolicyHelpers.RequireResourceAccess(context, resource, service, scope);
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

        return new MetadataV2ValidationResult(true, publication, resource, service, null);
    }

    /// <summary>
    /// Validates a collection identifier against the V2 graph snapshot. The collection id
    /// is matched against publication serviceLocalId, path, name, or id (case-insensitive),
    /// in that order. For unambiguous routing, callers should ensure publications carry a
    /// distinct <c>ServiceLocalId</c>.
    /// </summary>
    public static async Task<MetadataV2ValidationResult> ValidateCollectionWithAccessV2Async(
        HttpContext context,
        string collectionId,
        AccessScope scope = AccessScope.Read,
        string? requiredProtocol = MetadataV2ServiceProtocols.OgcFeatures,
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

        var snapshot = await GetV2SnapshotAsync(context, cancellationToken).ConfigureAwait(false);

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
        // doesn't get dispatched to the wrong protocol.
        if (!string.IsNullOrWhiteSpace(requiredProtocol))
        {
            publication = snapshot.Graph.Publications.FirstOrDefault(p =>
                MatchesCollectionId(p) &&
                snapshot.Index.ServicesById.TryGetValue(p.ServiceId, out var s) &&
                MetadataV2ServiceProtocols.IsProtocolEnabled(s, requiredProtocol));
        }
        publication ??= snapshot.Graph.Publications.FirstOrDefault(MatchesCollectionId);

        if (publication is null || IsRetired(publication))
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

        if (resource is null || IsRetired(resource))
        {
            return new MetadataV2ValidationResult(
                false,
                publication,
                null,
                service,
                StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found."));
        }

        var accessError = AccessPolicyHelpers.RequireResourceAccess(context, resource, service, scope);
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

        return new MetadataV2ValidationResult(true, publication, resource, service, null);
    }

    /// <summary>
    /// Builds a deterministic layerIndex → <see cref="MetadataV2Service"/> map for a protocol-
    /// specific route surface. When a publication's layer index could belong to several
    /// services, the one whose <see cref="MetadataV2Service.Protocols"/> contains
    /// <paramref name="requiredProtocol"/> wins; otherwise the service with the
    /// lexicographically earliest name keeps routing deterministic.
    /// </summary>
    public static IReadOnlyDictionary<int, MetadataV2Service> BuildPrimaryServiceMapV2(
        MetadataV2GraphSnapshot snapshot,
        string? requiredProtocol = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Track the chosen publication alongside the service so subsequent tie-break
        // decisions can consult publication.IsPrimary.
        var byLayer = new Dictionary<int, (MetadataV2Publication Publication, MetadataV2Service Service)>();
        foreach (var pub in snapshot.Graph.Publications)
        {
            if (!pub.LayerIndex.HasValue)
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

    /// <summary>
    /// Resolves the primary V2 service for a layer index. Preferred match is on
    /// <paramref name="requiredProtocol"/>; falls back to the lexicographically earliest
    /// service name. Returns null when no publication matches.
    /// </summary>
    public static async Task<MetadataV2Service?> ResolvePrimaryServiceV2Async(
        HttpContext context,
        int layerId,
        string? requiredProtocol = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetV2SnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        var (_, _, service) = ResolveV2Triple(snapshot, layerId, requiredProtocol);
        return service;
    }

    /// <summary>
    /// Resolves the primary V2 service name for a layer index.
    /// </summary>
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

    /// <summary>
    /// Resolves the primary V2 service name for a layer index, preferring services
    /// whose <see cref="MetadataV2Service.Protocols"/> list contains
    /// <paramref name="requiredProtocol"/>. Kept as a distinct entry point so call
    /// sites self-document the protocol-string gating contract.
    /// </summary>
    public static async Task<string?> ResolvePrimaryServiceNameByProtocolV2Async(
        HttpContext context,
        int layerId,
        string? requiredProtocol,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetV2SnapshotAsync(context, cancellationToken).ConfigureAwait(false);

        MetadataV2Service? chosen = null;
        foreach (var pub in snapshot.Graph.Publications)
        {
            if (pub.LayerIndex != layerId) continue;
            if (!snapshot.Index.ServicesById.TryGetValue(pub.ServiceId, out var service)) continue;

            if (!string.IsNullOrWhiteSpace(requiredProtocol) &&
                !MetadataV2ServiceProtocols.IsProtocolEnabled(service, requiredProtocol))
            {
                continue;
            }

            if (chosen is null ||
                string.Compare(service.Metadata.Name, chosen.Metadata.Name, StringComparison.OrdinalIgnoreCase) < 0)
            {
                chosen = service;
            }
        }
        return chosen?.Metadata.Name;
    }

    /// <summary>
    /// Treats a publication or resource whose <see cref="MetadataV2Status.Lifecycle"/> is
    /// <see cref="MetadataV2LifecycleStatus.Retired"/> as missing. Admin-driven enable/disable
    /// (and the WebAppFixture <c>SetV2LayerEnabled</c> helper that mirrors it in tests) flip
    /// the lifecycle to Retired, so route resolution must 404 rather than serving the
    /// disabled layer.
    /// </summary>
    private static bool IsRetired(MetadataV2Resource resource)
        => resource.Status.Lifecycle == MetadataV2LifecycleStatus.Retired;

    private static bool IsRetired(MetadataV2Publication publication)
        => publication.Status.Lifecycle == MetadataV2LifecycleStatus.Retired;

    private static async Task<MetadataV2GraphSnapshot> GetV2SnapshotAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var provider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        return await provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
    }

    private static (MetadataV2Publication? Publication, MetadataV2Resource? Resource, MetadataV2Service? Service)
        ResolveV2Triple(
            MetadataV2GraphSnapshot snapshot,
            int layerId,
            string? requiredProtocol)
    {
        // Resolution order (now deterministic):
        //   1. requiredProtocol matches AND publication.IsPrimary
        //   2. requiredProtocol matches (any)
        //   3. publication.IsPrimary
        //   4. lexicographically earliest by service name
        var candidatePublications = snapshot.Graph.Publications
            .Where(p => p.LayerIndex == layerId)
            .ToList();

        if (!string.IsNullOrWhiteSpace(requiredProtocol))
        {
            var preferred = candidatePublications
                .Where(p =>
                    snapshot.Index.ServicesById.TryGetValue(p.ServiceId, out var s) &&
                    MetadataV2ServiceProtocols.IsProtocolEnabled(s, requiredProtocol))
                .OrderByDescending(p => p.IsPrimary)
                .FirstOrDefault();
            if (preferred is not null)
            {
                var preferredResource = snapshot.ResolveResource(preferred);
                var preferredService = snapshot.Index.ServicesById[preferred.ServiceId];
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

    /// <summary>
    /// Combines <see cref="ValidateCollectionWithAccessV2Async"/> with the V2 RBAC data-editor
    /// helper so OGC API Features CRUD/Transaction handlers can run a single check.
    /// Returns the matched publication + resource + service plus an
    /// <see cref="IResult"/> error when validation or authorization fails.
    /// </summary>
    /// <param name="context">HTTP context carrying request services and principal.</param>
    /// <param name="collectionId">Collection identifier from the route.</param>
    /// <param name="requiredProtocol">Optional protocol gate. Defaults to
    /// <c>OgcFeatures</c> protocol constant to mirror the v1 method.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<MetadataV2ValidationResult> ValidateCollectionWriteAccessV2Async(
        HttpContext context,
        string collectionId,
        string? requiredProtocol = MetadataV2ServiceProtocols.OgcFeatures,
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

        var rbacError = await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
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

        return validation;
    }

    /// <summary>
    /// Enforces the canonical-service boundary for writes: when the validated publication
    /// is not the resource's IsPrimary publication, the caller must also satisfy data-editor
    /// authorization on the canonical service. Returns a 403 IResult when the caller can
    /// write through the secondary publication but not the canonical one, otherwise null.
    /// </summary>
    private static async Task<IResult?> EnforceCanonicalServiceWriteAccessAsync(
        HttpContext context,
        MetadataV2ValidationResult validation,
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
        foreach (var pub in snapshot.Graph.Publications)
        {
            if (!pub.IsPrimary) continue;
            var pubResource = snapshot.ResolveResource(pub);
            if (pubResource is null) continue;
            if (string.Equals(pubResource.Metadata.Id, validation.Resource.Metadata.Id, StringComparison.OrdinalIgnoreCase))
            {
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

        // Re-evaluate data-editor authorization against the canonical service.
        return await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
            context,
            validation.Resource,
            canonicalService,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// V2 sibling of write-access validation. Combines V2 layer access validation
    /// with the V2 RBAC data-editor helper and preserves protocol-specific error formatting.
    /// </summary>
    public static async Task<MetadataV2ValidationResult> ValidateLayerWriteAccessV2Async(
        HttpContext context,
        int layerId,
        ValidationProtocol protocol,
        string? requiredProtocol = null,
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

        var rbacError = await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
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

        return validation;
    }

    /// <summary>
    /// V2 service-level validation. Looks up a <see cref="MetadataV2Service"/> by name and gates
    /// it on the access policy. Returns the resolved service plus all of its publications and
    /// their resources, so capability/metadata handlers can build their response without further
    /// snapshot walking.
    /// </summary>
    /// <param name="context">HTTP context.</param>
    /// <param name="serviceName">Public service name (case-insensitive).</param>
    /// <param name="requiredProtocol">Optional protocol gate; when set, only matches
    /// services whose <see cref="MetadataV2Service.Protocols"/> list contains the
    /// protocol. Use to route a request to (for example) the MapServer publication
    /// when a service name is shared across multiple protocols.</param>
    /// <param name="scope">Access scope to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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

        if (service is null)
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

        var serviceAccessError = AccessPolicyHelpers.RequireServiceAccess(context, service, scope);
        if (serviceAccessError != null)
        {
            return new MetadataV2ServiceValidationResult(false, service, [], [], serviceAccessError);
        }

        var publications = snapshot.PublicationsForService(service.Metadata.Id);
        var resources = new List<MetadataV2Resource>(publications.Count);
        foreach (var pub in publications)
        {
            var resource = snapshot.ResolveResource(pub);
            if (resource is null)
            {
                continue;
            }
            // Drop resources the caller can't read; capability documents normally hide them.
            if (!AccessPolicyHelpers.IsResourceAccessible(context, resource, service, scope))
            {
                continue;
            }
            resources.Add(resource);
        }

        return new MetadataV2ServiceValidationResult(true, service, publications, resources, null);
    }
}

/// <summary>
/// Result of V2 service-level validation. Carries the service plus its full publication set and
/// the resources visible to the caller, so capability-document handlers don't have to walk the
/// graph again.
/// </summary>
internal readonly record struct MetadataV2ServiceValidationResult(
    bool IsValid,
    MetadataV2Service? Service,
    IReadOnlyList<MetadataV2Publication> Publications,
    IReadOnlyList<MetadataV2Resource> AccessibleResources,
    IResult? ErrorResult);
