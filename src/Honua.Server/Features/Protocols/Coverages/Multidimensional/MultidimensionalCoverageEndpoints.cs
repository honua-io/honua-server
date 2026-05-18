// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Multidimensional.Abstractions;
using Honua.Core.Features.Raster.Multidimensional.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Protocols.Coverages.Multidimensional.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Protocols.Coverages.Multidimensional;

/// <summary>
/// Log category for multidimensional coverage admin endpoints.
/// </summary>
internal sealed class MultidimensionalCoverageEndpointsLog;

/// <summary>
/// Admin endpoints for cloud-optimized HDF5 / NetCDF4 coverage registration.
/// See ADR-0039 for the reader strategy.
/// </summary>
internal static class MultidimensionalCoverageEndpoints
{
    private const string DuplicateRegistrationMessage =
        "A multidimensional coverage is already registered for this layer / provider / bucket / object key.";

    private const string ReaderNotEnabledStatus = "reader-not-enabled";

    /// <summary>
    /// Maps multidimensional coverage admin endpoints.
    /// </summary>
    public static void MapMultidimensionalCoverageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/multidim-coverages")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Multidimensional Coverages")
            .RequireAdminAuthorization();

        group.MapPost("/", HandleRegister)
            .WithDisplayName("Register Multidim Coverage")
            .WithSummary("Register a cloud-hosted HDF5 / NetCDF4 coverage source");

        group.MapGet("/", HandleList)
            .WithDisplayName("List Multidim Coverages")
            .WithSummary("List registered HDF5 / NetCDF4 coverage sources for a layer");

        group.MapGet("/{id:long}", HandleGet)
            .WithDisplayName("Get Multidim Coverage")
            .WithSummary("Get registration details for an HDF5 / NetCDF4 coverage source");

        group.MapDelete("/{id:long}", HandleDelete)
            .WithDisplayName("Unregister Multidim Coverage")
            .WithSummary("Unregister an HDF5 / NetCDF4 coverage source");

        group.MapPost("/{id:long}/refresh", HandleRefresh)
            .WithDisplayName("Refresh Multidim Coverage Metadata")
            .WithSummary("Re-scan metadata from cloud storage (returns 501 until a reader is enabled)");
    }

    private static async Task<IResult> HandleRegister(
        RegisterMultidimensionalCoverageRequest request,
        [FromServices] ILayerCatalog layerCatalog,
        [FromServices] IMultidimensionalCoverageStore store,
        ILogger<MultidimensionalCoverageEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = new MultidimensionalCoverageRegistrationRequest
        {
            LayerId = request.LayerId,
            Name = request.Name,
            Description = request.Description,
            Format = request.Format,
            Provider = request.Provider,
            Bucket = request.Bucket,
            ObjectKey = request.ObjectKey,
            Variables = request.Variables ?? Array.Empty<string>()
        };

        if (!MultidimensionalCoverageValidation.TryValidate(validation, out var error))
        {
            return TypedResults.BadRequest(error);
        }

        if (!await layerCatalog.LayerExistsAsync(request.LayerId, cancellationToken).ConfigureAwait(false))
        {
            return TypedResults.NotFound("Layer not found.");
        }

        MultidimensionalCoverageRegistration registration;
        try
        {
            registration = await store.RegisterAsync(validation, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (
            ex.InnerException is not null &&
            ex.Message.Contains("already registered", StringComparison.Ordinal))
        {
            return TypedResults.Conflict(DuplicateRegistrationMessage);
        }

        MultidimensionalCoverageLog.Registered(
            logger,
            registration.Name,
            registration.Id,
            registration.LayerId,
            registration.Format,
            registration.Provider,
            registration.Bucket,
            registration.ObjectKey);

        return Results.Json(
            ToResponse(registration),
            MultidimensionalCoverageJsonContext.Default.MultidimensionalCoverageRegistrationResponse,
            statusCode: 201);
    }

    private static async Task<IResult> HandleList(
        [FromServices] IMultidimensionalCoverageStore store,
        ILogger<MultidimensionalCoverageEndpointsLog> logger,
        int? layerId = null,
        CancellationToken cancellationToken = default)
    {
        if (!layerId.HasValue)
        {
            return TypedResults.BadRequest("Query parameter 'layerId' is required.");
        }

        var registrations = await store.ListByLayerAsync(layerId.Value, cancellationToken).ConfigureAwait(false);
        MultidimensionalCoverageLog.Listed(logger, registrations.Length, layerId.Value);

        var responses = registrations.Select(ToResponse).ToArray();
        return Results.Json(
            responses,
            MultidimensionalCoverageJsonContext.Default.MultidimensionalCoverageRegistrationResponseArray);
    }

    private static async Task<IResult> HandleGet(
        long id,
        [FromServices] IMultidimensionalCoverageStore store,
        CancellationToken cancellationToken)
    {
        var registration = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (registration is null)
        {
            return TypedResults.NotFound();
        }

        return Results.Json(
            ToResponse(registration),
            MultidimensionalCoverageJsonContext.Default.MultidimensionalCoverageRegistrationResponse);
    }

    private static async Task<IResult> HandleDelete(
        long id,
        [FromServices] IMultidimensionalCoverageStore store,
        ILogger<MultidimensionalCoverageEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        var deleted = await store.UnregisterAsync(id, cancellationToken).ConfigureAwait(false);
        if (!deleted)
        {
            return TypedResults.NotFound();
        }

        MultidimensionalCoverageLog.Unregistered(logger, id);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> HandleRefresh(
        long id,
        [FromServices] IMultidimensionalCoverageStore store,
        [FromServices] IEnumerable<ICloudRangeReader> rangeReaders,
        [FromServices] IMultidimensionalCoverageMetadataReader metadataReader,
        ILogger<MultidimensionalCoverageEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        var registration = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (registration is null)
        {
            return TypedResults.NotFound();
        }

        var rangeReader = rangeReaders.FirstOrDefault(r => r.Provider == registration.Provider);
        if (rangeReader is null)
        {
            return TypedResults.BadRequest(
                $"No range reader configured for provider {registration.Provider}.");
        }

        try
        {
            var metadata = await metadataReader
                .ReadMetadataAsync(rangeReader, registration, cancellationToken)
                .ConfigureAwait(false);

            await store.UpdateMetadataAsync(id, metadata, cancellationToken).ConfigureAwait(false);
            MultidimensionalCoverageLog.MetadataScanCompleted(
                logger,
                id,
                metadata.Variables.Count,
                metadata.Srid);

            var refreshed = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
            return Results.Json(
                ToResponse(refreshed!),
                MultidimensionalCoverageJsonContext.Default.MultidimensionalCoverageRegistrationResponse);
        }
        catch (MultidimensionalCoverageReaderUnavailableException)
        {
            MultidimensionalCoverageLog.MetadataReaderUnavailable(logger, id);
            return Results.Problem(
                title: "HDF/NetCDF reader not enabled",
                detail: "Cloud-optimized HDF5 / NetCDF4 metadata extraction is not enabled in this build. " +
                        "See ADR-0039 for the reader strategy.",
                statusCode: StatusCodes.Status501NotImplemented,
                type: MultidimensionalCoverageReaderUnavailableException.ProblemCode);
        }
        catch (MultidimensionalCoverageUnsupportedLayoutException ex)
        {
            MultidimensionalCoverageLog.MetadataUnsupportedLayout(logger, id, ex.Message);
            return Results.Problem(
                title: "Unsupported HDF/NetCDF layout",
                detail: ex.Message,
                statusCode: StatusCodes.Status422UnprocessableEntity,
                type: MultidimensionalCoverageUnsupportedLayoutException.ProblemCode);
        }
    }

    private static MultidimensionalCoverageRegistrationResponse ToResponse(MultidimensionalCoverageRegistration reg)
    {
        var status = reg.Metadata is null && reg.MetadataScannedAt is null
            ? ReaderNotEnabledStatus
            : null;

        return new MultidimensionalCoverageRegistrationResponse
        {
            Id = reg.Id,
            LayerId = reg.LayerId,
            Name = reg.Name,
            Description = reg.Description,
            Format = reg.Format.ToString(),
            Provider = reg.Provider.ToString(),
            Bucket = reg.Bucket,
            ObjectKey = reg.ObjectKey,
            Variables = reg.Variables,
            Srid = reg.Metadata?.Srid,
            VariableCount = reg.Metadata?.Variables.Count,
            MetadataScannedAt = reg.MetadataScannedAt,
            Status = status,
            CreatedAt = reg.CreatedAt
        };
    }
}
