// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.PackageReview.Abstractions;
using Honua.Core.Features.PackageReview.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Server.Features.Console;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.PackageReview;

/// <summary>
/// Admin endpoints for package validation and read-only preview planning.
/// </summary>
internal static partial class PackageReviewEndpoints
{
    internal sealed class PackageReviewEndpointsLog;

    /// <summary>
    /// Maps package-review endpoints.
    /// </summary>
    public static void MapPackageReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/packages")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Packages", "Validation")
            .RequireAdminAuthorization();

        group.MapPost("/validate", HandleValidate)
            .WithDisplayName("Validate Package")
            .WithSummary("Validate a package using the shared package-review contract.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<ApiResponse<PackageReviewResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapPost("/preview", HandlePreview)
            .WithDisplayName("Preview Package")
            .WithSummary("Validate a package and return a read-only preview plan.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<ApiResponse<PackageReviewResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapPost("/", HandlePublishMapPackage)
            .WithDisplayName("Publish Map Package")
            .WithSummary("Persist a map package as an immutable Studio version and publication request.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<MapPackagePublishResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    private static Task<IResult> HandleValidate(
        PackageReviewRequest request,
        HttpContext context,
        [FromServices] IPackageReviewService reviewService,
        [FromServices] ILogger<PackageReviewEndpointsLog> logger)
        => HandleReview(request.WithPreviewPlan(false), context, reviewService, logger);

    private static Task<IResult> HandlePreview(
        PackageReviewRequest request,
        HttpContext context,
        [FromServices] IPackageReviewService reviewService,
        [FromServices] ILogger<PackageReviewEndpointsLog> logger)
        => HandleReview(request.WithPreviewPlan(true), context, reviewService, logger);

    private static async Task<IResult> HandleReview(
        PackageReviewRequest request,
        HttpContext context,
        IPackageReviewService reviewService,
        ILogger<PackageReviewEndpointsLog> logger)
    {
        try
        {
            var reviewContext = PackageReviewContextFactory.FromHttpContext(context);
            var response = await reviewService.ReviewAsync(
                request,
                reviewContext,
                context.RequestAborted).ConfigureAwait(false);

            PackageReviewLog.PackageReviewCompleted(
                logger,
                response.PackageFamily,
                response.Status,
                response.Findings.Count);

            return Results.Json(
                ApiResponse<PackageReviewResponse>.CreateSuccess(response),
                PackageReviewJsonContext.Default.ApiResponsePackageReviewResponse);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional catch-all request-handling boundary: logs and returns a generic
            // admin problem-details response below.
            PackageReviewLog.PackageReviewFailed(logger, ex);
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status500InternalServerError,
                "Package review failed",
                "An internal error occurred while reviewing the package.");
        }
    }

    private static async Task<IResult> HandlePublishMapPackage(
        MapPackagePublishRequest request,
        [FromServices] IStudioPackageLifecycleService lifecycle,
        HttpContext context)
    {
        if (request.Package is null || string.IsNullOrWhiteSpace(request.Package.MapPackageId))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context, StatusCodes.Status400BadRequest, "Map package is required", "package.mapPackageId is required.");
        }

        var actor = ConsolePrincipal.ResolveActorId(context.User);
        var packageJson = JsonSerializer.SerializeToElement(request.Package, PackagingJsonContext.Default.MapPackage);
        var draft = await lifecycle.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = request.Package.MapPackageId,
            WorkspaceId = request.WorkspaceId,
            OwnerId = actor,
            Envelope = new StudioPackageEnvelope
            {
                Family = StudioPackageFamily.Map,
                SchemaVersion = "studio_map.v1",
                Format = request.Package.Format,
                Body = packageJson,
                PublicationIntent = request.Intent
            },
            ActorId = actor
        }, context.RequestAborted).ConfigureAwait(false);

        var version = await lifecycle.SaveDraftAsVersionAsync(
            draft.DraftId, request.Message, actor, draft.Generation, context.RequestAborted).ConfigureAwait(false);
        if (version is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context, StatusCodes.Status409Conflict, "Map package could not be versioned", "The package draft was not available for versioning.");
        }

        var publication = await lifecycle.CreatePublicationRequestAsync(
            version.ItemId,
            version.VersionId,
            request.Intent,
            request.WarningAcknowledgement,
            actor,
            context.RequestAborted).ConfigureAwait(false);
        if (publication is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context, StatusCodes.Status409Conflict, "Map package could not be published", "The immutable package version was not available.");
        }

        return Results.Json(new MapPackagePublishResponse
        {
            PackageId = request.Package.MapPackageId,
            ItemId = version.ItemId,
            VersionId = version.VersionId,
            Package = request.Package with { Status = PackageStatus.Ready, UpdatedAt = version.CreatedAt },
            PublicationRequestId = publication.RequestId,
            PublicationStatus = publication.Status.ToString()
        }, PackageReviewJsonContext.Default.MapPackagePublishResponse, statusCode: StatusCodes.Status201Created);
    }

    internal sealed record MapPackagePublishRequest
    {
        public required MapPackage Package { get; init; }
        public string? MapId { get; init; }
        public string? WorkspaceId { get; init; }
        public string? Message { get; init; }
        public string? WarningAcknowledgement { get; init; }
        public StudioPublicationIntent? Intent { get; init; }
    }

    internal sealed record MapPackagePublishResponse
    {
        public required string PackageId { get; init; }
        public required Guid ItemId { get; init; }
        public required Guid VersionId { get; init; }
        public required MapPackage Package { get; init; }
        public required Guid PublicationRequestId { get; init; }
        public required string PublicationStatus { get; init; }
    }

}
