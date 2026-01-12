// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.OData.Services;
using Honua.Server.Features.OgcFeatures;

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
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Validation result with layer or error response</returns>
    public static async Task<LayerValidationResult> ValidateLayerWithAccessAsync(
        HttpContext context,
        int layerId,
        ValidationProtocol protocol,
        AccessScope scope = AccessScope.Read,
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

        return new LayerValidationResult(true, layer, null);
    }

    /// <summary>
    /// Validates collection existence and access in a single operation for OGC API Features.
    /// </summary>
    /// <param name="context">HTTP context containing request services and user information</param>
    /// <param name="collectionId">Collection ID string to validate and parse</param>
    /// <param name="scope">Access scope required for the request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Validation result with layer or error response</returns>
    public static async Task<LayerValidationResult> ValidateCollectionWithAccessAsync(
        HttpContext context,
        string collectionId,
        AccessScope scope = AccessScope.Read,
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

        return new LayerValidationResult(true, layer, null);
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
}
