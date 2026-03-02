// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.FeatureServer;

internal static class FeatureServerResourceValidationHelpers
{
    internal readonly record struct ServiceValidationResult(
        bool IsValid,
        ServiceDefinition? Service,
        IResult? ErrorResult);

    internal readonly record struct ServiceLayerValidationResult(
        bool IsValid,
        ServiceDefinition? Service,
        LayerDefinition? Layer,
        IResult? ErrorResult);

    public static async Task<ServiceValidationResult> ValidateServiceAsync(
        IResourceValidator resourceValidator,
        string serviceId,
        HttpContext context,
        ILogger? logger,
        CancellationToken cancellationToken = default)
    {
        var serviceResult = await resourceValidator.ValidateServiceAsync(serviceId, cancellationToken);
        if (!serviceResult.IsValid)
        {
            var errorMessage = serviceResult.ErrorMessage ?? "Resource not found.";

            if (serviceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
            {
                return new ServiceValidationResult(
                    false,
                    null,
                    StandardErrorHelpers.CreateBadRequest(context, errorMessage));
            }

            if (logger != null)
            {
                FeatureServerLog.ServiceNotFound(logger, serviceId);
            }
            return new ServiceValidationResult(
                false,
                null,
                StandardErrorHelpers.CreateNotFound(context, errorMessage));
        }

        var service = serviceResult.Resource!;
        var protocolError = ProtocolValidationHelpers.ValidateProtocolEnabled(
            context,
            service,
            ServiceProtocols.FeatureServer);
        if (protocolError != null)
        {
            return new ServiceValidationResult(false, null, protocolError);
        }

        return new ServiceValidationResult(true, service, null);
    }

    public static async Task<ServiceLayerValidationResult> ValidateServiceLayerAsync(
        IResourceValidator resourceValidator,
        string serviceId,
        int layerId,
        HttpContext context,
        ILogger? logger,
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

            if (logger != null && errorMessage.StartsWith("Service", StringComparison.OrdinalIgnoreCase))
            {
                FeatureServerLog.ServiceNotFound(logger, serviceId);
            }
            else if (logger != null && errorMessage.StartsWith("Layer", StringComparison.OrdinalIgnoreCase))
            {
                FeatureServerLog.LayerNotFound(logger, serviceId, layerId);
            }

            return new ServiceLayerValidationResult(
                false,
                null,
                null,
                StandardErrorHelpers.CreateNotFound(context, errorMessage));
        }

        var service = resourceResult.Resource!.Service;
        var protocolError = ProtocolValidationHelpers.ValidateProtocolEnabled(
            context,
            service,
            ServiceProtocols.FeatureServer);
        if (protocolError != null)
        {
            return new ServiceLayerValidationResult(false, null, null, protocolError);
        }

        return new ServiceLayerValidationResult(
            true,
            service,
            resourceResult.Resource.Layer,
            null);
    }
}
