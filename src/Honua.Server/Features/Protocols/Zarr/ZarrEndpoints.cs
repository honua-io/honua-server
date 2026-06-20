// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Zarr.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Protocols.Zarr;

/// <summary>
/// Log category for Zarr admin endpoints.
/// </summary>
internal sealed class ZarrEndpointsLog;

/// <summary>
/// Admin endpoints for Zarr store registration and metadata discovery.
/// </summary>
internal static class ZarrEndpoints
{
    private const string DuplicateRegistrationMessage =
        "A Zarr store with the same layer, provider, bucket, and root path is already registered.";

    /// <summary>
    /// Maps Zarr admin endpoints.
    /// </summary>
    public static void MapZarrEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/zarr-stores")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Zarr Stores")
            .RequireAdminAuthorization();

        group.MapPost("/", HandleRegister)
            .WithDisplayName("Register Zarr Store")
            .WithSummary("Register a Zarr coverage store for read-only serving");

        group.MapGet("/", HandleList)
            .WithDisplayName("List Zarr Stores")
            .WithSummary("List registered Zarr stores for a layer");

        group.MapGet("/{id:long}", HandleGet)
            .WithDisplayName("Get Zarr Store")
            .WithSummary("Get registration details for a Zarr store");

        group.MapDelete("/{id:long}", HandleDelete)
            .WithDisplayName("Unregister Zarr Store")
            .WithSummary("Unregister a Zarr store");

        group.MapPost("/{id:long}/refresh", HandleRefresh)
            .WithDisplayName("Refresh Zarr Metadata")
            .WithSummary("Re-scan Zarr metadata from the backing store");

        // Public datacube tile serving (#1835, Shape B): bind a registered Zarr
        // coverage slice to a map tile. Read-only; does not advertise an OGC
        // conformance class, so it leaves the CITE tile surfaces untouched.
        var tiles = endpoints.MapGroup("/api/v{version:apiVersion}/datacubes/{layerId:int}/tiles")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Datacubes", "Tiles");

        tiles.MapGet("/{tileMatrixSetId}/{z:int}/{x:int}/{y:int}", HandleDatacubeTile)
            .WithDisplayName("Datacube Tile")
            .WithSummary("Render a registered Zarr coverage slice as a PNG map tile");
    }

    /// <summary>
    /// Decoded payload cap for a single tile slice. A 256x256 float64 grid plus a
    /// little headroom; the planner rejects anything larger before any read happens.
    /// </summary>
    private const long MaxTileSliceBytes = 4L * 1024L * 1024L;

    private static async Task<IResult> HandleDatacubeTile(
        HttpContext context,
        int layerId,
        string tileMatrixSetId,
        int z,
        int x,
        int y,
        [FromServices] IZarrStore store,
        [FromServices] IZarrSubsetReader subsetReader,
        [FromServices] IEnumerable<ICloudRangeReader> rangeReaders,
        [FromServices] ITileMatrixSetRegistry tileMatrixSets,
        ILogger<ZarrEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        if (x < 0 || y < 0 || z < 0)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Tile coordinates must be non-negative.");
        }

        var registrations = await store.ListByLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        ZarrRegistration? servable = null;
        foreach (var candidate in registrations)
        {
            if (candidate.Metadata is not null)
            {
                servable = candidate;
                break;
            }
        }

        if (servable is null || servable.Metadata is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, "No servable Zarr coverage is registered for this layer.");
        }

        var metadata = servable.Metadata;

        if (!tileMatrixSets.TryGetGeometry(tileMatrixSetId, z, out var geometry))
        {
            return StandardErrorHelpers.CreateBadRequest(context, $"Tile matrix set '{tileMatrixSetId}' is not supported.");
        }

        // Resolvable case: the gridset CRS matches the coverage storage CRS so the
        // tile bbox maps directly to grid indices. Cross-CRS reprojection of the
        // tile window is deferred (#1835 follow-up).
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

        var variable = OgcCommonUtilities.GetQueryValue(context.Request, "variable");
        var datetimeRaw = OgcCommonUtilities.GetQueryValue(context.Request, "datetime");
        (DateTimeOffset? Start, DateTimeOffset? End)? datetime = null;
        if (!string.IsNullOrWhiteSpace(datetimeRaw))
        {
            if (!OgcTemporalFilterParser.TryParseRange(datetimeRaw, out var start, out var end, out var dtError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, dtError ?? "Invalid datetime parameter.");
            }
            datetime = (start, end);
        }

        int? verticalIndex = null;
        var elevationRaw = OgcCommonUtilities.GetQueryValue(context.Request, "elevation");
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
            // A tile that misses the coverage extent is an empty (transparent) tile,
            // which clients expect as a 204 rather than an error.
            if (planError is { } message && message.Contains("does not intersect", StringComparison.Ordinal))
            {
                return Results.NoContent();
            }
            return StandardErrorHelpers.CreateBadRequest(context, planError ?? "The tile could not be resolved against the coverage.");
        }

        var rangeReader = rangeReaders.FirstOrDefault(reader => reader.Provider == servable.Provider);
        if (rangeReader is null)
        {
            return StandardErrorHelpers.CreateInternalServerError(context, "The storage backend for this coverage is not configured.");
        }

        ZarrSubsetResult result;
        try
        {
            result = await subsetReader.ReadSubsetAsync(
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
        ZarrLog.DatacubeTileRendered(logger, layerId, result.Variable, z, x, y, png.Length);
        return Results.Bytes(png, "image/png");
    }

    private static async Task<IResult> HandleRegister(
        RegisterZarrRequest request,
        [FromServices] IMetadataV2GraphProvider graphProvider,
        [FromServices] IZarrStore store,
        ILogger<ZarrEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return TypedResults.BadRequest("Request body is required.");
        }
        if (!request.IsValid(out var error))
        {
            return TypedResults.BadRequest(error);
        }

        if (!await LayerPublicationExistsAsync(graphProvider, request.LayerId, cancellationToken).ConfigureAwait(false))
        {
            return TypedResults.NotFound("Layer not found.");
        }

        ZarrRegistration registration;
        try
        {
            registration = await store.RegisterAsync(new ZarrRegistrationRequest
            {
                LayerId = request.LayerId,
                Name = request.Name,
                Description = request.Description,
                Provider = request.Provider,
                Bucket = request.Bucket,
                RootPath = request.RootPath
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already registered", StringComparison.Ordinal))
        {
            return TypedResults.Conflict(DuplicateRegistrationMessage);
        }

        var providerName = registration.Provider.ToString();
        ZarrLog.ZarrRegistered(
            logger,
            registration.Name,
            registration.Id,
            registration.LayerId,
            providerName,
            registration.Bucket,
            registration.RootPath);

        return Results.Json(ToResponse(registration), ZarrJsonContext.Default.ZarrRegistrationResponse, statusCode: 201);
    }

    private static async Task<IResult> HandleList(
        [FromServices] IZarrStore store,
        ILogger<ZarrEndpointsLog> logger,
        int? layerId = null,
        CancellationToken cancellationToken = default)
    {
        if (!layerId.HasValue)
        {
            return TypedResults.BadRequest("Query parameter 'layerId' is required.");
        }

        var registrations = await store.ListByLayerAsync(layerId.Value, cancellationToken).ConfigureAwait(false);
        ZarrLog.ZarrListRetrieved(logger, registrations.Length);
        var responses = registrations.Select(ToResponse).ToArray();
        return Results.Json(responses, ZarrJsonContext.Default.ZarrRegistrationResponseArray);
    }

    private static async Task<IResult> HandleGet(
        long id,
        [FromServices] IZarrStore store,
        CancellationToken cancellationToken)
    {
        var registration = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (registration is null)
        {
            return TypedResults.NotFound();
        }
        return Results.Json(ToResponse(registration), ZarrJsonContext.Default.ZarrRegistrationResponse);
    }

    private static async Task<IResult> HandleDelete(
        long id,
        [FromServices] IZarrStore store,
        ILogger<ZarrEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        var deleted = await store.UnregisterAsync(id, cancellationToken).ConfigureAwait(false);
        if (!deleted)
        {
            return TypedResults.NotFound();
        }
        ZarrLog.ZarrUnregistered(logger, id);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> HandleRefresh(
        long id,
        HttpContext context,
        [FromServices] IZarrStore store,
        [FromServices] IEnumerable<ICloudRangeReader> rangeReaders,
        [FromServices] IZarrMetadataReader metadataReader,
        ILogger<ZarrEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        var registration = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (registration is null)
        {
            return TypedResults.NotFound();
        }

        var reader = rangeReaders.FirstOrDefault(r => r.Provider == registration.Provider);
        if (reader is null)
        {
            return TypedResults.BadRequest($"No range reader configured for provider {registration.Provider}.");
        }

        var refreshProviderName = registration.Provider.ToString();
        ZarrLog.MetadataScanStarted(
            logger,
            id,
            refreshProviderName,
            registration.Bucket,
            registration.RootPath);

        try
        {
            var metadata = await metadataReader
                .ReadMetadataAsync(reader, registration.Bucket, registration.RootPath, cancellationToken)
                .ConfigureAwait(false);
            await store.UpdateMetadataAsync(id, metadata, cancellationToken).ConfigureAwait(false);

            ZarrLog.MetadataScanCompleted(logger, id, metadata.Arrays.Length, metadata.Srid);

            var updated = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
            return Results.Json(ToResponse(updated!), ZarrJsonContext.Default.ZarrRegistrationResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidDataException ex)
        {
            ZarrLog.MetadataScanFailed(logger, ex, id);
            return TypedResults.BadRequest("Zarr metadata is invalid: " + ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            ZarrLog.MetadataScanFailed(logger, ex, id);
            return TypedResults.BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            ZarrLog.MetadataScanFailed(logger, ex, id);
            return StandardErrorHelpers.CreateInternalServerError(context, "Metadata scan failed for the specified Zarr store.");
        }
    }

    /// <summary>
    /// Returns true when the Metadata v2 graph contains any publication whose service-local layer
    /// index matches the supplied id.
    /// </summary>
    private static async Task<bool> LayerPublicationExistsAsync(
        IMetadataV2GraphProvider graphProvider,
        int layerId,
        CancellationToken cancellationToken)
    {
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        foreach (var publication in snapshot.Graph.Publications)
        {
            if (publication.LayerIndex == layerId)
            {
                return true;
            }
        }
        return false;
    }

    private static ZarrRegistrationResponse ToResponse(ZarrRegistration registration)
    {
        ZarrVariableSummary[]? variables = null;
        if (registration.Metadata is { } metadata)
        {
            variables = metadata.Arrays.Select(a => new ZarrVariableSummary
            {
                Name = a.Name,
                Shape = a.Shape,
                Chunks = a.Chunks,
                DataType = a.DataType,
                Compressor = a.Compressor,
                DimensionNames = a.DimensionNames
            }).ToArray();
        }

        return new ZarrRegistrationResponse
        {
            Id = registration.Id,
            LayerId = registration.LayerId,
            Name = registration.Name,
            Description = registration.Description,
            Provider = registration.Provider.ToString(),
            Bucket = registration.Bucket,
            RootPath = registration.RootPath,
            ZarrFormat = registration.Metadata is null ? null : (int)registration.Metadata.ZarrFormat,
            Srid = registration.Metadata?.Srid,
            VariableCount = registration.Metadata?.Arrays.Length,
            PrimaryVariable = registration.Metadata?.PrimaryVariable,
            Variables = variables,
            MetadataScannedAt = registration.MetadataScannedAt,
            CreatedAt = registration.CreatedAt
        };
    }
}
