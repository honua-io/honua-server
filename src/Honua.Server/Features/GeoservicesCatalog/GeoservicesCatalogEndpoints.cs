// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.GeoservicesCatalog;

/// <summary>
/// GeoServices catalog endpoints for service discovery.
/// </summary>
internal static class GeoservicesCatalogEndpoints
{
    private const string JsonFormat = "json";
    private const string PrettyJsonFormat = "pjson";
    private const string JsonContentType = "application/json";

    /// <summary>
    /// Maps root catalog endpoints under /rest.
    /// </summary>
    public static IEndpointRouteBuilder MapGeoservicesCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/rest/services", HandleGetServicesDirectory)
            .WithDisplayName("GeoServices Services Directory")
            .WithName("GeoServicesServicesDirectory")
            .WithSummary("List available GeoServices endpoints")
            .WithDescription("Returns FeatureServer, MapServer, and ImageServer service directory entries.")
            .WithTags("GeoServices Catalog")
            .Produces<ServicesDirectoryResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status400BadRequest);

        endpoints.MapGet("/rest/info", HandleGetRestInfo)
            .WithDisplayName("GeoServices REST Info")
            .WithName("GeoServicesRestInfo")
            .WithSummary("Get REST root metadata")
            .WithDescription("Returns root-level GeoServices metadata.")
            .WithTags("GeoServices Catalog")
            .Produces<RestInfoResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status400BadRequest);

        return endpoints;
    }

    private static async Task<IResult> HandleGetServicesDirectory(
        HttpContext context,
        string? f,
        [FromServices] ILayerCatalog layerCatalog,
        [FromServices] IRasterStore rasterStore,
        [FromServices] ILogger<GeoservicesCatalogLog> logger)
    {
        if (!IsSupportedFormat(f))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Output format must be json or pjson.");
        }

        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var services = await layerCatalog.ListServicesAsync(cancellationToken);
        var entries = new List<ServiceDirectoryEntry>();

        foreach (var service in services.OrderBy(static s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var visibleLayers = service.Layers
                .Where(layer => AccessPolicyHelpers.IsLayerAccessible(context, layer, service))
                .ToArray();

            // Hide services when no layers are visible to the current caller.
            if (visibleLayers.Length == 0)
            {
                continue;
            }

            var escapedName = Uri.EscapeDataString(service.Name);
            entries.Add(new ServiceDirectoryEntry
            {
                Name = service.Name,
                Type = "FeatureServer",
                Url = $"{baseUrl}/rest/services/{escapedName}/FeatureServer"
            });
            entries.Add(new ServiceDirectoryEntry
            {
                Name = service.Name,
                Type = "MapServer",
                Url = $"{baseUrl}/rest/services/{escapedName}/MapServer"
            });

            try
            {
                if (await ServiceContainsRastersAsync(visibleLayers, rasterStore, cancellationToken))
                {
                    entries.Add(new ServiceDirectoryEntry
                    {
                        Name = service.Name,
                        Type = "ImageServer",
                        Url = $"{baseUrl}/rest/services/{escapedName}/ImageServer"
                    });
                }
            }
            catch (Exception ex)
            {
                GeoservicesCatalogEndpointLogging.LogRasterProbeFailed(logger, service.Name, ex);
            }
        }

        if (entries.Count == 0 && services.Length > 0)
        {
            var accessError = AccessPolicyHelpers.RequireAnyLayerAccess(
                context,
                services.SelectMany(static service => service.Layers));
            if (accessError != null)
            {
                return accessError;
            }
        }

        var response = new ServicesDirectoryResponse
        {
            Services = [.. entries]
        };

        GeoservicesCatalogEndpointLogging.LogServicesDirectoryReturned(logger, response.Services.Length);

        return Results.Json(response, GeoservicesCatalogJsonContext.Default.ServicesDirectoryResponse, contentType: JsonContentType);
    }

    private static IResult HandleGetRestInfo(HttpContext context, string? f)
    {
        if (!IsSupportedFormat(f))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Output format must be json or pjson.");
        }

        var response = new RestInfoResponse();
        return Results.Json(response, GeoservicesCatalogJsonContext.Default.RestInfoResponse, contentType: JsonContentType);
    }

    private static async Task<bool> ServiceContainsRastersAsync(
        IReadOnlyList<LayerDefinition> layers,
        IRasterStore rasterStore,
        CancellationToken cancellationToken)
    {
        foreach (var layer in layers)
        {
            var rasters = await rasterStore.ListRastersAsync(layer.Id, cancellationToken);
            if (rasters.Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSupportedFormat(string? format)
        => string.IsNullOrWhiteSpace(format) ||
           string.Equals(format, JsonFormat, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(format, PrettyJsonFormat, StringComparison.OrdinalIgnoreCase);
}

internal static partial class GeoservicesCatalogEndpointLogging
{
    [LoggerMessage(EventId = 9401, Level = LogLevel.Information,
        Message = "GeoServices services directory returned {ServiceCount} entries.")]
    public static partial void LogServicesDirectoryReturned(ILogger logger, int serviceCount);

    [LoggerMessage(EventId = 9402, Level = LogLevel.Warning,
        Message = "Failed to probe raster availability for service {ServiceName}.")]
    public static partial void LogRasterProbeFailed(ILogger logger, string serviceName, Exception exception);
}

/// <summary>
/// Logger category for GeoServices catalog endpoints.
/// </summary>
internal sealed class GeoservicesCatalogLog
{
}
