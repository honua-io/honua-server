// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Protocols.GeoServices.Catalog;

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
            .CacheOutput("ServiceDirectory")
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
        [FromServices] IMetadataV2GraphProvider graphProvider,
        [FromServices] IRasterStore rasterStore,
        [FromServices] ILogger<GeoservicesCatalogLog> logger)
    {
        if (!IsSupportedFormat(f))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Output format must be json or pjson.");
        }

        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        var entries = new List<ServiceDirectoryEntry>();
        var deniedPublications = new List<MetadataV2Resource>();

        foreach (var service in snapshot.Graph.Services.OrderBy(static s => s.Metadata.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!TryMapServiceType(service.PrimaryProtocol, out var directoryType))
            {
                continue;
            }

            // Project publications -> resources, filtering by access.
            var visibleResources = new List<MetadataV2Resource>();
            foreach (var publication in snapshot.PublicationsForService(service.Metadata.Id))
            {
                var resource = snapshot.ResolveResource(publication);
                if (resource is null)
                {
                    continue;
                }

                if (AccessPolicyHelpers.IsResourceAccessible(context, resource, service))
                {
                    visibleResources.Add(resource);
                }
                else
                {
                    deniedPublications.Add(resource);
                }
            }

            if (visibleResources.Count == 0)
            {
                continue;
            }

            if (string.Equals(directoryType, "ImageServer", StringComparison.Ordinal))
            {
                // ImageServer entries use the layer id in the URL (not the service name);
                // probe the raster store to find the first publication that actually has
                // raster data registered, mirroring the v1 behaviour.
                try
                {
                    var imageServerLayerId = await GetImageServerLayerIdAsync(
                        snapshot,
                        service,
                        rasterStore,
                        cancellationToken).ConfigureAwait(false);
                    if (imageServerLayerId.HasValue)
                    {
                        entries.Add(new ServiceDirectoryEntry
                        {
                            Name = service.Metadata.Name,
                            Type = "ImageServer",
                            Url = $"{baseUrl}/rest/services/{imageServerLayerId.Value}/ImageServer"
                        });
                    }
                }
                catch (Exception ex)
                {
                    GeoservicesCatalogEndpointLogging.LogRasterProbeFailed(logger, service.Metadata.Name, ex);
                }
            }
            else
            {
                var escapedName = Uri.EscapeDataString(service.Metadata.Name);
                entries.Add(new ServiceDirectoryEntry
                {
                    Name = service.Metadata.Name,
                    Type = directoryType,
                    Url = $"{baseUrl}/rest/services/{escapedName}/{directoryType}"
                });
            }
        }

        // If nothing was emitted but there were publications the caller could not see,
        // surface the standard 401/403 access decision instead of an empty directory.
        if (entries.Count == 0 && deniedPublications.Count > 0)
        {
            var accessError = AccessPolicyHelpers.RequireAnyResourceAccess(context, deniedPublications);
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

    /// <summary>
    /// Maps an Esri-family primary protocol to the directory-entry "type" string the
    /// GeoServices REST catalog exposes. Returns false for non-Esri protocols
    /// (OGC API Features, STAC, etc.) which are surfaced through other catalogs.
    /// </summary>
    private static bool TryMapServiceType(string? primaryProtocol, out string directoryType)
    {
        switch (primaryProtocol)
        {
            case ServiceProtocols.FeatureServer:
                directoryType = "FeatureServer";
                return true;
            case ServiceProtocols.MapServer:
                directoryType = "MapServer";
                return true;
            case ServiceProtocols.ImageServer:
                directoryType = "ImageServer";
                return true;
            default:
                directoryType = string.Empty;
                return false;
        }
    }

    /// <summary>
    /// Finds the first publication on the given image service whose layer index
    /// has at least one raster registered in the raster store. The catalog uses
    /// the layer index (not the service name) as the route segment for
    /// ImageServer entries.
    /// </summary>
    private static async Task<int?> GetImageServerLayerIdAsync(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service,
        IRasterStore rasterStore,
        CancellationToken cancellationToken)
    {
        foreach (var publication in snapshot.PublicationsForService(service.Metadata.Id))
        {
            if (publication.LayerIndex is not { } layerIndex)
            {
                continue;
            }

            var rasters = await rasterStore.ListRastersAsync(layerIndex, cancellationToken).ConfigureAwait(false);
            if (rasters.Length > 0)
            {
                return layerIndex;
            }
        }

        return null;
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
