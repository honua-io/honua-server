// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Exceptions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for deploy coordination primitives.
/// </summary>
internal static class DeployControlEndpoints
{
    public static void MapDeployControlEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/deploy")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Deploy")
            .RequireAdminAuthorization();

        group.MapGet("/preflight", HandleGetDeployPreflight)
            .WithDisplayName("Get Deploy Preflight")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<DeployPreflightResponse>();

        group.MapPost("/plan", HandlePlanDeployOperation)
            .WithDisplayName("Plan Deploy Operation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<DeployPlanResponse>();

        group.MapPost("/operations", HandleCreateDeployOperation)
            .WithDisplayName("Create Deploy Operation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<DeployOperationResponse>();

        group.MapGet("/operations/{operationId}", HandleGetDeployOperation)
            .WithDisplayName("Get Deploy Operation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<DeployOperationResponse>();

        group.MapPost("/operations/{operationId}/submit", HandleSubmitDeployOperation)
            .WithDisplayName("Submit Deploy Operation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<DeployOperationResponse>();

        group.MapPost("/operations/{operationId}/rollback", HandleRollbackDeployOperation)
            .WithDisplayName("Rollback Deploy Operation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<DeployOperationResponse>();
    }

    private static async Task<IResult> HandleGetDeployPreflight(
        [FromServices] IDeployPreflightProbe deployPreflightProbe,
        [FromServices] IOptions<DeploymentOptions> deploymentOptions,
        [FromServices] IHostEnvironment hostEnvironment,
        HttpContext context)
    {
        var snapshot = await deployPreflightProbe.ProbeAsync(context.RequestAborted).ConfigureAwait(false);

        var response = new DeployPreflightResponse
        {
            Status = snapshot.Status,
            ReadyForCoordinatedDeploy = snapshot.ReadyForCoordinatedDeploy,
            Message = snapshot.Message,
            ServerVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
            Environment = hostEnvironment.EnvironmentName,
            DeploymentMode = deploymentOptions.Value.Mode.ToString(),
            InstanceName = Environment.MachineName,
            GeneratedAt = DateTimeOffset.UtcNow,
            Readiness = new DeployPreflightReadiness
            {
                IsReady = snapshot.Readiness.IsReady,
                StatusCode = snapshot.Readiness.StatusCode,
                Message = snapshot.Readiness.Message
            },
            Migration = new DeployPreflightMigration
            {
                LifecycleStatus = snapshot.Migration.LifecycleStatus,
                Message = snapshot.Migration.Message,
                PlanAvailable = snapshot.Migration.PlanAvailable,
                UpgradeRequired = snapshot.Migration.UpgradeRequired,
                PendingScripts = snapshot.Migration.PendingScripts,
                ExecutedButNotDiscoveredScripts = snapshot.Migration.ExecutedButNotDiscoveredScripts,
                PlanError = snapshot.Migration.PlanError
            }
        };

        return Results.Json(response, DeployControlJsonContext.Default.DeployPreflightResponse);
    }

    private static async Task<IResult> HandlePlanDeployOperation(
        [FromBody] DeployPlanRequest request,
        [FromServices] DeployWorkflowService deployWorkflowService,
        HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(request.TargetId) || string.IsNullOrWhiteSpace(request.DesiredRevision))
        {
            return Results.BadRequest(ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "Both targetId and desiredRevision are required."));
        }

        var result = await deployWorkflowService.PlanAsync(
                request.TargetId,
                request.DesiredRevision,
                request.CurrentRevision,
                request.Parameters,
                context.RequestAborted)
            .ConfigureAwait(false);

        if (result == null)
        {
            return Results.NotFound(ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status404NotFound,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                $"Deploy target '{request.TargetId}' was not found."));
        }

        return Results.Json(MapPlanResponse(result), DeployControlJsonContext.Default.DeployPlanResponse);
    }

    private static async Task<IResult> HandleCreateDeployOperation(
        [FromBody] CreateDeployOperationRequest request,
        [FromServices] DeployWorkflowService deployWorkflowService,
        HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(request.TargetId) || string.IsNullOrWhiteSpace(request.DesiredRevision))
        {
            return Results.BadRequest(ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "Both targetId and desiredRevision are required."));
        }

        if (!TryParsePriority(request.Priority, out var priority))
        {
            return Results.BadRequest(ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                $"Invalid priority '{request.Priority}'. Valid values: {string.Join(", ", Enum.GetNames<OperationPriority>())}."));
        }

        try
        {
            var operation = await deployWorkflowService.CreateAsync(
                    request.TargetId,
                    request.DesiredRevision,
                    request.CurrentRevision,
                    ResolveRequestedBy(context),
                    request.Reason,
                    request.IdempotencyKey,
                    request.CorrelationId,
                    priority,
                    request.SubmitImmediately ?? true,
                    request.Parameters,
                    context.RequestAborted)
                .ConfigureAwait(false);

            if (operation == null)
            {
                return Results.NotFound(ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status404NotFound,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                    $"Deploy target '{request.TargetId}' was not found."));
            }

            return Results.Json(MapOperationResponse(operation), DeployControlJsonContext.Default.DeployOperationResponse);
        }
        catch (InvalidOperationException ex)
        {
            if (ex is ResourceConflictException)
            {
                return Results.Conflict(ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status409Conflict,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status409Conflict),
                    ex.Message));
            }

            return Results.Problem(
                title: ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleGetDeployOperation(
        string operationId,
        [FromServices] DeployWorkflowService deployWorkflowService,
        [FromServices] IEnumerable<IOperationReconciler> reconcilers,
        HttpContext context)
    {
        try
        {
            var operation = await deployWorkflowService.GetAsync(operationId, context.RequestAborted).ConfigureAwait(false);
            if (operation == null)
            {
                return Results.NotFound(ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status404NotFound,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                    $"Deploy operation '{operationId}' was not found."));
            }

            if (operation.Status is WorkflowOperationStatus.Submitted or WorkflowOperationStatus.Reconciling or WorkflowOperationStatus.RollbackRequested)
            {
                var reconciler = reconcilers.FirstOrDefault();
                if (reconciler != null)
                {
                    await reconciler.ReconcileWorkflowOperationAsync(operationId, context.RequestAborted).ConfigureAwait(false);
                    operation = await deployWorkflowService.GetAsync(operationId, context.RequestAborted).ConfigureAwait(false) ?? operation;
                }
            }

            return Results.Json(MapOperationResponse(operation), DeployControlJsonContext.Default.DeployOperationResponse);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(
                title: ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleSubmitDeployOperation(
        string operationId,
        [FromBody] SubmitDeployOperationRequest? request,
        [FromServices] DeployWorkflowService deployWorkflowService,
        HttpContext context)
    {
        try
        {
            var operation = await deployWorkflowService.SubmitAsync(
                    operationId,
                    ResolveRequestedBy(context),
                    request?.Reason,
                    context.RequestAborted)
                .ConfigureAwait(false);

            if (operation == null)
            {
                return Results.NotFound(ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status404NotFound,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                    $"Deploy operation '{operationId}' was not found."));
            }

            return Results.Json(MapOperationResponse(operation), DeployControlJsonContext.Default.DeployOperationResponse);
        }
        catch (ResourceConflictException ex)
        {
            return Results.Conflict(ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status409Conflict,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status409Conflict),
                ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(
                title: ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleRollbackDeployOperation(
        string operationId,
        [FromBody] RollbackDeployOperationRequest? request,
        [FromServices] DeployWorkflowService deployWorkflowService,
        HttpContext context)
    {
        try
        {
            var operation = await deployWorkflowService.RequestRollbackAsync(
                    operationId,
                    ResolveRequestedBy(context),
                    request?.Reason,
                    context.RequestAborted)
                .ConfigureAwait(false);

            if (operation == null)
            {
                return Results.NotFound(ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status404NotFound,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                    $"Deploy operation '{operationId}' was not found."));
            }

            return Results.Json(MapOperationResponse(operation), DeployControlJsonContext.Default.DeployOperationResponse);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(
                title: ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static DeployPlanResponse MapPlanResponse(DeployWorkflowPlanResult result)
        => new()
        {
            Target = MapTargetResponse(result.Spec),
            ReadyToSubmit = result.Plan.IsReadyToSubmit,
            RequiresApproval = result.Plan.RequiresApproval,
            RequiresOutOfBandMigrations = result.Plan.RequiresOutOfBandMigrations,
            BackendRegistered = result.Capabilities != null,
            Capabilities = result.Capabilities == null
                ? null
                : new DeployBackendCapabilitiesResponse
                {
                    SupportsRollback = result.Capabilities.SupportsRollback,
                    SupportsCancellation = result.Capabilities.SupportsCancellation,
                    SupportsTrafficShifting = result.Capabilities.SupportsTrafficShifting,
                    RequiresOutOfBandMigrations = result.Capabilities.RequiresOutOfBandMigrations,
                    SupportsProgressPolling = result.Capabilities.SupportsProgressPolling,
                    SupportsRevisionPinning = result.Capabilities.SupportsRevisionPinning
                },
            Warnings = result.Plan.Warnings,
            BlockingReasons = result.Plan.BlockingReasons,
            GeneratedAt = DateTimeOffset.UtcNow
        };

    private static DeployOperationResponse MapOperationResponse(WorkflowOperationRecord operation)
        => new()
        {
            OperationId = operation.OperationId,
            Kind = operation.Kind.ToString(),
            Status = operation.Status.ToString(),
            Priority = operation.Priority.ToString(),
            Target = operation.Deploy == null ? null : MapTargetResponse(operation.Deploy),
            ProviderOperationId = operation.ProviderOperationId,
            CurrentPhase = operation.CurrentPhase,
            ObservedState = operation.ObservedState,
            ErrorMessage = operation.ErrorMessage,
            Warnings = operation.Warnings,
            BlockingReasons = operation.BlockingReasons,
            RequestedBy = operation.Audit.RequestedBy,
            Reason = operation.Audit.Reason,
            CorrelationId = operation.Audit.CorrelationId,
            CreatedAt = operation.CreatedAt,
            UpdatedAt = operation.UpdatedAt,
            CompletedAt = operation.CompletedAt
        };

    private static DeployPlanTargetResponse MapTargetResponse(DeployOperationSpec spec)
        => new()
        {
            TargetId = spec.TargetId,
            TargetKind = spec.TargetKind.ToString(),
            Backend = spec.Backend,
            Environment = spec.Environment,
            TargetName = spec.TargetName,
            ArtifactReference = spec.ArtifactReference,
            RuntimeProfile = spec.RuntimeProfile,
            CurrentRevision = spec.CurrentRevision,
            DesiredRevision = spec.DesiredRevision,
            Parameters = spec.Parameters
        };

    private static bool TryParsePriority(string? rawPriority, out OperationPriority priority)
    {
        if (string.IsNullOrWhiteSpace(rawPriority))
        {
            priority = OperationPriority.Normal;
            return true;
        }

        return Enum.TryParse(rawPriority, ignoreCase: true, out priority);
    }

    private static string? ResolveRequestedBy(HttpContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.User.Identity?.Name))
        {
            return context.User.Identity!.Name;
        }

        if (context.Request.Headers.TryGetValue("X-Admin-Principal", out var principal))
        {
            return principal.ToString();
        }

        return null;
    }
}
