// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Protocols.GeoServices;
using Honua.Infrastructure.Abstractions;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.MapServer;

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
        if (!GeoServicesRequestValueHelpers.TryValidateAllowedParameters(
                context.Request.Query,
                queryValidator,
                GeoServicesRequestValueHelpers.LayerQueryAllowedParameters,
                out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var queryHandler = context.RequestServices.GetRequiredService<IFeatureQueryDispatcher>();
        var cancellationToken = GeoServicesRequestValueHelpers.GetTimeoutAwareCancellationToken(context);
        var values = GeoServicesRequestValueHelpers.ToCaseInsensitiveDictionary(context.Request.Query);

        return await queryHandler.HandleQueryFeaturesAsync(
            serviceId,
            layerId,
            values,
            context,
            queryValidator,
            ServiceProtocols.MapServer,
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
        if (!GeoServicesRequestValueHelpers.TryValidateAllowedParameters(
                context.Request.Query,
                queryValidator,
                GeoServicesRequestValueHelpers.LayerQueryAllowedParameters,
                out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var cancellationToken = GeoServicesRequestValueHelpers.GetTimeoutAwareCancellationToken(context);
        var (values, readError) = await GeoServicesRequestValueHelpers.TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (values == null)
        {
            if (GeoServicesRequestValueHelpers.TryGetUnsupportedMediaType(readError, out var receivedContentType))
            {
                return GeoServicesRequestValueHelpers.CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
            }

            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [readError ?? "Invalid request body."]);
        }

        if (!GeoServicesRequestValueHelpers.TryValidateAllowedParameters(
                values,
                queryValidator,
                GeoServicesRequestValueHelpers.LayerQueryAllowedParameters,
                out error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var queryHandler = context.RequestServices.GetRequiredService<IFeatureQueryDispatcher>();
        var mergedValues = GeoServicesRequestValueHelpers.ToCaseInsensitiveDictionary(context.Request.Query);
        foreach (var pair in values)
        {
            mergedValues[pair.Key] = pair.Value;
        }

        return await queryHandler.HandleQueryFeaturesAsync(
            serviceId,
            layerId,
            mergedValues,
            context,
            queryValidator,
            ServiceProtocols.MapServer,
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
        if (!GeoServicesRequestValueHelpers.TryValidateAllowedParameters(
                context.Request.Query,
                queryValidator,
                GeoServicesRequestValueHelpers.ServiceQueryAllowedParameters,
                out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var values = GeoServicesRequestValueHelpers.ToCaseInsensitiveDictionary(context.Request.Query);
        if (!TryResolveServiceLayerId(values, out var layerId, out var layerError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [layerError ?? "layerId parameter is required."]);
        }

        var queryHandler = context.RequestServices.GetRequiredService<IFeatureQueryDispatcher>();
        var cancellationToken = GeoServicesRequestValueHelpers.GetTimeoutAwareCancellationToken(context);

        return await queryHandler.HandleQueryFeaturesAsync(
            serviceId,
            layerId,
            values,
            context,
            queryValidator,
            ServiceProtocols.MapServer,
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
        if (!GeoServicesRequestValueHelpers.TryValidateAllowedParameters(
                context.Request.Query,
                queryValidator,
                GeoServicesRequestValueHelpers.ServiceQueryAllowedParameters,
                out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var cancellationToken = GeoServicesRequestValueHelpers.GetTimeoutAwareCancellationToken(context);
        var (bodyValues, readError) = await GeoServicesRequestValueHelpers.TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (bodyValues == null)
        {
            if (GeoServicesRequestValueHelpers.TryGetUnsupportedMediaType(readError, out var receivedContentType))
            {
                return GeoServicesRequestValueHelpers.CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
            }

            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [readError ?? "Invalid request body."]);
        }

        if (!GeoServicesRequestValueHelpers.TryValidateAllowedParameters(
                bodyValues,
                queryValidator,
                GeoServicesRequestValueHelpers.ServiceQueryAllowedParameters,
                out error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var mergedValues = GeoServicesRequestValueHelpers.ToCaseInsensitiveDictionary(context.Request.Query);
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
            ServiceProtocols.MapServer,
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
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var serviceResult = await ServiceResourceValidationHelpers.ValidateServiceV2Async(
            resourceValidator,
            serviceId,
            ServiceProtocols.MapServer,
            context,
            cancellationToken: cancellationToken);
        return serviceResult.IsValid ? null : serviceResult.ErrorResult;
    }
}
