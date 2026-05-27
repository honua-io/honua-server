// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Protocols.OData.Services;
using Honua.Server.Features.Protocols.Ogc.Api.Features;
using Microsoft.Extensions.DependencyInjection;
using AccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;

namespace Honua.Server.Features.Infrastructure.Validation;

/// <summary>
/// Shared helper methods for layer validation and access checking across CRUD handlers.
/// Eliminates DRY violations by providing a unified validation pattern for all protocols.
/// </summary>
internal static class LayerValidationHelpers
{
    /// <summary>
    /// Protocol-specific error format options for layer validation.
    /// </summary>
    internal enum ValidationProtocol
    {
        OData,
        OgcFeatures
    }

    /// <summary>
    /// Result of combined layer validation and access checking.
    /// </summary>
    /// <param name="IsValid">Whether validation succeeded</param>
    /// <param name="Layer">The validated layer if successful</param>
    /// <param name="ErrorResult">IResult containing appropriate error response if validation failed</param>
    internal readonly record struct LayerValidationResult(
        bool IsValid,
        LayerDefinition? Layer,
        IResult? ErrorResult);

    /// <summary>
    /// Result of V2 publication validation + access checking. Replaces
    /// <see cref="LayerValidationResult"/> for consumers that have been ported off the v1
    /// LayerDefinition shape. Carries the publication, the canonical resource, and the
    /// resolved service so the caller has everything needed for downstream operations.
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
    /// Validates layer existence and access in a single operation.
    /// Returns protocol-specific error responses while maintaining existing error message formats.
    /// </summary>
    /// <param name="context">HTTP context containing request services and user information</param>
    /// <param name="layerId">Layer ID to validate</param>
    /// <param name="protocol">Protocol format for error responses</param>
    /// <param name="scope">Access scope required for the request</param>
    /// <param name="requiredProtocol">Protocol that must be enabled for the resolved layer.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Validation result with layer or error response</returns>
    public static async Task<LayerValidationResult> ValidateLayerWithAccessAsync(
        HttpContext context,
        int layerId,
        ValidationProtocol protocol,
        AccessScope scope = AccessScope.Read,
        string? requiredProtocol = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveToken = protocol == ValidationProtocol.OData
            ? ODataUtilityService.GetTimeoutAwareCancellationToken(context)
            : cancellationToken;

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var layerResult = await resourceValidator.ValidateLayerAsync(layerId, effectiveToken);

        if (!layerResult.IsValid)
        {
            var layerErrorMessage = layerResult.ErrorMessage ?? $"Layer {layerId} not found";
            var statusCode = layerResult.ErrorCode == ResourceValidationError.InvalidIdentifier ? 400 : 404;

            var errorResult = protocol switch
            {
                ValidationProtocol.OData => CreateODataError(context, layerErrorMessage, statusCode),
                ValidationProtocol.OgcFeatures => CreateOgcError(context, layerErrorMessage, statusCode),
                _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "Unsupported validation protocol")
            };

            return new LayerValidationResult(false, null, errorResult);
        }

        var layer = layerResult.Resource!;
        var effectiveRequiredProtocol = string.IsNullOrWhiteSpace(requiredProtocol)
            ? protocol switch
            {
                ValidationProtocol.OData => ServiceProtocols.OData,
                ValidationProtocol.OgcFeatures => ServiceProtocols.OgcFeatures,
                _ => null
            }
            : requiredProtocol;
        var relatedServices = await GetRelatedServicesAsync(context, layer.Id, effectiveToken);
        var resolvedService = ResolvePrimaryService(relatedServices, effectiveRequiredProtocol);
        var accessDecision = EvaluateLayerAccess(context, layer, resolvedService, scope);
        var accessError = AccessPolicyHelpers.CreateAccessDeniedResult(context, accessDecision);
        if (accessError != null)
        {
            return new LayerValidationResult(false, null, accessError);
        }

        if (!string.IsNullOrWhiteSpace(effectiveRequiredProtocol) &&
            !IsProtocolEnabledForLayer(layer, resolvedService, effectiveRequiredProtocol))
        {
            var protocolError = protocol switch
            {
                ValidationProtocol.OData => CreateODataError(
                    context,
                    $"{effectiveRequiredProtocol} is not enabled for this service.",
                    StatusCodes.Status404NotFound),
                ValidationProtocol.OgcFeatures => CreateOgcError(
                    context,
                    $"{effectiveRequiredProtocol} is not enabled for this service.",
                    StatusCodes.Status404NotFound),
                _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "Unsupported validation protocol")
            };

