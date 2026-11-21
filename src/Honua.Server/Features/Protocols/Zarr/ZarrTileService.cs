// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Features.Protocols.Zarr;

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

internal sealed class ZarrTileService : IZarrTileService
{
    private const long MaxTileSliceBytes = 4L * 1024L * 1024L;

    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly ILayerAccessAuthorizer _layerAccessAuthorizer;
    private readonly IZarrStore _store;
    private readonly ZarrSubsetReader _subsetReader;
    private readonly IEnumerable<ICloudRangeReader> _rangeReaders;
    private readonly ITileMatrixSetRegistry _tileMatrixSets;
    private readonly ILogger<ZarrEndpointsLog> _logger;

    public ZarrTileService(
        IMetadataV2GraphProvider graphProvider,
        ILayerAccessAuthorizer layerAccessAuthorizer,
        IZarrStore store,
        ZarrSubsetReader subsetReader,
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

        var denied = await AuthorizeLayerIndexAsync(context, layerId, cancellationToken).ConfigureAwait(false);
        if (denied is not null)
        {
            return denied;
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

        // Cross-CRS reprojection of the tile window is deferred.
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

        // Preserve the source dtype until nodata comparison; widening int64/uint64
        // sentinels first can collapse an adjacent real sample onto the same double.
        var png = ZarrTileRenderer.RenderWithTypedFill(result, slice, ZarrTileRenderer.DefaultTileSize,
            colormap: null, fillValue: slice.Plan.Array.FillValue);
        ZarrLog.DatacubeTileRendered(_logger, layerId, result.Variable, z, x, y, png.Length);
        return Results.Bytes(png, "image/png");
    }

    // The route id is a service-local publication index that several services can share,
    // and Zarr registrations are keyed by that index alone. Every routable publication with
    // the index must allow the caller, so a public layer cannot vouch for a private one.
    private async Task<IResult?> AuthorizeLayerIndexAsync(
        HttpContext context,
        int layerId,
        CancellationToken cancellationToken)
    {
        var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var publications = snapshot.Graph.Publications
            .Where(candidate => candidate.LayerIndex == layerId && snapshot.IsRoutable(candidate))
            .ToList();
        if (publications.Count == 0)
        {
            return StandardErrorHelpers.CreateForbidden(context, "Access to this resource is forbidden.");
        }

        // Collect every decision so an anonymous caller is challenged (401) whenever any
        // publication would admit them after signing in, rather than seeing graph-order 403s.
        AccessDecision? denial = null;
        foreach (var publication in publications)
        {
            var resource = snapshot.ResolveResource(publication);
            var access = resource is null
                ? AccessDecision.Forbidden()
                : await _layerAccessAuthorizer
                    .AuthorizePublicationAsync(
                        context.User,
                        publication,
                        resource,
                        snapshot.Index.ServicesById.GetValueOrDefault(publication.ServiceId),
                        AuthorizationOperation.Query,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (!access.IsAllowed && denial is not { RequiresAuthentication: true })
            {
                denial = access;
            }
        }

        return denial is { } denied ? CreateDeniedResult(context, denied) : null;
    }

    private static IResult CreateDeniedResult(HttpContext context, AccessDecision access)
    {
        if (!access.RequiresAuthentication)
        {
            return StandardErrorHelpers.CreateForbidden(context, "Access to this resource is forbidden.");
        }

        AccessPolicyHelpers.AppendAuthenticationChallenge(context);
        return StandardErrorHelpers.CreateUnauthorized(context, "Authentication is required to access this resource.");
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
