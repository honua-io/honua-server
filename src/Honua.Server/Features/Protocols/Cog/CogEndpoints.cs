// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.Protocols.Cog.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Honua.Server.Features.Protocols.Cog;

/// <summary>
/// Log category for COG endpoints.
/// </summary>
internal sealed class CogEndpointsLog;

/// <summary>
/// Admin endpoints for COG registration and management.
/// </summary>
internal static class CogEndpoints
{
    private const string DuplicateRegistrationMessage =
        "A cloud raster with the same layer, provider, bucket, and object key is already registered.";

    /// <summary>
    /// Maps COG admin endpoints.
    /// </summary>
    public static void MapCogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/cloud-rasters")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Cloud Rasters")
            .RequireAdminAuthorization();

        group.MapPost("/", HandleRegister)
            .WithDisplayName("Register COG")
            .WithSummary("Register a cloud-hosted COG for direct serving");

        group.MapGet("/", HandleList)
            .WithDisplayName("List COGs")
            .WithSummary("List registered COGs for a layer");

        group.MapGet("/{id:long}", HandleGet)
            .WithDisplayName("Get COG")
            .WithSummary("Get registration details for a COG");

        group.MapDelete("/{id:long}", HandleDelete)
            .WithDisplayName("Unregister COG")
            .WithSummary("Unregister a COG");

        group.MapPost("/{id:long}/refresh", HandleRefresh)
            .WithDisplayName("Refresh COG Metadata")
            .WithSummary("Re-scan COG metadata from cloud storage");
    }

    private static async Task<IResult> HandleRegister(
        RegisterCogRequest request,
        [FromServices] IMetadataV2GraphProvider graphProvider,
        [FromServices] ICogStore store,
        ILogger<CogEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        if (!request.IsValid(out var error))
        {
            return TypedResults.BadRequest(error);
        }

        if (!await LayerPublicationExistsAsync(graphProvider, request.LayerId, cancellationToken).ConfigureAwait(false))
        {
            return TypedResults.NotFound("Layer not found.");
        }

        CogRegistration registration;
        try
        {
            registration = await store.RegisterAsync(new CogRegistrationRequest
            {
                LayerId = request.LayerId,
                Name = request.Name,
                Description = request.Description,
                Provider = request.Provider,
                Bucket = request.Bucket,
                ObjectKey = request.ObjectKey
            }, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is not null &&
            ex.Message.Contains("already registered", StringComparison.Ordinal))
        {
            return TypedResults.Conflict(DuplicateRegistrationMessage);
        }

        var providerName = registration.Provider.ToString();
        CogLog.CogRegistered(logger, registration.Name, registration.Id, registration.LayerId,
            providerName, registration.Bucket, registration.ObjectKey);

        return Results.Json(ToResponse(registration), CogJsonContext.Default.CogRegistrationResponse, statusCode: 201);
    }

    private static async Task<IResult> HandleList(
        [FromServices] ICogStore store,
        ILogger<CogEndpointsLog> logger,
        int? layerId = null,
        CancellationToken cancellationToken = default)
    {
        // If layerId is specified, filter by layer; otherwise list all
        CogRegistration[] registrations;
        if (layerId.HasValue)
        {
            registrations = await store.ListByLayerAsync(layerId.Value, cancellationToken);
        }
        else
        {
            return TypedResults.BadRequest("Query parameter 'layerId' is required.");
        }

        CogLog.CogListRetrieved(logger, registrations.Length);
        var responses = registrations.Select(ToResponse).ToArray();
        return Results.Json(responses, CogJsonContext.Default.CogRegistrationResponseArray);
    }

    private static async Task<IResult> HandleGet(
        long id,
        [FromServices] ICogStore store,
        CancellationToken cancellationToken)
    {
        var registration = await store.GetAsync(id, cancellationToken);
        if (registration == null)
        {
            return TypedResults.NotFound();
        }

        return Results.Json(ToResponse(registration), CogJsonContext.Default.CogRegistrationResponse);
    }

    private static async Task<IResult> HandleDelete(
        long id,
        [FromServices] ICogStore store,
        IMemoryCache cache,
        ILogger<CogEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        var deleted = await store.UnregisterAsync(id, cancellationToken);
        if (!deleted)
        {
            return TypedResults.NotFound();
        }

        cache.Remove(CogTileResolver.MetadataCacheKey(id));
        CogLog.CogUnregistered(logger, id);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> HandleRefresh(
        long id,
        HttpContext context,
        [FromServices] ICogStore store,
        [FromServices] IEnumerable<ICloudRangeReader> rangeReaders,
        [FromServices] ICogMetadataReader metadataReader,
        IMemoryCache cache,
        ILogger<CogEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        var registration = await store.GetAsync(id, cancellationToken);
        if (registration == null)
        {
            return TypedResults.NotFound();
        }

        var reader = rangeReaders.FirstOrDefault(r => r.Provider == registration.Provider);
        if (reader == null)
        {
            return TypedResults.BadRequest($"No range reader configured for provider {registration.Provider}.");
        }

        var refreshProviderName = registration.Provider.ToString();
        CogLog.MetadataScanStarted(logger, id, refreshProviderName, registration.Bucket, registration.ObjectKey);

        try
        {
            var metadata = await metadataReader.ReadMetadataAsync(reader, registration.Bucket, registration.ObjectKey, cancellationToken);

            // Warn about non-web-mercator CRS
            if (metadata.Srid is not (3857 or 4326) and > 0)
            {
                CogLog.NonWebMercatorCrs(logger, id, metadata.Srid);
            }

            // Warn about unsupported compression
            if (metadata.Compression is not ("JPEG" or "DEFLATE" or "NONE" or ""))
            {
                CogLog.UnsupportedCompression(logger, metadata.Compression, id);
            }

            await store.UpdateMetadataAsync(id, metadata, ifdCache: null, cancellationToken);

            // Evict stale tile-resolver metadata cache so the next tile request
            // picks up the freshly-scanned IFD offsets and extent.
            cache.Remove(CogTileResolver.MetadataCacheKey(id));

            CogLog.MetadataScanCompleted(logger, id, metadata.Width, metadata.Height, metadata.BandCount, metadata.OverviewLevels.Length);

            var updated = await store.GetAsync(id, cancellationToken);
            return Results.Json(ToResponse(updated!), CogJsonContext.Default.CogRegistrationResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            CogLog.MetadataScanFailed(logger, ex, id);
            return StandardErrorHelpers.CreateInternalServerError(context, "Metadata scan failed for the specified COG.");
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

    private static CogRegistrationResponse ToResponse(CogRegistration reg) => new()
    {
        Id = reg.Id,
        LayerId = reg.LayerId,
        Name = reg.Name,
        Description = reg.Description,
        Provider = reg.Provider.ToString(),
        Bucket = reg.Bucket,
        ObjectKey = reg.ObjectKey,
        Width = reg.Metadata?.Width,
        Height = reg.Metadata?.Height,
        BandCount = reg.Metadata?.BandCount,
        Srid = reg.Metadata?.Srid,
        Compression = reg.Metadata?.Compression,
        OverviewLevelCount = reg.Metadata?.OverviewLevels.Length,
        MetadataScannedAt = reg.MetadataScannedAt,
        CreatedAt = reg.CreatedAt
    };
}
