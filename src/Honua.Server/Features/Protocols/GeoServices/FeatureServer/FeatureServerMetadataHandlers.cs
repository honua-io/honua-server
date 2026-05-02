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
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Styling;
using Honua.Server.Features.Infrastructure.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer;

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
        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.ServiceMetadata, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var requestedFormat = context.Request.Query.TryGetValue("f", out var formatValue)
            ? formatValue.ToString()
            : null;
        if (!TryValidateOutputFormat(requestedFormat, JsonOnlyFormats, out _, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [formatError ?? "Output format is not supported."]);
        }

        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out string? serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        IOptions<LimitsOptions> limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>();
        ILoggerFactory loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        ILogger logger = loggerFactory.CreateLogger("Honua.Server.FeatureServerEndpoints");

        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceAsync(
            resourceValidator,
            serviceId,
            context,
            logger,
            cancellationToken);
        if (!serviceValidationResult.IsValid)
        {
            return serviceValidationResult.ErrorResult!;
        }

        var service = serviceValidationResult.Service!;
        if (RequiresDefaultMetadataAuthentication(context, service.Metadata?.AccessPolicy is not null, service.Layers.Any(layer => layer.Metadata?.AccessPolicy is not null)))
        {
            return StandardErrorHelpers.CreateUnauthorized(context, AccessPolicyHelpers.AuthRequiredMessage);
        }

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

            var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
            var supportsAttachmentUploads = HasAttachmentSurface(context.RequestServices);
            FeatureServerResponse response = MapServiceToResponse(
                service,
                limits,
                supportsGeobufOutput: featureReader is IGeobufFeatureStore,
                supportsAttachmentUploads: supportsAttachmentUploads);

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
        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.LayerMetadata, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var requestedFormat = context.Request.Query.TryGetValue("f", out var formatValue)
            ? formatValue.ToString()
            : null;
        if (!TryValidateOutputFormat(requestedFormat, JsonOnlyFormats, out _, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [formatError ?? "Output format is not supported."]);
        }

        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out string? serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var layerError = RouteValidationHelpers.ValidateLayerId(context, out int layerId);
        if (layerError is not null)
        {
            return layerError;
        }

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        IOptions<LimitsOptions> limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>();
        ILoggerFactory loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        ILogger logger = loggerFactory.CreateLogger("Honua.Server.FeatureServerEndpoints");

        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var validationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerAsync(
            resourceValidator,
            serviceId,
            layerId,
            context,
            logger,
            cancellationToken);
        if (!validationResult.IsValid)
        {
            return validationResult.ErrorResult!;
        }

        var service = validationResult.Service!;
        var layer = validationResult.Layer!;
        if (RequiresDefaultMetadataAuthentication(
                context,
                service.Metadata?.AccessPolicy is not null,
                layer.Metadata?.AccessPolicy is not null))
        {
            return StandardErrorHelpers.CreateUnauthorized(context, AccessPolicyHelpers.AuthRequiredMessage);
        }

        var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, service);
        if (accessError != null)
        {
            return accessError;
        }

        return await FetchLayerMetadataAsync(
            context,
            serviceId,
            service,
            layer,
            limitsOptions.Value.Query,
            logger,
            cancellationToken);
    }

    /// <summary>
    /// Fetches layer metadata asynchronously
    /// </summary>
    private static async Task<IResult> FetchLayerMetadataAsync(
        HttpContext context,
        string serviceId,
        ServiceDefinition service,
        LayerDefinition layer,
        QueryLimits limits,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            FeatureServerLog.LayerMetadataRequested(logger, serviceId, layer.Id);

            if (layer.Metadata?.Extrusion is { } extrusion)
            {
                var extrusionErrors = ExtrusionValidator.Validate(extrusion, layer.Fields);
                if (extrusionErrors.Count > 0)
                {
                    return StandardErrorHelpers.CreateUnprocessableEntity(
                        context,
                        "Layer extrusion configuration is invalid.",
                        [.. extrusionErrors]);
                }
            }

            var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
            var supportsAttachmentUploads = HasAttachmentSurface(context.RequestServices);
            var timeInfo = await BuildTimeInfoAsync(layer, featureReader, cancellationToken).ConfigureAwait(false);
            var drawingInfo = await LayerStyleMetadataResolver.TryGetDrawingInfoAsync(
                context.RequestServices,
                layer,
                logger,
                cancellationToken).ConfigureAwait(false);
            var extrusionInfo = BuildExtrusionInfo(layer);

            LayerResponse response = MapLayerToResponse(
                service,
                layer,
                limits,
                timeInfo,
                drawingInfo,
                extrusionInfo,
                supportsGeobufOutput: featureReader is IGeobufFeatureStore,
                supportsAttachmentUploads: supportsAttachmentUploads);

            FeatureServerLog.LayerMetadataReturned(logger, serviceId, layer.Id, layer.Name);

            return Results.Json(response, FeatureServerJsonContext.Default.LayerResponse,
                contentType: "application/json");
        }
        catch (Exception ex)
        {
            FeatureServerLog.LayerMetadataFailed(logger, serviceId, layer.Id, ex.Message, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "Layer metadata retrieval failed");
        }
    }

    private static bool RequiresDefaultMetadataAuthentication(
        HttpContext context,
        bool hasServicePolicy,
        bool hasLayerPolicy)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            return false;
        }

        var environment = context.RequestServices.GetRequiredService<IHostEnvironment>();
        if (!environment.IsProduction())
        {
            return false;
        }

        return !hasServicePolicy && !hasLayerPolicy;
    }
}
