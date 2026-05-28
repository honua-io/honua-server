// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Admin.Services;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for validating layer storage metadata.
/// </summary>
internal static class AdminLayerValidationEndpoints
{
    /// <summary>
    /// Maps layer validation endpoints to the admin metadata API group.
    /// </summary>
    public static void MapAdminLayerValidationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/metadata/layers")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Metadata", "Validation")
            .RequireAdminAuthorization();

        _ = group.MapGet("/{layerId:int}/validation", HandleGetLayerValidation)
            .WithName("GetAdminLayerValidation")
            .WithSummary("Validate layer metadata against its backing storage")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    private static async Task<IResult> HandleGetLayerValidation(
        int layerId,
        HttpContext context,
        [FromServices] ILayerValidationService validationService,
        CancellationToken cancellationToken)
    {
        if (layerId < 0)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                $"Layer identifier '{layerId}' is invalid.");
        }

        var validation = await validationService.ValidateLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (validation == null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status404NotFound,
                $"Layer {layerId} not found.");
        }

        var payload = ApiResponse<LayerValidationResponse>.CreateSuccess(validation);
        return Results.Json(payload, LayerValidationJsonContext.Default.ApiResponseLayerValidationResponse);
    }
}
