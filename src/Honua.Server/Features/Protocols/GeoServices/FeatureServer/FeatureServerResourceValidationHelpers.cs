// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Helpers;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer;

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
        var result = await ServiceResourceValidationHelpers.ValidateServiceAsync(
            resourceValidator,
            serviceId,
            ServiceProtocols.FeatureServer,
            context,
            logger != null ? id => FeatureServerLog.ServiceNotFound(logger, id) : null,
            cancellationToken: cancellationToken);

        return new ServiceValidationResult(result.IsValid, result.Service, result.ErrorResult);
    }

    public static async Task<ServiceLayerValidationResult> ValidateServiceLayerAsync(
        IResourceValidator resourceValidator,
        string serviceId,
        int layerId,
        HttpContext context,
        ILogger? logger,
        CancellationToken cancellationToken = default)
        => await ValidateServiceLayerAsync(
            resourceValidator,
            serviceId,
            layerId,
            context,
            logger,
            ServiceProtocols.FeatureServer,
            cancellationToken)
            .ConfigureAwait(false);

    public static async Task<ServiceLayerValidationResult> ValidateServiceLayerAsync(
        IResourceValidator resourceValidator,
        string serviceId,
        int layerId,
        HttpContext context,
        ILogger? logger,
        string requiredProtocol,
        CancellationToken cancellationToken = default)
    {
        var result = await ServiceResourceValidationHelpers.ValidateServiceLayerAsync(
            resourceValidator,
            serviceId,
            layerId,
            requiredProtocol,
            context,
            logger != null ? id => FeatureServerLog.ServiceNotFound(logger, id) : null,
            logger != null ? (id, layer) => FeatureServerLog.LayerNotFound(logger, id, layer) : null,
            cancellationToken).ConfigureAwait(false);

        return new ServiceLayerValidationResult(
            result.IsValid,
            result.Service,
            result.Layer,
            result.ErrorResult);
    }
}
