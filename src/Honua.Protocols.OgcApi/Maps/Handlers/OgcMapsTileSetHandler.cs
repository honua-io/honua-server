// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Api.Maps.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Protocols.Ogc.Api.Maps.Handlers;

/// <summary>
/// Handler for OGC API - Maps TileSet operations.
/// Provides map tile set metadata for integration with OGC API - Tiles.
/// </summary>
internal sealed class OgcMapsTileSetHandler
{
    private const string OgcApiMapsProtocol = "OGC-API-Maps";
    private const string OgcApiTilesProtocol = "OGC-API-Tiles";

    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly ILogger<OgcMapsTileSetHandler> _logger;

    public OgcMapsTileSetHandler(
        IMetadataV2GraphProvider graphProvider,
        ILogger<OgcMapsTileSetHandler> logger)
    {
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets available map tile sets for a collection.
    /// </summary>
    public async Task<IResult> GetMapTileSetsAsync(
        int layerId,
        HttpContext? context = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "metadata",
            HonuaTelemetry.Protocols.OgcMaps,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "get-map-tile-sets");

        try
        {
            var (resource, service) = await ResolveResourceAndServiceAsync(layerId, cancellationToken).ConfigureAwait(false);
            if (resource is null)
            {
                OgcMapsLog.CollectionNotFound(_logger, layerId);
                return CreateNotFoundResult(context, $"Collection {layerId} not found");
            }

            if (!IsOgcApiMapsEnabled(service))
            {
                return CreateNotFoundResult(context, $"Collection {layerId} not found");
            }

            if (context is not null)
            {
                var accessError = AccessPolicyHelpers.RequireResourceAccess(context, resource, service);
                if (accessError != null)
                {
                    return accessError;
                }
            }

            var basePathPrefix = ResolveBasePathPrefix(context);
            var relativeTilesListPath = $"/ogc/maps/collections/{layerId}/map/tiles";
            var tilesListPath = BuildPath(basePathPrefix, relativeTilesListPath);
            var tileMatrixSetBasePath = BuildPath(basePathPrefix, "/ogc/tiles/tileMatrixSets");
            var includeOgcTilesLinks = IsOgcApiTilesEnabled(service);
            var tileSets = BuildTileSets(layerId, resource.Metadata.Name, basePathPrefix, tileMatrixSetBasePath, includeOgcTilesLinks);

            var response = new TileSetsList
            {
                Tilesets = tileSets,
                Links =
                [
                    new OgcLink
                    {
                        Href = tilesListPath,
                        Rel = "self",
                        Type = MediaTypes.Json,
                        Title = "This tileset list"
                    },
                    new OgcLink
                    {
                        Href = BuildPath(basePathPrefix, $"/ogc/maps/collections/{layerId}/map"),
                        Rel = "parent",
                        Type = MediaTypes.Png,
                        Title = "Collection map"
                    }
                ]
            };

            OgcMapsLog.TileSetsRetrieved(_logger, layerId, response.Tilesets.Length);
            scope.SetSuccess(response.Tilesets.Length);

            return Results.Ok(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OgcMapsLog.TileSetRetrievalFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return CreateErrorResult(context, "An error occurred while retrieving map tile sets.");
        }
    }

    /// <summary>
    /// Gets map tile set metadata for a single tile matrix set.
    /// </summary>
    public async Task<IResult> GetMapTileSetAsync(
        int layerId,
        string tileMatrixSetId,
        HttpContext? context = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "metadata",
            HonuaTelemetry.Protocols.OgcMaps,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "get-map-tile-set")
             .WithTag("honua.tile_matrix_set_id", tileMatrixSetId);

        try
        {
            var (resource, service) = await ResolveResourceAndServiceAsync(layerId, cancellationToken).ConfigureAwait(false);
            if (resource is null)
            {
                OgcMapsLog.CollectionNotFound(_logger, layerId);
                return CreateNotFoundResult(context, $"Collection {layerId} not found");
            }

            if (!IsOgcApiMapsEnabled(service))
            {
                return CreateNotFoundResult(context, $"Collection {layerId} not found");
            }

            if (context is not null)
            {
                var accessError = AccessPolicyHelpers.RequireResourceAccess(context, resource, service);
                if (accessError != null)
                {
                    return accessError;
                }
            }

            var basePathPrefix = ResolveBasePathPrefix(context);
            var tileMatrixSetBasePath = BuildPath(basePathPrefix, "/ogc/tiles/tileMatrixSets");
            var includeOgcTilesLinks = IsOgcApiTilesEnabled(service);
            var tileSet = BuildTileSets(layerId, resource.Metadata.Name, basePathPrefix, tileMatrixSetBasePath, includeOgcTilesLinks)
                .FirstOrDefault(candidate => string.Equals(
                    candidate.TileMatrixSetId,
                    tileMatrixSetId,
                    StringComparison.OrdinalIgnoreCase));

            if (tileSet == null)
            {
                return CreateNotFoundResult(context, $"Tile matrix set '{tileMatrixSetId}' not found");
            }

            scope.SetSuccess(1);
            return Results.Ok(tileSet);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OgcMapsLog.TileSetRetrievalFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return CreateErrorResult(context, "An error occurred while retrieving map tile set metadata.");
        }
    }

