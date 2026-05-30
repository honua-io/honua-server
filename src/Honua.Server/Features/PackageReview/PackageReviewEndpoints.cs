// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.PackageReview.Abstractions;
using Honua.Core.Features.PackageReview.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.PackageReview;

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
        catch (Exception ex)
        {
            PackageReviewLog.PackageReviewFailed(logger, ex);
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status500InternalServerError,
                "Package review failed",
                "An internal error occurred while reviewing the package.");
        }
    }

}
