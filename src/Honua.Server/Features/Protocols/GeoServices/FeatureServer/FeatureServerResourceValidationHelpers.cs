// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Helpers;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer;

internal static class FeatureServerResourceValidationHelpers
{
    private const string FeatureServerProtocol = "FeatureServer";

    /// <summary>
    /// Result of resolving a feature service through the Metadata v2 graph.
    /// </summary>
    internal readonly record struct ServiceValidationV2Result(
        bool IsValid,
        MetadataV2Service? Service,
        IResult? ErrorResult);

    /// <summary>
    /// Result of resolving a feature service layer through the Metadata v2 graph.
    /// </summary>
    internal readonly record struct ServiceLayerValidationV2Result(
        bool IsValid,
        MetadataV2Service? Service,
        MetadataV2Publication? Publication,
        MetadataV2Resource? Resource,
        IResult? ErrorResult);

    public static async Task<ServiceValidationV2Result> ValidateServiceV2Async(
        IResourceValidator resourceValidator,
        string serviceId,
        HttpContext context,
        ILogger? logger,
        CancellationToken cancellationToken = default)
    {
        var result = await ServiceResourceValidationHelpers.ValidateServiceV2Async(
            resourceValidator,
            serviceId,
            FeatureServerProtocol,
            context,
            logger != null ? id => FeatureServerLog.ServiceNotFound(logger, id) : null,
            requireServiceAccess: false,
            cancellationToken).ConfigureAwait(false);

        return new ServiceValidationV2Result(result.IsValid, result.Service, result.ErrorResult);
    }

    public static async Task<ServiceLayerValidationV2Result> ValidateServiceLayerV2Async(
        IResourceValidator resourceValidator,
        string serviceId,
        int layerId,
        HttpContext context,
        ILogger? logger,
        CancellationToken cancellationToken = default)
        => await ValidateServiceLayerV2Async(
            resourceValidator,
            serviceId,
            layerId,
            context,
            logger,
            FeatureServerProtocol,
            cancellationToken).ConfigureAwait(false);

    public static async Task<ServiceLayerValidationV2Result> ValidateServiceLayerV2Async(
        IResourceValidator resourceValidator,
        string serviceId,
        int layerId,
        HttpContext context,
        ILogger? logger,
        string requiredProtocol,
        CancellationToken cancellationToken = default)
    {
        var result = await ServiceResourceValidationHelpers.ValidateServiceLayerV2Async(
            resourceValidator,
            serviceId,
            layerId,
            requiredProtocol,
            context,
            logger != null ? id => FeatureServerLog.ServiceNotFound(logger, id) : null,
            logger != null ? (id, layer) => FeatureServerLog.LayerNotFound(logger, id, layer) : null,
            cancellationToken).ConfigureAwait(false);

        return new ServiceLayerValidationV2Result(
            result.IsValid,
            result.Service,
            result.Publication,
            result.Resource,
            result.ErrorResult);
    }
}
