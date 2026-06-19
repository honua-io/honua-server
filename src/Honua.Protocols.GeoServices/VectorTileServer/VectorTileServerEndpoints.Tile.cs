// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// VectorTileServer vector tile data route (honua-server#1778). Implemented WITHOUT editing
// the shared VectorTileServerEndpoints.cs: the route is declared here in this partial file.
//
// The handler is a thin protocol adapter over the shared vector-tile pipeline. It resolves
// the GeoServices service by NAME (ValidateServiceV2Async, mirroring the foundation metadata
// handler), resolves the service's primary tiled publication -> storage layer id, then
// delegates to Honua.Infrastructure.Rendering.VectorTileExecution / ITileProvider.GetMvtTileAsync
// (the same canonical path FeatureServer's /tiles/{layerId}/{z}/{x}/{y}.mvt uses). No new
// data-access path is introduced.
//
// Esri addresses tiles as {z}/{y}/{x} over the WebMercatorQuad matrix set (top-left origin),
// which is the same origin as the canonical (x, y, z) tuple, so the mapping is direct
// (z -> zoomLevel, x -> tileCol, y -> tileRow).

using System.Diagnostics;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Rendering;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Protocols.GeoServices.VectorTileServer;

internal static partial class VectorTileServerEndpoints
{
    private const string VectorTileServerProtocolName = "VectorTileServer";
    private const string MvtContentType = "application/vnd.mapbox-vector-tile";

    /// <summary>
    /// Maps the VectorTileServer vector tile data route (<c>tile/{z}/{y}/{x}.pbf</c>).
    /// </summary>
    private static void MapTileEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/rest/services/{serviceId}/VectorTileServer/tile/{z:int}/{y:int}/{x:int}.pbf",
                static (HttpContext context, int z, int y, int x, CancellationToken cancellationToken)
                    => HandleGetTile(context, z, y, x, cancellationToken))
            .WithDisplayName("Get Vector Tile")
            .WithName("GetVectorTile")
            .WithSummary("Get a Mapbox Vector Tile (.pbf) from a VectorTileServer service")
            .WithDescription("Returns a protobuf-encoded Mapbox Vector Tile for the service's primary tiled layer over the WebMercatorQuad tile matrix set.")
            .WithTags("VectorTileServer")
            .AllowAnonymous()
            .CacheOutput("MvtTile")
            .Produces(200, contentType: MvtContentType)
            .Produces(204)
            .Produces(400)
            .Produces(404);
    }

    /// <summary>
    /// Handle a VectorTileServer tile request. Resolves the service by name, resolves its
    /// primary tiled publication to a storage layer id, and renders the tile through the
    /// shared vector-tile execution path. Esri <c>{z}/{y}/{x}</c> maps directly onto the
    /// canonical <c>(x, y, z)</c> tuple (shared top-left WebMercatorQuad origin).
    /// </summary>
    private static async Task<IResult> HandleGetTile(HttpContext context, int z, int y, int x, CancellationToken cancellationToken)
    {
        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var tileOptions = context.RequestServices.GetRequiredService<IOptions<TileOptions>>().Value;
        var tileLimits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.Tiles;

        // Out-of-range zoom (LimitsOptions.Tiles) and bad coordinates -> 400, before any
        // metadata/data resolution, mirroring FeatureServer's HandleLayerTile.
        if (z < tileLimits.MinTileZoom || z > tileLimits.MaxTileZoom)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                $"Zoom level {z} is outside supported range",
                [$"Supported zoom range is {tileLimits.MinTileZoom}-{tileLimits.MaxTileZoom}"]);
        }

        if (!TileMath.ValidateTileCoordinates(x, y, z))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                $"Invalid tile coordinates: x={x}, y={y}, z={z}");
        }

        using var scope = HonuaTelemetryScope.StartFeature(
            "tile",
            HonuaTelemetry.Protocols.VectorTileServer,
            "*");
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId)
            .WithTag(HonuaTelemetry.Tags.Operation, "get-tile");
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

            var primary = ResolvePrimaryTiledPublication(snapshot, service);
            if (primary is null)
            {
                return StandardErrorHelpers.CreateNotFound(context,
                    $"Service '{service.Metadata.Name}' has no tiled layer to render.");
            }

            var (publication, resource) = primary.Value;

            var accessError = AccessPolicyHelpers.RequireAnyResourceAccess(context, [resource], service);
            if (accessError != null)
            {
                return accessError;
            }

            // Mirror the V2 metadata builders' resolution order: integer storage handle is
            // publication.LayerIndex when the graph carries no explicit storage binding.
            var storageLayerId = publication.LayerIndex ?? snapshot.ResolveStorageLayerId(publication);
            if (storageLayerId is null)
            {
                return StandardErrorHelpers.CreateNotFound(context,
                    $"Layer '{resource.Metadata.Name}' is not bound to a storage layer.");
            }

            var query = VectorTileExecution.CreateQuery(
                resource.ReadSrid() ?? SpatialReference.WGS84.Wkid);

            var tileProvider = context.RequestServices.GetRequiredService<ITileProvider>();

            // Esri tile/{z}/{y}/{x} -> canonical (tileCol=x, tileRow=y, zoom=z); same top-left origin.
            var result = await VectorTileExecution.ExecuteAsync(
                context,
                tileProvider,
                storageLayerId.Value,
                x,
                y,
                z,
                query,
                tileOptions,
                tileLimits,
                cancellationToken);

            stopwatch.Stop();
            scope.SetSuccess();
            scope.CategorizeLatency(stopwatch.Elapsed.TotalMilliseconds);
            return result;
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

    /// <summary>
    /// Resolves the service's primary tiled publication. Prefers an
    /// <see cref="MetadataV2PublicationType.EsriVectorTileLayer"/> publication (the type the
    /// VectorTileServer adapter advertises); falls back to the first resolvable publication so
    /// services that publish their tiled layer under a different publication type still render.
    /// </summary>
    private static (MetadataV2Publication Publication, MetadataV2Resource Resource)? ResolvePrimaryTiledPublication(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service)
    {
        (MetadataV2Publication Publication, MetadataV2Resource Resource)? fallback = null;
        foreach (var publication in snapshot.Index.PublicationsByService[service.Metadata.Id])
        {
            var resource = snapshot.ResolveResource(publication);
            if (resource is null)
            {
                continue;
            }

            if (publication.PublicationType == MetadataV2PublicationType.EsriVectorTileLayer)
            {
                return (publication, resource);
            }

            fallback ??= (publication, resource);
        }

        return fallback;
    }
}
