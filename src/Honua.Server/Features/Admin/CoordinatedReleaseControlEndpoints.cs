// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Exceptions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for coordinated platform-upgrade releases (Demo C). A coordinated release composes
/// the container rollout, the additive DB script migration, and the metadata-v2 activation into one
/// ordered, gated, rollback-able operation. These endpoints are the create / read / gate-approve /
/// rollback surface the console's GitOps Releases page consumes.
/// </summary>
internal static class CoordinatedReleaseControlEndpoints
{
    private const string ControlUnavailableMessage = "Coordinated release control is temporarily unavailable.";
    private const string ConflictMessage = "The requested coordinated release conflicts with the current operation state.";

    public static void MapCoordinatedReleaseControlEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/metadata/coordinated-releases")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Metadata", "Releases")
            .RequireAdminAuthorization();

        group.MapPost("/operations", HandleCreate)
            .WithDisplayName("Create Coordinated Release Operation")
            .WithSummary("Submits a coordinated container + DB-change + metadata release operation (Demo C).")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<CoordinatedReleaseOperationResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/{packageId}/operation", HandleGet)
            .WithDisplayName("Get Coordinated Release Operation")
            .WithSummary("Gets the coordinated release operation for a release package id.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<CoordinatedReleaseOperationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/operations/{operationId}/approve/{gate}", HandleApproveGate)
            .WithDisplayName("Approve Coordinated Release Gate")
            .WithSummary("Approves the container-swap or data-affecting gate so the conductor can advance.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<CoordinatedReleaseOperationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/operations/{operationId}/rollback", HandleRollback)
            .WithDisplayName("Roll Back Coordinated Release")
            .WithSummary("Requests the single coordinated rollback that unwinds completed steps in reverse.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<CoordinatedReleaseOperationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> HandleCreate(
        [FromBody] CreateCoordinatedReleaseOperationRequest request,
        [FromServices] CoordinatedReleaseControlService controlService,
        HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(request.PackageId) ||
            string.IsNullOrWhiteSpace(request.TargetEnvironment) ||
            string.IsNullOrWhiteSpace(request.ContainerTargetId) ||
            string.IsNullOrWhiteSpace(request.DesiredImage) ||
            string.IsNullOrWhiteSpace(request.ResourceSemanticId) ||
            string.IsNullOrWhiteSpace(request.NewFieldName))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "packageId, targetEnvironment, containerTargetId, desiredImage, resourceSemanticId, and newFieldName are required.");
        }

        var scriptId = string.IsNullOrWhiteSpace(request.ScriptId)
            ? $"add-{request.NewFieldName}"
            : request.ScriptId!;

        var container = new CoordinatedReleaseContainerSpec
        {
            TargetId = request.ContainerTargetId!,
            DesiredImage = request.DesiredImage!,
            CurrentImage = request.CurrentImage,
            RequiresExplicitApproval = true
        };

        var metadataPlan = new MetadataReleaseExecutionPlan
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
                    container,
                    metadataPlan,
                    request.TargetEnvironment!,
                    ResolveRequestedBy(context),
                    request.Reason,
                    request.IdempotencyKey,
                    request.CorrelationId,
                    context.RequestAborted)
                .ConfigureAwait(false);

            return Results.Json(
                MapResponse(operation),
                CoordinatedReleaseJsonContext.Default.CoordinatedReleaseOperationResponse,
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

    private static async Task<IResult> HandleGet(
        string packageId,
        [FromServices] CoordinatedReleaseControlService controlService,
        [FromServices] ICoordinatedReleaseReconciler reconciler,
        HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "Package id is required.");
        }

        WorkflowOperationRecord? operation;
        try
        {
            operation = await controlService.GetByPackageIdAsync(packageId.Trim(), context.RequestAborted).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return Results.Problem(
                title: ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                detail: ControlUnavailableMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (operation is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status404NotFound,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                $"Coordinated release operation for package '{packageId}' was not found.");
        }

        if (operation.Status is WorkflowOperationStatus.Submitted
            or WorkflowOperationStatus.Reconciling
            or WorkflowOperationStatus.RollbackRequested)
        {
            await reconciler.ReconcileCoordinatedReleaseAsync(operation.OperationId, context.RequestAborted).ConfigureAwait(false);
            operation = await controlService.GetAsync(operation.OperationId, context.RequestAborted).ConfigureAwait(false) ?? operation;
        }

        return Results.Json(MapResponse(operation), CoordinatedReleaseJsonContext.Default.CoordinatedReleaseOperationResponse);
    }

    private static async Task<IResult> HandleApproveGate(
        string operationId,
        string gate,
        [FromBody] CoordinatedReleaseActionRequest? request,
        [FromServices] CoordinatedReleaseControlService controlService,
        HttpContext context)
    {
        if (!TryParseGate(gate, out var gateStep))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "gate must be 'container' or 'data'.");
        }

        WorkflowOperationRecord? operation;
        try
        {
            operation = await controlService.ApproveGateAsync(
                    operationId,
                    gateStep,
                    ResolveRequestedBy(context),
                    request?.Reason,
                    context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "The requested gate cannot be approved.");
        }

        if (operation is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status404NotFound,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                $"Coordinated release operation '{operationId}' was not found.");
        }

        return Results.Json(MapResponse(operation), CoordinatedReleaseJsonContext.Default.CoordinatedReleaseOperationResponse);
    }

    private static async Task<IResult> HandleRollback(
        string operationId,
        [FromBody] CoordinatedReleaseActionRequest? request,
        [FromServices] CoordinatedReleaseControlService controlService,
        HttpContext context)
    {
        var operation = await controlService.RequestRollbackAsync(
                operationId,
                ResolveRequestedBy(context),
                request?.Reason,
                context.RequestAborted)
            .ConfigureAwait(false);

        if (operation is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status404NotFound,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                $"Coordinated release operation '{operationId}' was not found.");
        }

        return Results.Json(MapResponse(operation), CoordinatedReleaseJsonContext.Default.CoordinatedReleaseOperationResponse);
    }

    private static bool TryParseGate(string gate, out CoordinatedReleaseStep step)
    {
        switch (gate.Trim().ToLowerInvariant())
        {
            case "container":
                step = CoordinatedReleaseStep.ContainerRollout;
                return true;
            case "data":
            case "metadata":
                step = CoordinatedReleaseStep.MetadataAndSchema;
                return true;
            default:
                step = CoordinatedReleaseStep.Preflight;
                return false;
        }
    }

    internal static CoordinatedReleaseOperationResponse MapResponse(WorkflowOperationRecord operation)
    {
        var context = operation.CoordinatedRelease!;

        var artifacts = new List<CoordinatedReleaseArtifactResponse>
        {
            new()
            {
                Kind = CoordinatedReleaseArtifactKind.ContainerImage.ToString(),
                Title = $"Container rollout · {context.Container.TargetId}",
                Desired = context.Container.DesiredImage,
                Current = context.Container.CurrentImage
            },
            new()
            {
                Kind = CoordinatedReleaseArtifactKind.DatabaseSchema.ToString(),
                Title = $"DB schema · {context.MetadataPlan.Script.ScriptId}",
                Desired = $"Add nullable field '{context.MetadataPlan.NewFieldName}' to {context.MetadataPlan.ResourceSemanticId}",
                Current = null
            },
            new()
            {
                Kind = CoordinatedReleaseArtifactKind.Metadata.ToString(),
                Title = $"Metadata · {context.PackageId}",
                Desired = $"Activate new metadata revision in {context.TargetEnvironment}",
                Current = null
            }
        };

        var steps = context.Steps
            .Select(s => new CoordinatedReleaseStepResponse
            {
                Step = s.Step.ToString(),
                Status = s.Status.ToString(),
                RequiresApproval = s.RequiresApproval,
                IsReversible = s.IsReversible,
                Detail = s.Detail
            })
            .ToList();

        var rollbackReady = context.Steps.Any(s =>
            s.IsReversible && s.Status is CoordinatedReleaseStepStatus.Succeeded or CoordinatedReleaseStepStatus.Running);

        return new CoordinatedReleaseOperationResponse
        {
            OperationId = operation.OperationId,
            PackageId = context.PackageId,
            Status = operation.Status.ToString(),
            CurrentStep = context.CurrentStep.ToString(),
            TargetEnvironment = context.TargetEnvironment,
            CurrentPhase = operation.CurrentPhase,
            Artifacts = artifacts,
            Steps = steps,
            ContainerGateApproved = context.ContainerGateApproved,
            DataGateApproved = context.DataGateApproved,
            ContainerOperationId = context.ContainerOperationId,
            MetadataOperationId = context.MetadataOperationId,
            RollbackReady = rollbackReady,
            Blockers = context.Blockers,
            Warnings = context.Warnings,
            ErrorMessage = operation.ErrorMessage,
            CreatedAt = operation.CreatedAt,
            UpdatedAt = operation.UpdatedAt,
            CompletedAt = operation.CompletedAt
        };
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
