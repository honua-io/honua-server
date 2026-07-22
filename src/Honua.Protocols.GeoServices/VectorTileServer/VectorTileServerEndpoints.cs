// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Read-only Esri-compatible VectorTileServer surface; anonymous by design. The
// service-name route resolves the GeoServices service by NAME (not a numeric layer
// id) via ValidateServiceV2Async, mirroring MapServer. GET + POST share the metadata
// handler because Esri clients hydrate service metadata by POSTing {"f":"json"}.
//
// This is the metadata FOUNDATION (#1777): the tile / resources / tileMap routes are
// declared here but their handlers live in partial files (*.Tile.cs / *.Resources.cs /
// *.TileMap.cs) so the parallel wave (#1778 / #1779 / #1781) can fill them in WITHOUT
// editing this file. The foundation stubs them as HTTP 501.

using System.Diagnostics;
using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.VectorTileServer.Models;
using Honua.Protocols.GeoServices.VectorTileServer.Services;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Protocols.GeoServices.VectorTileServer;

/// <summary>
/// Maps GeoServices VectorTileServer REST API endpoints (Esri vector tile service adapter).
/// </summary>
internal static partial class VectorTileServerEndpoints
{
    private const string JsonContentType = "application/json";

    /// <summary>
    /// Maps VectorTileServer REST API endpoints using AOT-compatible routing. The
    /// service identifier in the route is the GeoServices service NAME, resolved through
    /// the shared <c>ValidateServiceV2Async</c> helper against the
    /// <see cref="MetadataV2ServiceProtocols.VectorTileServer"/> protocol.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to register routes on.</param>
    /// <returns>The same <paramref name="endpoints"/> instance for chaining.</returns>
    public static IEndpointRouteBuilder MapVectorTileServerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/rest/services/{serviceId}/VectorTileServer",
                static (HttpContext context, CancellationToken cancellationToken) => HandleGetServiceMetadata(context))
            .WithDisplayName("Get VectorTileServer Service Metadata")
            .WithName("GetVectorTileServerMetadata")
            .WithSummary("Get VectorTileServer service metadata")
            .WithDescription("Returns Esri-compatible VectorTileServer service metadata including the WebMercatorQuad tileInfo descriptor")
            .WithTags("VectorTileServer")
            .AllowAnonymous()
            .CacheOutput("ServiceMetadata");

        // Esri clients hydrate metadata by POSTing {"f":"json"}; mirror the GET form so
        // discovery succeeds. Anonymous by design and without the CacheOutput companion,
        // matching the MapServer metadata POST variant.
        endpoints.MapPost("/rest/services/{serviceId}/VectorTileServer",
                static (HttpContext context, CancellationToken cancellationToken) => HandleGetServiceMetadata(context))
            .WithDisplayName("Get VectorTileServer Service Metadata (POST)")
            .WithName("GetVectorTileServerMetadataPost")
            .WithSummary("Get VectorTileServer service metadata using POST")
            .WithDescription("Returns Esri-compatible VectorTileServer service metadata including the WebMercatorQuad tileInfo descriptor")
            .WithTags("VectorTileServer")
            .AllowAnonymous();

        MapTileEndpoints(endpoints);
        MapResourcesEndpoints(endpoints);
        MapTileMapEndpoints(endpoints);

        return endpoints;
    }

    /// <summary>
    /// Handle VectorTileServer service metadata requests. Resolves the service by name and
    /// emits the Esri VectorTileServer descriptor (tiles template, WebMercatorQuad tileInfo,
    /// styles / tileMap pointers, and the service extent resolved like MapServer).
    /// </summary>
    private static async Task<IResult> HandleGetServiceMetadata(HttpContext context)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        if (!TryValidateMetadataFormat(context.Request.Query, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, formatError ?? "Output format is not supported.");
        }

        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        using var scope = HonuaTelemetryScope.StartFeature(
            "metadata",
            HonuaTelemetry.Protocols.VectorTileServer,
            "*");
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId)
            .WithTag(HonuaTelemetry.Tags.Operation, "get-service-metadata");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
            var serviceResult = await ServiceResourceValidationHelpers.ValidateServiceV2Async(
                resourceValidator,
                serviceId,
                MetadataV2ServiceProtocols.VectorTileServer,
                context,
                cancellationToken: cancellationToken);
            if (!serviceResult.IsValid)
            {
                return serviceResult.ErrorResult!;
            }

            var service = serviceResult.Service!;
            var graphProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

            var publications = ResolveVectorTilePublications(snapshot, service);

            var accessError = AccessPolicyHelpers.RequireAnyResourceAccess(
                context,
                publications.Select(static publication => publication.Resource),
                service);
            if (accessError != null)
            {
                return accessError;
            }

            var visiblePublications = publications
                .Where(publication => AccessPolicyHelpers.IsResourceAccessible(context, publication.Resource, service))
                .ToArray();

            var limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value;
            var response = BuildMetadataResponse(service, visiblePublications, limitsOptions.Tiles.MaxTileZoom);

            stopwatch.Stop();
            scope.SetSuccess(visiblePublications.Length);
            scope.CategorizeLatency(stopwatch.Elapsed.TotalMilliseconds);

            return Results.Json(response, VectorTileServerJsonContext.Default.VectorTileServerMetadataResponse, contentType: JsonContentType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            scope.RecordException(ex);
            throw;
        }
    }

    private sealed record VectorTilePublicationDescriptor(
        MetadataV2Publication Publication,
        MetadataV2Resource Resource);

    private static VectorTilePublicationDescriptor[] ResolveVectorTilePublications(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service)
    {
        var descriptors = new List<VectorTilePublicationDescriptor>();
        foreach (var publication in snapshot.Index.PublicationsByService[service.Metadata.Id])
        {
            var resource = snapshot.ResolveResource(publication);
            if (resource is null)
            {
                continue;
            }

            descriptors.Add(new VectorTilePublicationDescriptor(publication, resource));
        }

        return [.. descriptors];
    }

    private static VectorTileServerMetadataResponse BuildMetadataResponse(
        MetadataV2Service service,
        IReadOnlyList<VectorTilePublicationDescriptor> publications,
        int maxTileZoom)
    {
        var tileInfo = VectorTileServerTileInfoBuilder.Build(maxTileZoom);
        var minLod = tileInfo.Lods is { Length: > 0 } lods ? lods[0].Level : 0;
        var maxLod = tileInfo.Lods is { Length: > 0 } maxLods ? maxLods[^1].Level : 0;

        var spatialReference = ResolveServiceSpatialReference(service, publications);
        var extent = ResolveServiceExtent(publications, spatialReference);

        return new VectorTileServerMetadataResponse
        {
            Name = service.Metadata.Name,
            TileInfo = tileInfo,
            MinLod = minLod,
            MaxLod = maxLod,
            FullExtent = extent,
            InitialExtent = extent
        };
    }

    private static MetadataV2SpatialReference ResolveServiceSpatialReference(
        MetadataV2Service service,
        IReadOnlyList<VectorTilePublicationDescriptor> publications)
        => service.SpatialReference
           ?? publications.Select(static publication => publication.Resource.Spatial?.SpatialReference)
               .FirstOrDefault(static spatialReference => spatialReference is not null)
           ?? MetadataV2SpatialReference.Wgs84;

    private static VectorTileExtent? ResolveServiceExtent(
        IReadOnlyList<VectorTilePublicationDescriptor> publications,
        MetadataV2SpatialReference spatialReference)
    {
        double? west = null;
        double? south = null;
        double? east = null;
        double? north = null;

        foreach (var bbox in (publications).Select(publication => publication.Resource.ReadBbox()))
        {
            if (bbox is null)
            {
                continue;
            }

            west = west.HasValue ? Math.Min(west.Value, bbox.West) : bbox.West;
            south = south.HasValue ? Math.Min(south.Value, bbox.South) : bbox.South;
            east = east.HasValue ? Math.Max(east.Value, bbox.East) : bbox.East;
            north = north.HasValue ? Math.Max(north.Value, bbox.North) : bbox.North;
        }

        if (!(west.HasValue && south.HasValue && east.HasValue && north.HasValue))
        {
            return null;
        }

        return new VectorTileExtent
        {
            Xmin = west.Value,
            Ymin = south.Value,
            Xmax = east.Value,
            Ymax = north.Value,
            SpatialReference = ToVectorTileSpatialReference(spatialReference)
        };
    }

    private static VectorTileSpatialReference ToVectorTileSpatialReference(MetadataV2SpatialReference spatialReference)
    {
        var wkid = spatialReference.ResolveSrid() ?? 4326;
        return new VectorTileSpatialReference { Wkid = wkid, LatestWkid = wkid };
    }

    private static bool TryValidateMetadataFormat(IQueryCollection query, out string? error)
    {
        error = null;
        if (!query.TryGetValue("f", out var formatValues))
        {
            return true;
        }

        var format = formatValues.ToString();
        if (string.IsNullOrWhiteSpace(format))
        {
            return true;
        }

        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(format, "pjson", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        error = $"Output format '{format}' is not supported.";
        return false;
    }
}
