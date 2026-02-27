// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.FeatureServer;
using Honua.Server.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.Extensions.Primitives;

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

        var queryHandler = context.RequestServices.GetRequiredService<IFeatureQueryDispatcher>();
        var cancellationToken = FeatureServerEndpoints.GetTimeoutAwareCancellationToken(context);
        var values = FeatureServerEndpoints.ToCaseInsensitiveDictionary(context.Request.Query);

        return await queryHandler.HandleQueryFeaturesAsync(
            serviceId,
            layerId,
            values,
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

        var queryHandler = context.RequestServices.GetRequiredService<IFeatureQueryDispatcher>();
        return await queryHandler.HandleQueryFeaturesAsync(
            serviceId,
            layerId,
            values,
            context,
            queryValidator,
            cancellationToken);
    }

    /// <summary>
    /// Handle MapServer service query (GET) by routing to a concrete layer query.
    /// </summary>
    private static async Task<IResult> HandleServiceQueryGet(string serviceId, HttpContext context)
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
                FeatureServerEndpoints.FeatureServerServiceQueryAllowedParameters,
                out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var values = FeatureServerEndpoints.ToCaseInsensitiveDictionary(context.Request.Query);
        if (!TryResolveServiceLayerId(values, out var layerId, out var layerError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [layerError ?? "layerId parameter is required."]);
        }

        var queryHandler = context.RequestServices.GetRequiredService<IFeatureQueryDispatcher>();
        var cancellationToken = FeatureServerEndpoints.GetTimeoutAwareCancellationToken(context);

        return await queryHandler.HandleQueryFeaturesAsync(
            serviceId,
            layerId,
            values,
            context,
            queryValidator,
            cancellationToken);
    }

    /// <summary>
    /// Handle MapServer service query (POST) by routing to a concrete layer query.
    /// </summary>
    private static async Task<IResult> HandleServiceQueryPost(string serviceId, HttpContext context)
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
                FeatureServerEndpoints.FeatureServerServiceQueryAllowedParameters,
                out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var cancellationToken = FeatureServerEndpoints.GetTimeoutAwareCancellationToken(context);
        var (bodyValues, readError) = await FeatureServerEndpoints.TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (bodyValues == null)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [readError ?? "Invalid request body."]);
        }

        if (!FeatureServerEndpoints.TryValidateAllowedParameters(
                bodyValues,
                queryValidator,
                FeatureServerEndpoints.FeatureServerServiceQueryAllowedParameters,
                out error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var mergedValues = FeatureServerEndpoints.ToCaseInsensitiveDictionary(context.Request.Query);
        foreach (var pair in bodyValues)
        {
            mergedValues[pair.Key] = pair.Value;
        }

        if (!TryResolveServiceLayerId(mergedValues, out var layerId, out var layerError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [layerError ?? "layerId parameter is required."]);
        }

        var queryHandler = context.RequestServices.GetRequiredService<IFeatureQueryDispatcher>();
        return await queryHandler.HandleQueryFeaturesAsync(
            serviceId,
            layerId,
            mergedValues,
            context,
            queryValidator,
            cancellationToken);
    }

    private static bool TryResolveServiceLayerId(
        Dictionary<string, StringValues> values,
        out int layerId,
        out string? error)
    {
        layerId = default;
        error = null;

        if (values.TryGetValue("layerId", out var layerIdRaw) && !StringValues.IsNullOrEmpty(layerIdRaw))
        {
            if (!int.TryParse(layerIdRaw.ToString(), out layerId))
            {
                error = "layerId must be an integer";
                return false;
            }

            return true;
        }

        if (!values.TryGetValue("layers", out var layersRaw) || StringValues.IsNullOrEmpty(layersRaw))
        {
            error = "layerId parameter is required for service-level query";
            return false;
        }

        var layersValue = layersRaw.ToString();
        var segments = new List<string>();
        foreach (var segment in layersValue.Split(',', StringSplitOptions.None))
        {
            var trimmed = segment.Trim();
            if (trimmed.Length == 0)
            {
                error = "layers must contain exactly one layer identifier";
                return false;
            }

            segments.Add(trimmed);
        }

        if (segments.Count != 1)
        {
            error = "layers must contain exactly one layer identifier";
            return false;
        }

        if (!int.TryParse(segments[0], out layerId))
        {
            error = "layers must contain integer layer identifiers";
            return false;
        }

        return true;
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