    private static IResult CreateNotFoundResult(HttpContext? context, string message)
        => context is not null
            ? StandardErrorHelpers.CreateNotFound(context, message)
            : Results.NotFound();

    private static IResult CreateErrorResult(HttpContext? context, string message)
        => context is not null
            ? StandardErrorHelpers.CreateInternalServerError(context, message)
            : Results.Problem(message, statusCode: 500);

    private static TileSet[] BuildTileSets(
        int layerId,
        string? layerName,
        string basePathPrefix,
        string tileMatrixSetBasePath,
        bool includeOgcTilesLinks)
    {
        var displayName = string.IsNullOrWhiteSpace(layerName)
            ? $"Layer {layerId}"
            : layerName;

        return OgcTileMatrixSetDescriptors.Supported
            .Select(descriptor => BuildTileSet(
                layerId,
                displayName,
                basePathPrefix,
                tileMatrixSetBasePath,
                descriptor,
                includeOgcTilesLinks))
            .ToArray();
    }

    private static TileSet BuildTileSet(
        int layerId,
        string displayName,
        string basePathPrefix,
        string tileMatrixSetBasePath,
        OgcTileMatrixSetDescriptor descriptor,
        bool includeOgcTilesLinks)
    {
        var tileSetPath = BuildPath(
            basePathPrefix,
            $"/ogc/maps/collections/{layerId}/map/tiles/{descriptor.Id}");
        var tileTemplate = BuildPath(
            basePathPrefix,
            $"/ogc/tiles/collections/{layerId}/tiles/{descriptor.Id}/{{tileMatrix}}/{{tileRow}}/{{tileCol}}?f=png");
        var links = new List<OgcLink>
        {
            new()
            {
                Href = tileSetPath,
                Rel = "self",
                Type = MediaTypes.Json,
                Title = "This tileset"
            }
        };

        if (includeOgcTilesLinks)
        {
            links.Add(new OgcLink
            {
                Href = tileTemplate,
                Rel = "item",
                Type = MediaTypes.Png,
                Title = "PNG map tiles",
                Templated = true
            });
            links.Add(new OgcLink
            {
                Href = $"{tileMatrixSetBasePath}/{descriptor.Id}",
                Rel = "http://www.opengis.net/def/rel/ogc/1.0/tiling-scheme",
                Type = MediaTypes.Json,
                Title = $"{descriptor.ProjectionName} tile matrix set definition"
            });
        }

        return new TileSet
        {
            Title = $"Map tiles for {displayName} in {descriptor.ProjectionName}",
            Description = $"Map tiles generated from {displayName} using {descriptor.ProjectionName} projection",
            Crs = descriptor.Crs,
            TileMatrixSetId = descriptor.Id,
            TileMatrixSetUri = descriptor.Uri,
            Links = [.. links]
        };
    }

    private static string ResolveBasePathPrefix(HttpContext? context)
    {
        return context is null ? string.Empty : BaseUrlResolver.GetBaseUrl(context);
    }

    private static string BuildPath(string basePathPrefix, string relativePath)
        => string.IsNullOrWhiteSpace(basePathPrefix)
            ? relativePath
            : $"{basePathPrefix}{relativePath}";

    private static bool IsOgcApiMapsEnabled(MetadataV2Service? service)
        => IsProtocolEnabled(service, OgcApiMapsProtocol);

    private static bool IsOgcApiTilesEnabled(MetadataV2Service? service)
        => IsProtocolEnabled(service, OgcApiTilesProtocol);

    private static bool IsProtocolEnabled(MetadataV2Service? service, string protocol)
        => service?.Protocols.Any(enabled => string.Equals(enabled, protocol, StringComparison.OrdinalIgnoreCase)) == true;

    private async Task<(MetadataV2Resource? Resource, MetadataV2Service? Service)> ResolveResourceAndServiceAsync(
        int storageLayerId,
        CancellationToken cancellationToken)
    {
        var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Index.ResourcesByStorageLayerId.TryGetValue(storageLayerId, out var resource))
        {
            return (null, null);
        }

        // Pick the OGC-API-Maps publication for this resource if one exists; otherwise null.
        MetadataV2Service? service = null;
        foreach (var publication in snapshot.Index.PublicationsByResource[resource.Metadata.Id])
        {
            if (!snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var candidate))
            {
                continue;
            }
            if (IsProtocolEnabled(candidate, OgcApiMapsProtocol))
            {
                service = candidate;
                break;
            }
        }

        return (resource, service);
    }
}
