// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.OData.Services;
using Honua.Server.Features.OgcFeatures;
using Microsoft.Extensions.DependencyInjection;

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
        var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, scope: scope);
        if (accessError != null)
        {
            return new LayerValidationResult(false, null, accessError);
        }

        var effectiveRequiredProtocol = string.IsNullOrWhiteSpace(requiredProtocol)
            ? protocol switch
            {
                ValidationProtocol.OData => ServiceProtocols.OData,
                ValidationProtocol.OgcFeatures => ServiceProtocols.OgcFeatures,
                _ => null
            }
            : requiredProtocol;
        if (!string.IsNullOrWhiteSpace(effectiveRequiredProtocol) &&
            !await IsProtocolEnabledForLayerAsync(context, layer, effectiveRequiredProtocol, effectiveToken))
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
        var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, scope: scope);
        if (accessError != null)
        {
            return new LayerValidationResult(false, null, accessError);
        }

        if (!string.IsNullOrWhiteSpace(requiredProtocol))
        {
            if (!await IsProtocolEnabledForLayerAsync(context, layer, requiredProtocol, cancellationToken))
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
        var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, scope: scope);
        if (accessError != null)
        {
            return new LayerValidationResult(false, null, accessError);
        }

        if (!string.IsNullOrWhiteSpace(requiredProtocol))
        {
            if (!await IsProtocolEnabledForLayerAsync(context, layer, requiredProtocol, cancellationToken))
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
    /// with <see cref="ServiceDataEditorAuthorization.RequireLayerDataEditorAsync"/>.
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

        var rbacError = await ServiceDataEditorAuthorization.RequireLayerDataEditorAsync(
            context, layerId, cancellationToken);
        if (rbacError != null)
        {
            return new LayerValidationResult(false, null, rbacError);
        }

        return layerValidation;
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

        var rbacError = await ServiceDataEditorAuthorization.RequireLayerDataEditorAsync(
            context, layerValidation.Layer!.Id, cancellationToken);
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

        var rbacError = await ServiceDataEditorAuthorization.RequireLayerDataEditorAsync(
            context, layerId, cancellationToken);
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

    private static async Task<bool> IsProtocolEnabledForLayerAsync(
        HttpContext context,
        LayerDefinition layer,
        string protocol,
        CancellationToken cancellationToken)
    {
        var layerAllowsProtocol = ServiceProtocols.IsProtocolEnabled(layer.Metadata, protocol);
        var layerCatalog = context.RequestServices.GetRequiredService<ILayerCatalog>();
        var services = await layerCatalog.ListServicesAsync(cancellationToken);

        var relatedServices = services
            .Where(service => service.Layers.Any(candidate => candidate.Id == layer.Id))
            .ToArray();
        if (relatedServices.Length == 0)
        {
            return layerAllowsProtocol;
        }

        return relatedServices.Any(service => ServiceProtocols.IsProtocolEnabled(service.Metadata, protocol));
    }
}
