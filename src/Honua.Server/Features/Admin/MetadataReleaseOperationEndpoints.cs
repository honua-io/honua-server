// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.ControlPlane;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for metadata release workflow operation reads.
/// </summary>
internal static class MetadataReleaseOperationEndpoints
{
    private const string DeployControlUnavailableMessage = "Deploy control is temporarily unavailable.";

    public static void MapMetadataReleaseOperationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/metadata/releases")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Metadata")
            .RequireAdminAuthorization();

        group.MapGet("/{packageId}/operation", HandleGetMetadataReleaseOperation)
            .WithDisplayName("Get Metadata Release Operation")
            .WithDescription("Gets the most recent metadata release operation for a package ID. Retry attempts remain stable by operation ID through /api/v1/admin/deploy/operations/{operationId}.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<DeployOperationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> HandleGetMetadataReleaseOperation(
        string packageId,
        [FromServices] IEnumerable<IWorkflowOperationStore> workflowStores,
        HttpContext context,
        [FromServices] IWorkflowOperationReconciler? reconciler = null)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "Package id is required.");
        }

        var workflowStore = workflowStores.FirstOrDefault();
        if (workflowStore == null)
        {
            return Results.Problem(
                title: ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                detail: DeployControlUnavailableMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var operation = await workflowStore.GetByMetadataPackageIdAsync(packageId.Trim(), context.RequestAborted)
            .ConfigureAwait(false);
        if (operation == null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status404NotFound,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                $"Metadata release operation for package '{packageId}' was not found.");
        }

        if (operation.Status is WorkflowOperationStatus.Submitted
            or WorkflowOperationStatus.Reconciling
            or WorkflowOperationStatus.RollbackRequested)
        {
            if (reconciler != null)
            {
                await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId, context.RequestAborted)
                    .ConfigureAwait(false);
                operation = await workflowStore.GetAsync(operation.OperationId, context.RequestAborted)
                    .ConfigureAwait(false) ?? operation;
            }
        }

        return Results.Json(
            DeployControlEndpoints.MapOperationResponse(operation),
            DeployControlJsonContext.Default.DeployOperationResponse);
    }
}
