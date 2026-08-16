// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Infrastructure.Authentication;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing;

/// <summary>
/// Protocol-neutral authenticated content surface for staged geoprocessing outputs. Protocol
/// adapters publish links to this route but do not own its authorization, validation, or storage
/// lifecycle semantics.
/// </summary>
internal static class GeoprocessingOutputArtifactEndpoints
{
    private const string Tag = "Geoprocessing";

    public static IEndpointRouteBuilder MapGeoprocessingOutputArtifactEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/geoprocessing/jobs/{jobId}/artifacts/{artifactIndex:int}/content",
                GetArtifactContent)
            .WithTags(Tag)
            .WithName("GeoprocessingOutputArtifactContent")
            .WithSummary("Download a staged geoprocessing output artifact")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ExcludeFromDescription();

        return endpoints;
    }

    private static async Task<IResult> GetArtifactContent(
        string jobId,
        int artifactIndex,
        HttpContext context,
        IGeoprocessingJobService jobService)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            "geoprocessing.getoutputartifactcontent");
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, "Geoprocessing");
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "GetOutputArtifactContent");
        activity?.SetTag(HonuaTelemetry.Tags.JobId, jobId);

        var gate = context.RequestServices.GetRequiredService<OperatorApprovalGate>();
        var authDecision = await gate.CheckAuthorizationAsync(
            context.User,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Job,
                Operation = OperatorOperation.Read
            },
            context.RequestAborted).ConfigureAwait(false);
        if (!authDecision.IsAllowed)
        {
            return AuthorizationError(authDecision.RequiresAuthentication);
        }

        ExecutionJobRecord job;
        try
        {
            job = await jobService.GetJobAsync(jobId, context.User, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (GeoprocessingNotFoundException)
        {
            return JobNotFound(jobId);
        }
        catch (GeoprocessingAuthorizationException exception)
        {
            return AuthorizationError(exception.RequiresAuthentication);
        }
        catch (GeoprocessingStoreUnavailableException)
        {
            return StoreUnavailable();
        }

        if (job.Spec.Kind != ExecutionJobKind.Geoprocessing)
        {
            return JobNotFound(jobId);
        }

        authDecision = await gate.CheckAuthorizationAsync(
            context.User,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Job,
                ResourceId = job.OperationId,
                Operation = OperatorOperation.Read
            },
            context.RequestAborted).ConfigureAwait(false);
        if (!authDecision.IsAllowed)
        {
            return AuthorizationError(authDecision.RequiresAuthentication);
        }

        if (job.Status != ExecutionJobStatus.Succeeded)
        {
            return ArtifactNotAvailable(jobId, artifactIndex);
        }

        try
        {
            _ = await jobService.GetJobResultsAsync(jobId, context.User, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (GeoprocessingNotFoundException)
        {
            return JobNotFound(jobId);
        }
        catch (GeoprocessingStoreUnavailableException)
        {
            return StoreUnavailable();
        }

        if (artifactIndex < 0
            || artifactIndex >= job.ArtifactReferences.Count
            || !RasterOutputJson.TryDeserialize(job.ArtifactReferences[artifactIndex], out var descriptor)
            || descriptor is not StagedObjectRasterOutputDescriptor staged
            || !RasterOutputDescriptorValidator.Validate(staged).IsValid
            || !string.Equals(staged.JobId, job.OperationId, StringComparison.Ordinal)
            || staged.AttemptNumber != job.AttemptCount)
        {
            return ArtifactNotAvailable(jobId, artifactIndex);
        }

        var store = context.RequestServices.GetService<IGeoprocessingOutputObjectStore>();
        if (store is null
            || store.Provider != staged.Provider
            || !string.Equals(store.StoreReference, staged.StoreReference, StringComparison.Ordinal))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Artifact store unavailable",
                detail: "The staged output store for this artifact is not configured on this host.");
        }

        var stagingOptions = context.RequestServices
            .GetService<IOptionsMonitor<GeoprocessingOutputStagingOptions>>();
        var leaseDuration = stagingOptions?.CurrentValue.ReadLeaseDuration ?? TimeSpan.FromMinutes(15);
        if (!await store.TryAcquireReadLeaseAsync(
                staged.ObjectKey, leaseDuration, context.RequestAborted).ConfigureAwait(false))
        {
            return ArtifactNotAvailable(jobId, artifactIndex);
        }

        var stream = await store.OpenReadAsync(staged.ObjectKey, context.RequestAborted)
            .ConfigureAwait(false);
        if (stream is null)
        {
            return ArtifactNotAvailable(jobId, artifactIndex);
        }

        var checksum = staged.Content.Checksum!;
        var entityTag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue(
            $"\"{checksum.Algorithm}:{checksum.Value}\"");
        return Results.Stream(
            stream,
            staged.Content.MediaType,
            fileDownloadName: null,
            lastModified: null,
            entityTag: entityTag,
            enableRangeProcessing: true);
    }

    private static IResult AuthorizationError(bool requiresAuthentication)
        => Results.Problem(
            statusCode: requiresAuthentication
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status403Forbidden,
            title: requiresAuthentication ? "Authentication required" : "Permission denied",
            detail: requiresAuthentication
                ? "Authentication is required for this operation."
                : "You do not have permission to perform this operation.");

    private static IResult JobNotFound(string jobId)
        => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Job not found",
            detail: $"Job '{jobId}' was not found.");

    private static IResult ArtifactNotAvailable(string jobId, int artifactIndex)
        => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Artifact not available",
            detail: $"Job '{jobId}' has no downloadable staged artifact at index {artifactIndex}.");

    private static IResult StoreUnavailable()
        => Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Job store unavailable",
            detail: "The job store is temporarily unavailable.");
}
