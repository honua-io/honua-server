// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Tiles;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.FeatureServer;

/// <summary>
/// Metadata handlers for FeatureServer endpoints
/// </summary>
internal static partial class FeatureServerEndpoints
{
    /// <summary>
    /// Handle service metadata requests
    /// </summary>
    private static async Task<IResult> HandleGetServiceMetadata(HttpContext context)
    {
        if (!string.Equals(context.Request.Method, HttpMethods.Get, StringComparison.OrdinalIgnoreCase))
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);

        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.ServiceMetadata, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        if (!RouteValidationHelpers.TryValidateServiceId(context, out string? serviceId))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Service ID is required");
        }

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        IOptions<LimitsOptions> limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>();
        ILoggerFactory loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        ILogger logger = loggerFactory.CreateLogger("Honua.Server.FeatureServerEndpoints");

        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceResult = await resourceValidator.ValidateServiceAsync(serviceId, cancellationToken);
        if (!serviceResult.IsValid)
        {
            var errorMessage = serviceResult.ErrorMessage ?? "Service not found.";
            if (serviceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
            {
                return StandardErrorHelpers.CreateBadRequest(context, errorMessage);
            }

            FeatureServerLog.ServiceNotFound(logger, serviceId);
            return StandardErrorHelpers.CreateNotFound(context, errorMessage);
        }

        var service = serviceResult.Resource!;
        var accessError = AccessPolicyHelpers.RequireAnyLayerAccess(context, service.Layers, service);
        if (accessError != null)
        {
            return accessError;
        }

        var visibleLayers = service.Layers
            .Where(layer => AccessPolicyHelpers.IsLayerAccessible(context, layer, service))
            .ToArray();

        var filteredService = service with { Layers = visibleLayers };

        return await GetServiceMetadataAsync(
            context,
            filteredService,
            limitsOptions.Value.Query,
            logger);
    }

    /// <summary>
    /// Gets service metadata asynchronously
    /// </summary>
    private static Task<IResult> GetServiceMetadataAsync(
        HttpContext context,
        ServiceDefinition service,
        QueryLimits limits,
        ILogger logger)
    {
        try
        {
            FeatureServerLog.ServiceMetadataRequested(logger, service.Name);

            FeatureServerResponse response = MapServiceToResponse(service, limits);

            FeatureServerLog.ServiceMetadataReturned(logger, service.Name, response.Layers.Length);

            return Task.FromResult(Results.Json(response, FeatureServerJsonContext.Default.FeatureServerResponse,
                contentType: "application/json"));
        }
        catch (Exception ex)
        {
            FeatureServerLog.ServiceMetadataFailed(logger, service.Name, ex.Message, ex);
            return Task.FromResult(StandardErrorHelpers.CreateInternalServerError(
                context,
                "Service metadata retrieval failed"));
        }
    }

    /// <summary>
    /// Handle layer metadata requests
    /// </summary>
    private static async Task<IResult> HandleLayerMetadata(HttpContext context)
    {
        if (!string.Equals(context.Request.Method, HttpMethods.Get, StringComparison.OrdinalIgnoreCase))
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);

        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.LayerMetadata, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        if (!RouteValidationHelpers.TryValidateServiceId(context, out string? serviceId))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Service ID is required");
        }

        if (!RouteValidationHelpers.TryValidateLayerId(context, out int layerId))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Layer ID is required");
        }

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        IOptions<LimitsOptions> limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>();
        ILoggerFactory loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        ILogger logger = loggerFactory.CreateLogger("Honua.Server.FeatureServerEndpoints");

        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var resourceResult = await resourceValidator.ValidateServiceLayerAsync(serviceId, layerId, cancellationToken);
        if (!resourceResult.IsValid)
        {
            var errorMessage = resourceResult.ErrorMessage ?? "Resource not found.";

            if (resourceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
            {
                return StandardErrorHelpers.CreateBadRequest(context, errorMessage);
            }

            if (errorMessage.StartsWith("Service", StringComparison.OrdinalIgnoreCase))
            {
                FeatureServerLog.ServiceNotFound(logger, serviceId);
            }
            else if (errorMessage.StartsWith("Layer", StringComparison.OrdinalIgnoreCase))
            {
                FeatureServerLog.LayerNotFound(logger, serviceId, layerId);
            }

            return StandardErrorHelpers.CreateNotFound(context, errorMessage);
        }

        var service = resourceResult.Resource!.Service;
        var layer = resourceResult.Resource.Layer;
        var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, service);
        if (accessError != null)
        {
            return accessError;
        }

        return await FetchLayerMetadataAsync(
            context,
            serviceId,
            layer,
            limitsOptions.Value.Query,
            logger);
    }

    /// <summary>
    /// Fetches layer metadata asynchronously
    /// </summary>
    private static Task<IResult> FetchLayerMetadataAsync(
        HttpContext context,
        string serviceId,
        LayerDefinition layer,
        QueryLimits limits,
        ILogger logger)
    {
        try
        {
            FeatureServerLog.LayerMetadataRequested(logger, serviceId, layer.Id);

            LayerResponse response = MapLayerToResponse(layer, limits);

            FeatureServerLog.LayerMetadataReturned(logger, serviceId, layer.Id, layer.Name);

            return Task.FromResult(Results.Json(response, FeatureServerJsonContext.Default.LayerResponse,
                contentType: "application/json"));
        }
        catch (Exception ex)
        {
            FeatureServerLog.LayerMetadataFailed(logger, serviceId, layer.Id, ex.Message, ex);
            return Task.FromResult(StandardErrorHelpers.CreateInternalServerError(
                context,
                "Layer metadata retrieval failed"));
        }
    }
}
