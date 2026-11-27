// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Admin.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Admin;

internal static class AdminLayerValidationEndpoints
{
    public static void MapAdminLayerValidationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/metadata/layers")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Metadata", "Validation")
            .RequireAdminAuthorization();

        _ = group.MapGet("/{storageLayerId:int}/validation", HandleGetLayerValidation)
            .WithName("GetAdminLayerValidation")
            .WithSummary("Validate layer metadata against its backing storage")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    private static async Task<IResult> HandleGetLayerValidation(
        int storageLayerId,
        HttpContext context,
        [FromServices] LayerValidationService validationService,
        CancellationToken cancellationToken)
    {
        if (storageLayerId < 0)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                $"Layer identifier '{storageLayerId}' is invalid.");
        }

        var graphProvider = context.RequestServices.GetService<IMetadataV2GraphProvider>();
        if (graphProvider is not null)
        {
            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var resolution = snapshot.ResolveStorageResource(storageLayerId);
            if (resolution.Resource is not null
                && !ServiceResourceValidationHelpers.IsAdminResourceTenantVisible(context, snapshot, resolution.Resource))
            {
                return ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status404NotFound,
                    $"Layer {storageLayerId} not found.");
            }
        }

        var validation = await validationService.ValidateLayerAsync(storageLayerId, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return ServiceResourceValidationHelpers.CreateAdminValidationProblem(context, validation, $"Layer {storageLayerId} not found.");
        }

        var payload = ApiResponse<LayerValidationResponse>.CreateSuccess(validation.Resource!);
        return Results.Json(payload, LayerValidationJsonContext.Default.ApiResponseLayerValidationResponse);
    }
}
