// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.CloudCog.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Honua.Server.Features.CloudCog;

/// <summary>
/// Log category for cloud COG endpoints.
/// </summary>
internal sealed class CloudCogEndpointsLog;

/// <summary>
/// Admin endpoints for cloud COG registration and management.
/// </summary>
internal static class CloudCogEndpoints
{
    /// <summary>
    /// Maps cloud COG admin endpoints.
    /// </summary>
    public static void MapCloudCogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/cloud-rasters")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Cloud Rasters")
            .RequireAdminAuthorization();

        group.MapPost("/", HandleRegister)
            .WithDisplayName("Register Cloud COG")
            .WithSummary("Register a cloud-hosted COG for direct serving");

        group.MapGet("/", HandleList)
            .WithDisplayName("List Cloud COGs")
            .WithSummary("List registered cloud COGs for a layer");

        group.MapGet("/{id:long}", HandleGet)
            .WithDisplayName("Get Cloud COG")
            .WithSummary("Get registration details for a cloud COG");

        group.MapDelete("/{id:long}", HandleDelete)
            .WithDisplayName("Unregister Cloud COG")
            .WithSummary("Unregister a cloud COG");

        group.MapPost("/{id:long}/refresh", HandleRefresh)
            .WithDisplayName("Refresh Cloud COG Metadata")
            .WithSummary("Re-scan COG metadata from cloud storage");
    }

    private static async Task<IResult> HandleRegister(
        RegisterCloudCogRequest request,
        [FromServices] ILayerCatalog layerCatalog,
        [FromServices] ICloudCogStore store,
        ILogger<CloudCogEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        if (!request.IsValid(out var error))
        {
            return TypedResults.BadRequest(error);
        }

        if (!await layerCatalog.LayerExistsAsync(request.LayerId, cancellationToken).ConfigureAwait(false))
        {
            return TypedResults.NotFound("Layer not found.");
        }

        CloudCogRegistration registration;
        try
        {
            registration = await store.RegisterAsync(new CloudCogRegistrationRequest
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
            return TypedResults.Conflict(ex.Message);
        }

        var providerName = registration.Provider.ToString();
        CloudCogLog.CogRegistered(logger, registration.Name, registration.Id, registration.LayerId,
            providerName, registration.Bucket, registration.ObjectKey);

        return Results.Json(ToResponse(registration), CloudCogJsonContext.Default.CloudCogRegistrationResponse, statusCode: 201);
    }

    private static async Task<IResult> HandleList(
        [FromServices] ICloudCogStore store,
        ILogger<CloudCogEndpointsLog> logger,
        int? layerId = null,
        CancellationToken cancellationToken = default)
    {
        // If layerId is specified, filter by layer; otherwise list all
        CloudCogRegistration[] registrations;
        if (layerId.HasValue)
        {
            registrations = await store.ListByLayerAsync(layerId.Value, cancellationToken);
        }
        else
        {
            return TypedResults.BadRequest("Query parameter 'layerId' is required.");
        }

        CloudCogLog.CogListRetrieved(logger, registrations.Length);
        var responses = registrations.Select(ToResponse).ToArray();
        return Results.Json(responses, CloudCogJsonContext.Default.CloudCogRegistrationResponseArray);
    }

    private static async Task<IResult> HandleGet(
        long id,
        [FromServices] ICloudCogStore store,
        CancellationToken cancellationToken)
    {
        var registration = await store.GetAsync(id, cancellationToken);
        if (registration == null)
        {
            return TypedResults.NotFound();
        }

        return Results.Json(ToResponse(registration), CloudCogJsonContext.Default.CloudCogRegistrationResponse);
    }

    private static async Task<IResult> HandleDelete(
        long id,
        [FromServices] ICloudCogStore store,
        IMemoryCache cache,
        ILogger<CloudCogEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        var deleted = await store.UnregisterAsync(id, cancellationToken);
        if (!deleted)
        {
            return TypedResults.NotFound();
        }

        cache.Remove(CloudCogTileResolver.MetadataCacheKey(id));
        CloudCogLog.CogUnregistered(logger, id);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> HandleRefresh(
        long id,
        HttpContext context,
        [FromServices] ICloudCogStore store,
        [FromServices] IEnumerable<ICloudRangeReader> rangeReaders,
        [FromServices] ICogMetadataReader metadataReader,
        IMemoryCache cache,
        ILogger<CloudCogEndpointsLog> logger,
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
        CloudCogLog.MetadataScanStarted(logger, id, refreshProviderName, registration.Bucket, registration.ObjectKey);

        try
        {
            var metadata = await metadataReader.ReadMetadataAsync(reader, registration.Bucket, registration.ObjectKey, cancellationToken);

            // Warn about non-web-mercator CRS
            if (metadata.Srid is not (3857 or 4326) and > 0)
            {
                CloudCogLog.NonWebMercatorCrs(logger, id, metadata.Srid);
            }

            // Warn about unsupported compression
            if (metadata.Compression is not ("JPEG" or "DEFLATE" or "NONE" or ""))
            {
                CloudCogLog.UnsupportedCompression(logger, metadata.Compression, id);
            }

            await store.UpdateMetadataAsync(id, metadata, ifdCache: null, cancellationToken);

            // Evict stale tile-resolver metadata cache so the next tile request
            // picks up the freshly-scanned IFD offsets and extent.
            cache.Remove(CloudCogTileResolver.MetadataCacheKey(id));

            CloudCogLog.MetadataScanCompleted(logger, id, metadata.Width, metadata.Height, metadata.BandCount, metadata.OverviewLevels.Length);

            var updated = await store.GetAsync(id, cancellationToken);
            return Results.Json(ToResponse(updated!), CloudCogJsonContext.Default.CloudCogRegistrationResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            CloudCogLog.MetadataScanFailed(logger, ex, id);
            return StandardErrorHelpers.CreateInternalServerError(context, "Metadata scan failed for the specified cloud COG.");
        }
    }

    private static CloudCogRegistrationResponse ToResponse(CloudCogRegistration reg) => new()
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
