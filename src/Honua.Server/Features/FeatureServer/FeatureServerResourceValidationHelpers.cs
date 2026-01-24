// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.FeatureServer;

internal static class FeatureServerResourceValidationHelpers
{
    internal readonly record struct ServiceLayerValidationResult(
        bool IsValid,
        ServiceDefinition? Service,
        LayerDefinition? Layer,
        IResult? ErrorResult);

    public static async Task<ServiceLayerValidationResult> ValidateServiceLayerAsync(
        IResourceValidator resourceValidator,
        string serviceId,
        int layerId,
        HttpContext context,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var resourceResult = await resourceValidator.ValidateServiceLayerAsync(serviceId, layerId, cancellationToken);
        if (!resourceResult.IsValid)
        {
            var errorMessage = resourceResult.ErrorMessage ?? "Resource not found.";

            if (resourceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
            {
                return new ServiceLayerValidationResult(
                    false,
                    null,
                    null,
                    StandardErrorHelpers.CreateBadRequest(context, errorMessage));
            }

            if (errorMessage.StartsWith("Service", StringComparison.OrdinalIgnoreCase))
            {
                FeatureServerLog.ServiceNotFound(logger, serviceId);
            }
            else if (errorMessage.StartsWith("Layer", StringComparison.OrdinalIgnoreCase))
            {
                FeatureServerLog.LayerNotFound(logger, serviceId, layerId);
            }

            return new ServiceLayerValidationResult(
                false,
                null,
                null,
                StandardErrorHelpers.CreateNotFound(context, errorMessage));
        }

        return new ServiceLayerValidationResult(
            true,
            resourceResult.Resource!.Service,
            resourceResult.Resource.Layer,
            null);
    }
}
