// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
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

    /// <summary>
    /// V2 equivalent of <see cref="ServiceValidationResult"/>.
    /// </summary>
    internal readonly record struct ServiceValidationV2Result(
        bool IsValid,
        MetadataV2Service? Service,
        IResult? ErrorResult);

    /// <summary>
    /// V2 equivalent of <see cref="ServiceLayerValidationResult"/> carrying the resolved
    /// (service, publication, resource) triple.
    /// </summary>
    internal readonly record struct ServiceLayerValidationV2Result(
        bool IsValid,
        MetadataV2Service? Service,
        MetadataV2Publication? Publication,
        MetadataV2Resource? Resource,
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

    /// <summary>
    /// V2 overload of <see cref="ValidateServiceAsync(IResourceValidator, string, HttpContext, ILogger?, CancellationToken)"/>
    /// that resolves the canonical <see cref="MetadataV2Service"/> via the V2 graph.
    /// </summary>
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
            ServiceProtocols.FeatureServer,
            context,
            logger != null ? id => FeatureServerLog.ServiceNotFound(logger, id) : null,
            requireServiceAccess: false,
            cancellationToken).ConfigureAwait(false);

        return new ServiceValidationV2Result(result.IsValid, result.Service, result.ErrorResult);
    }

    /// <summary>
    /// V2 overload of <see cref="ValidateServiceLayerAsync(IResourceValidator, string, int, HttpContext, ILogger?, CancellationToken)"/>
    /// that resolves the (service, publication, resource) triple via the V2 graph.
    /// </summary>
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
            ServiceProtocols.FeatureServer,
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
