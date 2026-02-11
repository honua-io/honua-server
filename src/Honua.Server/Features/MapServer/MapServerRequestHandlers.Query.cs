// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.FeatureServer;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.MapServer;

internal static partial class MapServerEndpoints
{
    /// <summary>
    /// Handle MapServer layer query (GET) using FeatureServer query logic.
    /// </summary>
    private static async Task<IResult> HandleLayerQueryGet(string serviceId, int layerId, HttpContext context)
    {
        var serviceError = await TryValidateMapServerServiceAsync(serviceId, context);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!FeatureServerEndpoints.TryValidateAllowedParameters(
                context.Request.Query,
                queryValidator,
                FeatureServerEndpoints.FeatureServerQueryAllowedParameters,
                out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        if (!FeatureServerEndpoints.TryParseQueryParameters(
                FeatureServerEndpoints.ToCaseInsensitiveDictionary(context.Request.Query),
                out var queryParams,
                out var parseError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [parseError ?? "Invalid query parameter."]);
        }

        var queryHandler = context.RequestServices.GetRequiredService<FeatureServerQueryHandler>();
        var cancellationToken = FeatureServerEndpoints.GetTimeoutAwareCancellationToken(context);

        return await queryHandler.HandleQueryFeaturesAsync(
            serviceId,
            layerId,
            queryParams,
            context,
            queryValidator,
            cancellationToken);
    }

    /// <summary>
    /// Handle MapServer layer query (POST) using FeatureServer query logic.
    /// </summary>
    private static async Task<IResult> HandleLayerQueryPost(string serviceId, int layerId, HttpContext context)
    {
        var serviceError = await TryValidateMapServerServiceAsync(serviceId, context);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!FeatureServerEndpoints.TryValidateAllowedParameters(
                context.Request.Query,
                queryValidator,
                FeatureServerEndpoints.FeatureServerQueryAllowedParameters,
                out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var cancellationToken = FeatureServerEndpoints.GetTimeoutAwareCancellationToken(context);
        var (values, readError) = await FeatureServerEndpoints.TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (values == null)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [readError ?? "Invalid request body."]);
        }

        if (!FeatureServerEndpoints.TryValidateAllowedParameters(
                values,
                queryValidator,
                FeatureServerEndpoints.FeatureServerQueryAllowedParameters,
                out error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        if (!FeatureServerEndpoints.TryParseQueryParameters(values, out var queryParams, out var parseError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [parseError ?? "Invalid query parameter."]);
        }

        var queryHandler = context.RequestServices.GetRequiredService<FeatureServerQueryHandler>();
        return await queryHandler.HandleQueryFeaturesAsync(
            serviceId,
            layerId,
            queryParams,
            context,
            queryValidator,
            cancellationToken);
    }

    private static async Task<IResult?> TryValidateMapServerServiceAsync(string serviceId, HttpContext context)
    {
        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var serviceResult = await resourceValidator.ValidateServiceAsync(serviceId, context.RequestAborted);
        if (!serviceResult.IsValid)
        {
            var errorMessage = serviceResult.ErrorMessage ?? "Service not found.";
            if (serviceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
            {
                return StandardErrorHelpers.CreateBadRequest(context, errorMessage);
            }

            return StandardErrorHelpers.CreateNotFound(context, errorMessage);
        }

        var service = serviceResult.Resource!;
        return ProtocolValidationHelpers.ValidateProtocolEnabled(context, service, ServiceProtocols.MapServer);
    }
}
