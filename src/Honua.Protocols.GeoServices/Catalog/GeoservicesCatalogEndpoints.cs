// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Protocols.GeoServices.Catalog;

/// <summary>
/// GeoServices catalog endpoints for service discovery.
/// </summary>
internal static class GeoservicesCatalogEndpoints
{
    private const string JsonFormat = "json";
    private const string PrettyJsonFormat = "pjson";
    private const string JsonContentType = "application/json";
    private const string FeatureServerProtocolName = "FeatureServer";
    private const string MapServerProtocolName = "MapServer";
    private const string ImageServerProtocolName = "ImageServer";

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
        // ImageServer entries require a raster-store probe to determine availability.
        // Collect them separately so all probes can run concurrently instead of serially.
        var imageServerServices = new List<MetadataV2Service>();

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
                imageServerServices.Add(service);
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

        // Probe all ImageServer services concurrently (bounded to 4 in-flight at once)
        // rather than serially, to avoid an N+1 raster-store round-trip per service.
        if (imageServerServices.Count > 0)
        {
            var imageServerEntries = new ServiceDirectoryEntry?[imageServerServices.Count];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, imageServerServices.Count),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
                async (i, ct) =>
                {
                    var svc = imageServerServices[i];
                    try
                    {
                        var imageServerLayerId = await GetImageServerLayerIdAsync(
                            snapshot,
                            svc,
                            rasterStore,
                            ct).ConfigureAwait(false);
                        if (imageServerLayerId.HasValue)
                        {
                            // The URL uses the service NAME as the route segment (matching every
                            // other service type and the canonical ArcGIS addressing), not the
                            // numeric layer id. The probe above only decides whether to advertise.
                            var escapedImageServerName = Uri.EscapeDataString(svc.Metadata.Name);
                            imageServerEntries[i] = new ServiceDirectoryEntry
                            {
                                Name = svc.Metadata.Name,
                                Type = "ImageServer",
                                Url = $"{baseUrl}/rest/services/{escapedImageServerName}/ImageServer"
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        GeoservicesCatalogEndpointLogging.LogRasterProbeFailed(logger, svc.Metadata.Name, ex);
                    }
                }).ConfigureAwait(false);

            // Merge ImageServer entries back; re-sort by name to restore alphabetical order.
            foreach (var entry in imageServerEntries)
            {
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }

            entries.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
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
            case FeatureServerProtocolName:
                directoryType = "FeatureServer";
                return true;
            case MapServerProtocolName:
                directoryType = "MapServer";
                return true;
            case ImageServerProtocolName:
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
