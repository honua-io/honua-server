// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Multidimensional.Abstractions;
using Honua.Core.Features.Raster.Multidimensional.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Licensing;
using Honua.Server.Features.Protocols.Coverages.Multidimensional.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

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

    private const string MultidimCoverageEntitlement = "raster.multidim-coverage";

    private const string JobSubstrateUnavailableDetail =
        "The execution-job substrate (Redis-backed store/queue) is not available, so the GDAL " +
        "metadata scan cannot be dispatched. Configure the job substrate to enable coverage scans.";

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
            .WithSummary("Dispatch a GDAL metadata scan job (202; poll the returned status URL)");

        group.MapGet("/jobs/{jobId}", HandleScanJobStatus)
            .WithDisplayName("Get Multidim Coverage Scan Job")
            .WithSummary("Get the status of a metadata scan job; materializes metadata on success");
    }

    private static async Task<IResult> HandleRegister(
        RegisterMultidimensionalCoverageRequest request,
        [FromServices] IMetadataV2GraphProvider graphProvider,
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

        if (!await LayerPublicationExistsAsync(graphProvider, request.LayerId, cancellationToken).ConfigureAwait(false))
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
        HttpContext context,
        [FromServices] IMultidimensionalCoverageStore store,
        ILogger<MultidimensionalCoverageEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        var registration = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (registration is null)
        {
            return TypedResults.NotFound();
        }

        var gate = LicenseGate.RequireEntitlement(
            context, MultidimCoverageEntitlement, "Multidimensional Coverage", logger);
        if (gate is not null)
        {
            return gate;
        }

        var jobStore = context.RequestServices.GetService<IExecutionJobStore>();
        var jobQueue = context.RequestServices.GetService<IJobQueue>();
        if (jobStore is null || jobQueue is null)
        {
            return Results.Problem(
                title: "Coverage scan unavailable",
                detail: JobSubstrateUnavailableDetail,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var jobId = await MultidimCoverageScanJob
            .SubmitAsync(jobStore, jobQueue, registration, cancellationToken)
            .ConfigureAwait(false);
        MultidimensionalCoverageLog.ScanJobSubmitted(logger, id, jobId);

        var response = new MultidimensionalCoverageScanJobResponse
        {
            JobId = jobId,
            Status = ToStatusString(ExecutionJobStatus.Queued),
            StatusUrl = $"/api/v1/admin/multidim-coverages/jobs/{jobId}",
        };
        return Results.Json(
            response,
            MultidimensionalCoverageJsonContext.Default.MultidimensionalCoverageScanJobResponse,
            statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> HandleScanJobStatus(
        string jobId,
        HttpContext context,
        [FromServices] IMultidimensionalCoverageStore store,
        ILogger<MultidimensionalCoverageEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        var gate = LicenseGate.RequireEntitlement(
            context, MultidimCoverageEntitlement, "Multidimensional Coverage", logger);
        if (gate is not null)
        {
            return gate;
        }

        var jobStore = context.RequestServices.GetService<IExecutionJobStore>();
        if (jobStore is null)
        {
            return Results.Problem(
                title: "Coverage scan unavailable",
                detail: JobSubstrateUnavailableDetail,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var job = await jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null || !MultidimCoverageScanJob.IsScanJob(job))
        {
            return TypedResults.NotFound();
        }

        var statusUrl = $"/api/v1/admin/multidim-coverages/jobs/{jobId}";
        var response = new MultidimensionalCoverageScanJobResponse
        {
            JobId = jobId,
            Status = ToStatusString(job.Status),
            StatusUrl = statusUrl,
            Error = job.Status == ExecutionJobStatus.Failed ? job.ErrorMessage : null,
        };

        if (job.Status == ExecutionJobStatus.Succeeded &&
            MultidimCoverageScanJob.TryGetRegistrationId(job, out var registrationId))
        {
            var registration = await store.GetAsync(registrationId, cancellationToken).ConfigureAwait(false);
            if (registration is not null)
            {
                // Materialize on first observation of success; UpdateMetadataAsync is
                // idempotent so repeated polls converge.
                var artifact = job.ArtifactReferences.Count > 0 ? job.ArtifactReferences[0] : null;
                var metadata = MultidimCoverageScanJob.TryMapArtifact(
                    artifact,
                    registration.Format,
                    registration.Variables);

                if (metadata is not null)
                {
                    await store.UpdateMetadataAsync(registrationId, metadata, cancellationToken).ConfigureAwait(false);
                    MultidimensionalCoverageLog.MetadataScanCompleted(
                        logger, registrationId, metadata.Variables.Count, metadata.Srid);
                    registration = await store.GetAsync(registrationId, cancellationToken).ConfigureAwait(false);
                }

                await TryRegisterDerivedZarrAsync(context, registration!, artifact, logger, cancellationToken)
                    .ConfigureAwait(false);

                response = response with { Coverage = ToResponse(registration!) };
            }
        }

        return Results.Json(response, MultidimensionalCoverageJsonContext.Default.MultidimensionalCoverageScanJobResponse);
    }

    /// <summary>
    /// Best-effort registration of the worker's derived Zarr store as a Zarr
    /// coverage so OGC Coverages serves pixel slices through the existing reader.
    /// The job substrate and Zarr services are resolved optionally so the metadata
    /// path still succeeds where they are absent.
    /// </summary>
    private static async Task TryRegisterDerivedZarrAsync(
        HttpContext context,
        MultidimensionalCoverageRegistration registration,
        string? artifact,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!MultidimCoverageScanJob.TryGetZarrRootPath(artifact, out var zarrRootPath))
        {
            return;
        }

        var zarrStore = context.RequestServices.GetService<IZarrStore>();
        var zarrMetadataReader = context.RequestServices.GetService<IZarrMetadataReader>();
        if (zarrStore is null || zarrMetadataReader is null)
        {
            return;
        }

        var rangeReaders = context.RequestServices.GetServices<ICloudRangeReader>();

        try
        {
            await MultidimCoverageScanJob.RegisterDerivedZarrAsync(
                zarrStore, zarrMetadataReader, rangeReaders, registration, zarrRootPath, logger, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never fail the status read because derived-Zarr registration hiccuped.
            MultidimensionalCoverageLog.DerivedZarrScanDeferred(logger, registration.Id, ex.Message);
        }
    }

    private static string ToStatusString(ExecutionJobStatus status) => status switch
    {
        ExecutionJobStatus.Queued => "queued",
        ExecutionJobStatus.Provisioning => "provisioning",
        ExecutionJobStatus.Running => "running",
        ExecutionJobStatus.Succeeded => "succeeded",
        ExecutionJobStatus.Failed => "failed",
        ExecutionJobStatus.Cancelled => "cancelled",
        _ => status.ToString().ToLowerInvariant(),
    };

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
