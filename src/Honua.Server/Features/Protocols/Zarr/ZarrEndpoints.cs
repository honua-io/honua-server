// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
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
