// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.ControlPlane;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoint that submits an additive metadata-release layer-evolution operation. This is the
/// create path for <see cref="WorkflowOperationKind.MetadataRelease"/>; reads continue to use
/// <see cref="MetadataReleaseOperationEndpoints"/> and the shared deploy operation endpoints.
/// </summary>
internal static class MetadataReleaseControlEndpoints
{
    private const string ControlUnavailableMessage = "Metadata release control is temporarily unavailable.";
    private const string ConflictMessage = "The requested metadata release conflicts with the current operation state.";

    public static void MapMetadataReleaseControlEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/metadata/releases")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Metadata", "Releases")
            .RequireAdminAuthorization();

        group.MapPost("/operations", HandleCreateMetadataReleaseOperation)
            .WithDisplayName("Create Metadata Release Operation")
            .WithSummary("Submits an additive metadata-release layer-evolution lifecycle operation.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<DeployOperationResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> HandleCreateMetadataReleaseOperation(
        [FromBody] CreateMetadataReleaseOperationRequest request,
        [FromServices] MetadataReleaseControlService controlService,
        HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(request.PackageId) ||
            string.IsNullOrWhiteSpace(request.TargetEnvironment) ||
            string.IsNullOrWhiteSpace(request.ResourceSemanticId) ||
            string.IsNullOrWhiteSpace(request.NewFieldName))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "packageId, targetEnvironment, resourceSemanticId, and newFieldName are required.");
        }

        var scriptId = string.IsNullOrWhiteSpace(request.ScriptId)
            ? $"add-{request.NewFieldName}"
            : request.ScriptId!;

        var plan = new MetadataReleaseExecutionPlan
        {
            PackageId = request.PackageId!,
            TargetEnvironment = request.TargetEnvironment!,
            ResourceSemanticId = request.ResourceSemanticId!,
            NewFieldName = request.NewFieldName!,
            DataPopulateWorkloadId = request.DataPopulateWorkloadId,
            Script = new MetadataReleaseScript
            {
                ScriptId = scriptId,
                Reversible = true,
                ForwardOperations =
                [
                    new MetadataReleaseScriptOperation
                    {
                        Kind = MetadataReleaseScriptOperationKind.AddColumn,
                        ResourceSemanticId = request.ResourceSemanticId!,
                        FieldName = request.NewFieldName!,
                        FieldType = request.NewFieldType,
                        Nullable = true
                    }
                ]
            }
        };

        try
        {
            var operation = await controlService.CreateAsync(
                    plan,
                    ResolveRequestedBy(context),
                    request.Reason,
                    request.IdempotencyKey,
                    request.CorrelationId,
                    context.RequestAborted)
                .ConfigureAwait(false);

            return Results.Json(
                DeployControlEndpoints.MapOperationResponse(operation),
                DeployControlJsonContext.Default.DeployOperationResponse,
                statusCode: StatusCodes.Status201Created);
        }
        catch (ResourceConflictException)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status409Conflict,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status409Conflict),
                ConflictMessage);
        }
        catch (InvalidOperationException)
        {
            return Results.Problem(
                title: ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                detail: ControlUnavailableMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static string? ResolveRequestedBy(HttpContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.User.Identity?.Name))
        {
            return context.User.Identity!.Name;
        }

        var apiKeyId = context.User.FindFirst("api_key_id")?.Value;
        return string.IsNullOrWhiteSpace(apiKeyId) ? null : $"api-key:{apiKeyId}";
    }
}
