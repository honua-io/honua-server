// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.MapServer.Models;

namespace Honua.Server.Features.MapServer;

internal static partial class MapServerEndpoints
{
    /// <summary>
    /// Handle MapServer service metadata requests.
    /// </summary>
    private static async Task<IResult> HandleGetServiceMetadata(HttpContext context)
    {
        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Honua.Server.MapServerEndpoints");

        var serviceResult = await resourceValidator.ValidateServiceAsync(serviceId, context.RequestAborted);
        if (!serviceResult.IsValid)
        {
            var errorMessage = serviceResult.ErrorMessage ?? "Service not found.";
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

        var response = MapServiceToMapServerResponse(service with { Layers = visibleLayers });
        return Results.Json(response, MapServerJsonContext.Default.MapServerResponse, contentType: "application/json");
    }

    private static MapServerResponse MapServiceToMapServerResponse(ServiceDefinition service)
    {
        return new MapServerResponse
        {
            MapName = service.Name,
            Description = service.Description,
            SpatialReference = EsriSpatialReference.FromSpatialReference(service.SpatialReference),
            Layers = [.. service.Layers.Select(l => new MapServerLayerInfo
            {
                Id = l.Id,
                Name = l.Name,
                DefaultVisibility = l.DefaultVisibility,
                MinScale = l.MinScale,
                MaxScale = l.MaxScale
            })],
            SupportedImageFormatTypes = "PNG,PNG8,PNG24,PNG32,JPG,GIF",
            Capabilities = "Map,Query,Data",
            FullExtent = service.EffectiveExtent.HasValue
                ? EsriExtent.FromFeatureExtent(service.EffectiveExtent.Value)
                : null,
            InitialExtent = service.EffectiveExtent.HasValue
                ? EsriExtent.FromFeatureExtent(service.EffectiveExtent.Value)
                : null
        };
    }
}
