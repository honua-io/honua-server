// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoint for Metadata v2 release-package compatibility prevalidation.
/// </summary>
internal static class MetadataPrevalidationEndpoints
{
    /// <summary>
    /// Maps Metadata v2 compatibility prevalidation endpoints.
    /// </summary>
    public static void MapMetadataPrevalidationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/metadata")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Metadata", "Validation")
            .RequireAdminAuthorization();

        group.MapPost("/prevalidate", HandlePrevalidate)
            .WithDisplayName("Prevalidate Metadata Release Package Compatibility")
            .WithSummary("Returns an environment-scoped Metadata v2 compatibility report for a release package.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<ApiResponse<MetadataCompatibilityReport>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> HandlePrevalidate(
        [FromBody] MetadataPrevalidateRequest request,
        [FromServices] IMetadataCompatibilityPrevalidationService prevalidationService,
        HttpContext context)
    {
        try
        {
            var report = await prevalidationService.PrevalidateAsync(
                    request.ToCoreRequest(),
                    context.RequestAborted)
                .ConfigureAwait(false);
            return Results.Json(
                ApiResponse<MetadataCompatibilityReport>.CreateSuccess(report),
                MetadataPrevalidationJsonContext.Default.ApiResponseMetadataCompatibilityReport);
        }
        catch (ArgumentException ex)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status500InternalServerError,
                "Metadata compatibility prevalidation could not be completed.");
        }
    }
}
