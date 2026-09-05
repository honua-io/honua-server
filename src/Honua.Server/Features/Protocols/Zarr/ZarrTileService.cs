// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Features.Protocols.Zarr;

/// <summary>
/// Focused service for the datacube tile request path. Keeping publication resolution,
/// authorization, storage access, and rendering behind this seam prevents the endpoint
/// handler from becoming an unreviewable dependency container.
/// </summary>
internal interface IZarrTileService
{
    Task<IResult> HandleAsync(
        HttpContext context,
        int layerId,
        string tileMatrixSetId,
        int z,
        int x,
        int y,
        CancellationToken cancellationToken);
}

/// <inheritdoc />
internal sealed class ZarrTileService : IZarrTileService
{
    private const long MaxTileSliceBytes = 4L * 1024L * 1024L;

    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly ILayerAccessAuthorizer _layerAccessAuthorizer;
    private readonly IZarrStore _store;
    private readonly IZarrSubsetReader _subsetReader;
    private readonly IEnumerable<ICloudRangeReader> _rangeReaders;
    private readonly ITileMatrixSetRegistry _tileMatrixSets;
    private readonly ILogger<ZarrEndpointsLog> _logger;

    public ZarrTileService(
        IMetadataV2GraphProvider graphProvider,
        ILayerAccessAuthorizer layerAccessAuthorizer,
        IZarrStore store,
        IZarrSubsetReader subsetReader,
        IEnumerable<ICloudRangeReader> rangeReaders,
        ITileMatrixSetRegistry tileMatrixSets,
        ILogger<ZarrEndpointsLog> logger)
    {
        _graphProvider = graphProvider;
        _layerAccessAuthorizer = layerAccessAuthorizer;
        _store = store;
        _subsetReader = subsetReader;
        _rangeReaders = rangeReaders;
        _tileMatrixSets = tileMatrixSets;
        _logger = logger;
    }

    public async Task<IResult> HandleAsync(
        HttpContext context,
        int layerId,
        string tileMatrixSetId,
        int z,
        int x,
        int y,
        CancellationToken cancellationToken)
    {
        if (x < 0 || y < 0 || z < 0)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Tile coordinates must be non-negative.");
        }

        // The route id is a service-local publication index, not a storage-layer id.
        // Resolve that publication first so authorization evaluates the exact resource
        // and service policy that published the Zarr registration.
        var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var publication = snapshot.Graph.Publications
            .Where(candidate => candidate.LayerIndex == layerId && snapshot.IsRoutable(candidate))
            .OrderByDescending(candidate => candidate.IsPrimary)
            .ThenBy(candidate => candidate.Metadata.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        var resource = publication is null ? null : snapshot.ResolveResource(publication);
        var service = publication is not null
            ? snapshot.Index.ServicesById.GetValueOrDefault(publication.ServiceId)
            : null;

        if (publication is null || resource is null)
        {
            return StandardErrorHelpers.CreateForbidden(context, "Access to this resource is forbidden.");
        }

        var access = await _layerAccessAuthorizer
            .AuthorizePublicationAsync(
                context.User,
                publication,
                resource,
                service,
                AuthorizationOperation.Query,
                cancellationToken)
            .ConfigureAwait(false);
        if (!access.IsAllowed)
        {
            if (access.RequiresAuthentication)
            {
                context.Response.Headers.Append("WWW-Authenticate", "Bearer");
                return StandardErrorHelpers.CreateUnauthorized(
                    context, "Authentication is required to access this resource.");
            }

            return StandardErrorHelpers.CreateForbidden(
                context, "Access to this resource is forbidden.");
        }

        var registrations = await _store.ListByLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        var servable = registrations.FirstOrDefault(candidate => candidate.Metadata is not null);
        if (servable is null || servable.Metadata is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, "No servable Zarr coverage is registered for this layer.");
        }

        var metadata = servable.Metadata;
        if (!_tileMatrixSets.TryGetGeometry(tileMatrixSetId, z, out var geometry))
        {
            return StandardErrorHelpers.CreateBadRequest(context, $"Tile matrix set '{tileMatrixSetId}' is not supported.");
        }

        // Cross-CRS reprojection of the tile window is deferred (#1835 follow-up).
        if (geometry.Srid != metadata.Srid)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Tile matrix set '{tileMatrixSetId}' (EPSG:{geometry.Srid.ToString(CultureInfo.InvariantCulture)}) does not match the coverage CRS (EPSG:{metadata.Srid.ToString(CultureInfo.InvariantCulture)}). Request a matching gridset.");
        }

        if (geometry.GetTileBounds(x, y, z) is not { } bounds)
        {
            return StandardErrorHelpers.CreateBadRequest(context, $"Tile level {z.ToString(CultureInfo.InvariantCulture)} is not part of '{tileMatrixSetId}'.");
        }

        var variable = GetQueryValue(context, "variable");
        var datetimeRaw = GetQueryValue(context, "datetime");
        (DateTimeOffset? Start, DateTimeOffset? End)? datetime = null;
        if (!string.IsNullOrWhiteSpace(datetimeRaw))
        {
            if (!Iso8601TemporalIntervalParser.TryParseRange(datetimeRaw, out var start, out var end, out var dtError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, dtError ?? "Invalid datetime parameter.");
            }

            datetime = (start, end);
        }

        int? verticalIndex = null;
        var elevationRaw = GetQueryValue(context, "elevation");
        if (!string.IsNullOrWhiteSpace(elevationRaw))
        {
            if (!int.TryParse(elevationRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
            {
                return StandardErrorHelpers.CreateBadRequest(context, "The elevation parameter must be a non-negative grid index.");
            }

            verticalIndex = parsed;
        }

        var tileBounds = new ZarrTileBounds(bounds.XMin, bounds.YMin, bounds.XMax, bounds.YMax);
        if (!ZarrTileSlicePlanner.TryPlan(metadata, variable, tileBounds, datetime, verticalIndex, MaxTileSliceBytes, out var slice, out var planError))
        {
            if (planError is { } message && message.Contains("does not intersect", StringComparison.Ordinal))
            {
                return Results.NoContent();
            }

            return StandardErrorHelpers.CreateBadRequest(context, planError ?? "The tile could not be resolved against the coverage.");
        }

        var rangeReader = _rangeReaders.FirstOrDefault(reader => reader.Provider == servable.Provider);
        if (rangeReader is null)
        {
            return StandardErrorHelpers.CreateInternalServerError(context, "The storage backend for this coverage is not configured.");
        }

        ZarrSubsetResult result;
        try
        {
            result = await _subsetReader.ReadSubsetAsync(
                    rangeReader,
                    servable.Bucket,
                    servable.RootPath,
                    metadata,
                    slice!.Plan.Request,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return StandardErrorHelpers.CreateBadRequest(context, ex.Message);
        }
        catch (InvalidDataException)
        {
            return StandardErrorHelpers.CreateInternalServerError(context, "The Zarr store returned invalid data for this tile.");
        }

        var fillValue = slice.Plan.Array.FillValue as double?;
        var png = ZarrTileRenderer.Render(result, slice, ZarrTileRenderer.DefaultTileSize, colormap: null, fillValue: fillValue);
        ZarrLog.DatacubeTileRendered(_logger, layerId, result.Variable, z, x, y, png.Length);
        return Results.Bytes(png, "image/png");
    }

    private static string? GetQueryValue(HttpContext context, string key)
    {
        if (!context.Request.Query.TryGetValue(key, out var values))
        {
            return null;
        }

        var value = values.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