            return new LayerValidationResult(false, null, protocolError);
        }

        return new LayerValidationResult(true, layer, null);
    }

    /// <summary>
    /// Validates layer existence and access using standard error responses.
    /// </summary>
    /// <param name="context">HTTP context containing request services and user information</param>
    /// <param name="layerId">Layer ID to validate</param>
    /// <param name="scope">Access scope required for the request</param>
    /// <param name="requiredProtocol">Protocol that must be enabled for the resolved layer.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Validation result with layer or error response</returns>
    public static async Task<LayerValidationResult> ValidateLayerWithAccessAsync(
        HttpContext context,
        int layerId,
        AccessScope scope = AccessScope.Read,
        string? requiredProtocol = null,
        CancellationToken cancellationToken = default)
    {
        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var layerResult = await resourceValidator.ValidateLayerAsync(layerId, cancellationToken);

        if (!layerResult.IsValid)
        {
            var errorMessage = layerResult.ErrorMessage ?? $"Layer {layerId} not found";
            var errorResult = StandardErrorHelpers.CreateNotFound(context, errorMessage);
            return new LayerValidationResult(false, null, errorResult);
        }

        var layer = layerResult.Resource!;
        var relatedServices = await GetRelatedServicesAsync(context, layer.Id, cancellationToken);
        var resolvedService = ResolvePrimaryService(relatedServices, requiredProtocol);
        var accessDecision = EvaluateLayerAccess(context, layer, resolvedService, scope);
        var accessError = AccessPolicyHelpers.CreateAccessDeniedResult(context, accessDecision);
        if (accessError != null)
        {
            return new LayerValidationResult(false, null, accessError);
        }

        if (!string.IsNullOrWhiteSpace(requiredProtocol))
        {
            if (!IsProtocolEnabledForLayer(layer, resolvedService, requiredProtocol))
            {
                var protocolError = StandardErrorHelpers.CreateNotFound(
                    context,
                    $"{requiredProtocol} is not enabled for this service.");
                return new LayerValidationResult(false, null, protocolError);
            }
        }

        return new LayerValidationResult(true, layer, null);
    }

    /// <summary>
    /// Validates collection existence and access in a single operation for OGC API Features.
    /// </summary>
    /// <param name="context">HTTP context containing request services and user information</param>
    /// <param name="collectionId">Collection ID string to validate and parse</param>
    /// <param name="scope">Access scope required for the request</param>
    /// <param name="requiredProtocol">Protocol that must be enabled for the resolved collection layer.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Validation result with layer or error response</returns>
    public static async Task<LayerValidationResult> ValidateCollectionWithAccessAsync(
        HttpContext context,
        string collectionId,
        AccessScope scope = AccessScope.Read,
        string? requiredProtocol = ServiceProtocols.OgcFeatures,
        CancellationToken cancellationToken = default)
    {
        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var collectionResult = await resourceValidator.ValidateCollectionAsync(collectionId, cancellationToken);

        if (!collectionResult.IsValid)
        {
            var errorMessage = collectionResult.ErrorMessage ?? $"Collection '{collectionId}' not found.";
            var errorResult = StandardErrorHelpers.CreateNotFound(context, errorMessage);
            return new LayerValidationResult(false, null, errorResult);
        }

        var layer = collectionResult.Resource!;
        var relatedServices = await GetRelatedServicesAsync(context, layer.Id, cancellationToken);
        var resolvedService = ResolvePrimaryService(relatedServices, requiredProtocol);
        var accessDecision = EvaluateLayerAccess(context, layer, resolvedService, scope);
        var accessError = AccessPolicyHelpers.CreateAccessDeniedResult(context, accessDecision);
        if (accessError != null)
        {
            return new LayerValidationResult(false, null, accessError);
        }

        if (!string.IsNullOrWhiteSpace(requiredProtocol))
        {
            if (!IsProtocolEnabledForLayer(layer, resolvedService, requiredProtocol))
            {
                var protocolError = StandardErrorHelpers.CreateNotFound(
                    context,
                    $"{requiredProtocol} is not enabled for this service.");
                return new LayerValidationResult(false, null, protocolError);
            }
        }

        return new LayerValidationResult(true, layer, null);
    }

    /// <summary>
    /// Validates layer existence, write access, and RBAC data-editor role in a single call.
    /// Combines <see cref="ValidateLayerWithAccessAsync(HttpContext, int, AccessScope, string, CancellationToken)"/>
    /// with <see cref="ServiceDataEditorAuthorization.RequireLayerDataEditorAsync(HttpContext, LayerDefinition, ServiceDefinition?, CancellationToken)"/>.
    /// </summary>
    public static async Task<LayerValidationResult> ValidateWriteAccessAsync(
        HttpContext context,
        int layerId,
        CancellationToken cancellationToken = default)
    {
        var layerValidation = await ValidateLayerWithAccessAsync(
            context, layerId, scope: AccessScope.Write, cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation;
        }

        var service = await ResolvePrimaryServiceAsync(context, layerId, cancellationToken: cancellationToken);
        var rbacError = await ServiceDataEditorAuthorization.RequireLayerDataEditorAsync(
            context, layerValidation.Layer!, service, cancellationToken);
        if (rbacError != null)
        {
            return new LayerValidationResult(false, null, rbacError);
        }

        return layerValidation;
    }

    /// <summary>
    /// Resolves the canonical service for the specified layer and protocol.
    /// When a layer belongs to multiple services, the protocol-enabled service with the
    /// lexicographically earliest name wins to keep routing deterministic.
    /// </summary>
    public static async Task<ServiceDefinition?> ResolvePrimaryServiceAsync(
        HttpContext context,
        int layerId,
        string? preferredProtocol = null,
        CancellationToken cancellationToken = default)
    {
        var relatedServices = await GetRelatedServicesAsync(context, layerId, cancellationToken);
        return ResolvePrimaryService(relatedServices, preferredProtocol);
    }

    /// <summary>
    /// Resolves the canonical service name for the specified layer and protocol.
    /// </summary>
    public static async Task<string?> ResolvePrimaryServiceNameAsync(
        HttpContext context,
        int layerId,
        string? preferredProtocol = null,
        CancellationToken cancellationToken = default)
    {
        var service = await ResolvePrimaryServiceAsync(context, layerId, preferredProtocol, cancellationToken);
        return service?.Name;
    }

    /// <summary>
    /// Builds a deterministic layer-to-service map for a protocol-specific route surface.
    /// </summary>
    public static IReadOnlyDictionary<int, ServiceDefinition> BuildPrimaryServiceMap(
        IEnumerable<ServiceDefinition> services,
        string? preferredProtocol = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services
            .SelectMany(service => service.Layers.Select(layer => (LayerId: layer.Id, Service: service)))
            .GroupBy(static entry => entry.LayerId)
            .ToDictionary(
                static group => group.Key,
                group => ResolvePrimaryService(
                    group.Select(static entry => entry.Service).DistinctBy(static service => service.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                    preferredProtocol)
                    ?? throw new InvalidOperationException("Layer service group unexpectedly resolved to no service."));
    }

    /// <summary>
    /// Validates collection existence, write access, and RBAC data-editor role for OGC Features.
    /// </summary>
    public static async Task<LayerValidationResult> ValidateCollectionWriteAccessAsync(
        HttpContext context,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        var layerValidation = await ValidateCollectionWithAccessAsync(
            context, collectionId, scope: AccessScope.Write, cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation;
        }

        var service = await ResolvePrimaryServiceAsync(
            context,
            layerValidation.Layer!.Id,
            ServiceProtocols.OgcFeatures,
            cancellationToken);
        var rbacError = await ServiceDataEditorAuthorization.RequireLayerDataEditorAsync(
            context, layerValidation.Layer!, service, cancellationToken);
        if (rbacError != null)
        {
            return new LayerValidationResult(false, null, rbacError);
        }

        return layerValidation;
    }

    /// <summary>
    /// Validates layer existence, write access, and RBAC data-editor role with OData-specific
    /// error formatting.
    /// </summary>
    public static async Task<LayerValidationResult> ValidateODataWriteAccessAsync(
        HttpContext context,
        int layerId,
        CancellationToken cancellationToken = default)
    {
        var layerValidation = await ValidateLayerWithAccessAsync(
            context,
            layerId,
            ValidationProtocol.OData,
            scope: AccessScope.Write,
            requiredProtocol: ServiceProtocols.OData,
            cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation;
        }

        var service = await ResolvePrimaryServiceAsync(
            context,
            layerId,
            ServiceProtocols.OData,
            cancellationToken);
        var rbacError = await ServiceDataEditorAuthorization.RequireLayerDataEditorAsync(
            context, layerValidation.Layer!, service, cancellationToken);
        if (rbacError != null)
        {
            return new LayerValidationResult(false, null, rbacError);
        }

        return layerValidation;
    }

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

        return ODataUtilityService.CreateODataError(context, errorCode, message, statusCode);
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

    private static async Task<ServiceDefinition[]> GetRelatedServicesAsync(
        HttpContext context,
        int layerId,
        CancellationToken cancellationToken)
    {
        var layerCatalog = context.RequestServices.GetRequiredService<ILayerCatalog>();
        var services = await layerCatalog.ListServicesAsync(cancellationToken);
        return services
            .Where(service => service.Layers.Any(candidate => candidate.Id == layerId))
            .ToArray();
    }

    private static AccessDecision EvaluateLayerAccess(
        HttpContext context,
        LayerDefinition layer,
        ServiceDefinition? service,
        AccessScope scope)
    {
        return AccessPolicyHelpers.EvaluateAccess(
            context,
            layer.Metadata?.AccessPolicy,
            service?.Metadata?.AccessPolicy,
            scope);
    }

    private static bool IsProtocolEnabledForLayer(
        LayerDefinition layer,
        ServiceDefinition? service,
        string protocol)
    {
        return service == null
            ? ServiceProtocols.IsProtocolEnabled(layer.Metadata, protocol)
            : ServiceProtocols.IsProtocolEnabled(service.Metadata, protocol);
    }

    private static ServiceDefinition? ResolvePrimaryService(
        IEnumerable<ServiceDefinition> relatedServices,
        string? preferredProtocol)
    {
        ArgumentNullException.ThrowIfNull(relatedServices);

        return relatedServices
            .OrderByDescending(service =>
                !string.IsNullOrWhiteSpace(preferredProtocol) &&
                ServiceProtocols.IsProtocolEnabled(service.Metadata, preferredProtocol))
            .ThenBy(service => service.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
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
        var effectiveToken = protocol == ValidationProtocol.OData
            ? ODataUtilityService.GetTimeoutAwareCancellationToken(context)
            : cancellationToken;

        var snapshot = await GetV2SnapshotAsync(context, effectiveToken).ConfigureAwait(false);
        var (publication, resource, service) = ResolveV2Triple(snapshot, layerId, requiredProtocol);

        if (publication is null || resource is null)
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
            !ServiceProtocols.IsProtocolEnabled(service, requiredProtocol))
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

        if (publication is null || resource is null)
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
            !ServiceProtocols.IsProtocolEnabled(service, requiredProtocol))
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
        string? requiredProtocol = ServiceProtocols.OgcFeatures,
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
            var resource = snapshot.ResolveResource(p);
            return string.Equals(p.ServiceLocalId, collectionId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(p.Path, collectionId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(p.Metadata.Name, collectionId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(p.Metadata.Id, collectionId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(resource?.Metadata.Name, collectionId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(resource?.Metadata.Title, collectionId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(resource?.Metadata.Id, collectionId, StringComparison.OrdinalIgnoreCase);
        }

        bool IsVisiblePublication(MetadataV2Publication p)
        {
            var resource = snapshot.ResolveResource(p);
            var service = snapshot.Index.ServicesById.TryGetValue(p.ServiceId, out var resolvedService)
                ? resolvedService
                : null;
            return IsRuntimeVisible(p.Status) &&
                   IsRuntimeVisible(resource?.Status) &&
                   IsRuntimeVisible(service?.Status);
        }

        MetadataV2Publication? publication = null;

        // When a protocol is specified, prefer the matching publication so a STAC
        // collection request that shares a serviceLocalId with another protocol's
        // publication (e.g. an Esri Feature Service publication with the same local id)
        // doesn't get dispatched to the wrong protocol.
        if (!string.IsNullOrWhiteSpace(requiredProtocol))
        {
            publication = snapshot.Graph.Publications.FirstOrDefault(p =>
                IsVisiblePublication(p) &&
                MatchesCollectionId(p) &&
                snapshot.Index.ServicesById.TryGetValue(p.ServiceId, out var s) &&
                ServiceProtocols.IsProtocolEnabled(s, requiredProtocol));
        }
        publication ??= snapshot.Graph.Publications.FirstOrDefault(p => IsVisiblePublication(p) && MatchesCollectionId(p));

        if (publication is null)
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

        if (resource is null)
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
            !ServiceProtocols.IsProtocolEnabled(service, requiredProtocol))
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
            if (!pub.LayerIndex.HasValue || !IsRuntimeVisible(pub.Status))
            {
                continue;
            }
            if (!snapshot.Index.ServicesById.TryGetValue(pub.ServiceId, out var service))
            {
                continue;
            }
            if (!IsRuntimeVisible(service.Status))
            {
                continue;
            }
            var resource = snapshot.ResolveResource(pub);
            if (!IsRuntimeVisible(resource?.Status))
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
            var existingMatches = hasProtocolGate && ServiceProtocols.IsProtocolEnabled(existing.Service, requiredProtocol!);
            var candidateMatches = hasProtocolGate && ServiceProtocols.IsProtocolEnabled(service, requiredProtocol!);
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
            if (pub.LayerIndex != layerId || !IsRuntimeVisible(pub.Status)) continue;
            if (!snapshot.Index.ServicesById.TryGetValue(pub.ServiceId, out var service)) continue;
            if (!IsRuntimeVisible(service.Status)) continue;
            var resource = snapshot.ResolveResource(pub);
            if (!IsRuntimeVisible(resource?.Status)) continue;

            if (!string.IsNullOrWhiteSpace(requiredProtocol) &&
                !ServiceProtocols.IsProtocolEnabled(service, requiredProtocol))
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

    private static async Task<MetadataV2GraphSnapshot> GetV2SnapshotAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var provider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        return await provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsRuntimeVisible(MetadataV2Status? status)
        => status is null ||
           (status.Lifecycle is not (MetadataV2LifecycleStatus.Retired or MetadataV2LifecycleStatus.Archived) &&
            status.State is not MetadataV2OperationalState.Failed);

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
            .Where(p =>
            {
                if (p.LayerIndex != layerId || !IsRuntimeVisible(p.Status))
                {
                    return false;
                }

                var resource = snapshot.ResolveResource(p);
                var service = snapshot.Index.ServicesById.TryGetValue(p.ServiceId, out var resolvedService)
                    ? resolvedService
                    : null;
                return IsRuntimeVisible(resource?.Status) && IsRuntimeVisible(service?.Status);
            })
            .ToList();

        if (!string.IsNullOrWhiteSpace(requiredProtocol))
        {
            var preferred = candidatePublications
                .Where(p =>
                    snapshot.Index.ServicesById.TryGetValue(p.ServiceId, out var s) &&
                    ServiceProtocols.IsProtocolEnabled(s, requiredProtocol))
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
    /// V2 sibling of <see cref="ValidateCollectionWriteAccessAsync"/>. Combines
    /// <see cref="ValidateCollectionWithAccessV2Async"/> with the V2 RBAC data-editor
    /// helper so OGC API Features CRUD/Transaction handlers can run a single check.
    /// Returns the matched publication + resource + service plus an
    /// <see cref="IResult"/> error when validation or authorization fails.
    /// </summary>
    /// <param name="context">HTTP context carrying request services and principal.</param>
    /// <param name="collectionId">Collection identifier from the route.</param>
    /// <param name="requiredProtocol">Optional protocol gate. Defaults to
    /// <see cref="ServiceProtocols.OgcFeatures"/> to mirror the v1 method.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<MetadataV2ValidationResult> ValidateCollectionWriteAccessV2Async(
        HttpContext context,
        string collectionId,
        string? requiredProtocol = ServiceProtocols.OgcFeatures,
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
                IsRuntimeVisible(s.Status) &&
                ServiceProtocols.IsProtocolEnabled(s, requiredProtocol) &&
                string.Equals(s.Metadata.Name, serviceName, StringComparison.OrdinalIgnoreCase));
        }
        service ??= snapshot.Index.ServicesByName.TryGetValue(serviceName, out var s) && IsRuntimeVisible(s.Status) ? s : null;

        if (service is null)
        {
            return new MetadataV2ServiceValidationResult(
                false, null, [], [],
                StandardErrorHelpers.CreateNotFound(context, $"Service '{serviceName}' not found."));
        }

        if (!string.IsNullOrWhiteSpace(requiredProtocol) &&
            !ServiceProtocols.IsProtocolEnabled(service, requiredProtocol))
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

        var publications = snapshot.PublicationsForService(service.Metadata.Id)
            .Where(publication => IsRuntimeVisible(publication.Status))
            .ToArray();
        var resources = new List<MetadataV2Resource>(publications.Length);
        foreach (var pub in publications)
        {
            var resource = snapshot.ResolveResource(pub);
            if (resource is null)
            {
                continue;
            }
            if (!IsRuntimeVisible(resource.Status))
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
