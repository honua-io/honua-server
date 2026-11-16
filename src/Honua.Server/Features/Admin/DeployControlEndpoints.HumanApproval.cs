// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Server.Features.Admin.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

internal static partial class DeployControlEndpoints
{
    private static async Task<IResult> HandleReviewDeployOperation(
        string operationId, string action,
        [FromServices] DeployWorkflowService service,
        [FromServices] OperationHumanApprovalAuthority authority,
        HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (PlatformDeployAuthority.Deny(context) is { } denied) return denied;
        try
        {
            OperationHumanApprovalAuthority.RefuseNonHumanTransport(context);
            var operation = await service.GetAsync(operationId, context.RequestAborted).ConfigureAwait(false);
            if (operation is null) return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status404NotFound, "Not found", "The deploy operation was not found.");
            var review = await authority.OpenDeployReviewAsync(operation, action, context).ConfigureAwait(false);
            return Results.Json(new DeployHumanApprovalReviewResponse(MapOperationResponse(operation), action, review),
                DeployControlJsonContext.Default.DeployHumanApprovalReviewResponse);
        }
        catch (HumanApprovalRequiredException exception)
        {
            return DeployHumanApprovalDenied(exception);
        }
        catch (InvalidOperationException)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(StatusCodes.Status503ServiceUnavailable,
                "Service unavailable", DeployControlUnavailableMessage);
        }
    }

    private static IResult DeployHumanApprovalDenied(HumanApprovalRequiredException exception)
        => ProblemDetailsHelpers.CreateAdminProblem(StatusCodes.Status403Forbidden, "Human approval required", exception.Message);
}
