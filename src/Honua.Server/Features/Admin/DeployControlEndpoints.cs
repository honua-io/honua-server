// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Exceptions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.ControlPlane;
using Honua.ControlPlane.Executors;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for deploy coordination primitives.
/// </summary>
internal static class DeployControlEndpoints
{
    private const string DeployControlUnavailableMessage = "Deploy control is temporarily unavailable.";
    private const string DeployConflictMessage = "The requested deploy action conflicts with the current operation state.";

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

        group.MapGet("/operations", HandleListDeployOperations)
            .WithDisplayName("List Deploy Operations")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<DeployOperationListResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/operations", HandleCreateDeployOperation)
            .WithDisplayName("Create Deploy Operation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<DeployOperationResponse>(StatusCodes.Status201Created);

        group.MapGet("/operations/{operationId}", HandleGetDeployOperation)
            .WithDisplayName("Get Deploy Operation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<DeployOperationResponse>();

        group.MapPost("/operations/{operationId}/submit", HandleSubmitDeployOperation)
            .WithDisplayName("Submit Deploy Operation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<DeployOperationResponse>();

        group.MapPost("/operations/{operationId}/promote", HandlePromoteDeployOperation)
            .WithDisplayName("Promote Deploy Operation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<DeployOperationResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/operations/{operationId}/rollback", HandleRollbackDeployOperation)
            .WithDisplayName("Rollback Deploy Operation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<DeployOperationResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        var platformReleaseGroup = endpoints.MapGroup("/api/v{version:apiVersion}/admin/platform-release")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Deploy")
            .RequireAdminAuthorization();

        platformReleaseGroup.MapPost("/converge", HandleConvergePlatformRelease)
            .WithDisplayName("Converge Platform Release")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<PlatformReleaseConvergeResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> HandleGetDeployPreflight(
        [FromServices] IDeployPreflightProbe deployPreflightProbe,
        [FromServices] IOptions<DeploymentOptions> deploymentOptions,
        [FromServices] IHostEnvironment hostEnvironment,
        [FromServices] IOptionsMonitor<ControlPlaneOptions> controlPlaneOptions,
        HttpContext context)
    {
        var snapshot = await deployPreflightProbe.ProbeAsync(context.RequestAborted).ConfigureAwait(false);
        var includeDiagnostics = ShouldIncludeDiagnostics(context);

        var response = new DeployPreflightResponse
        {
            Status = snapshot.Status,
            ReadyForCoordinatedDeploy = snapshot.ReadyForCoordinatedDeploy,
            Message = snapshot.ReadyForCoordinatedDeploy
                ? "Instance is ready for coordinated deployment."
                : "Instance is not ready for coordinated deployment.",
            ServerVersion = includeDiagnostics ? typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown" : null,
            Environment = includeDiagnostics ? hostEnvironment.EnvironmentName : null,
            DeploymentMode = includeDiagnostics ? deploymentOptions.Value.Mode.ToString() : null,
            InstanceName = includeDiagnostics ? Environment.MachineName : null,
            GeneratedAt = DateTimeOffset.UtcNow,
            Readiness = includeDiagnostics
                ? new DeployPreflightReadiness
                {
                    IsReady = snapshot.Readiness.IsReady,
                    StatusCode = snapshot.Readiness.StatusCode,
                    Message = snapshot.Readiness.Message
                }
                : null,
            Migration = includeDiagnostics
                ? new DeployPreflightMigration
                {
                    LifecycleStatus = snapshot.Migration.LifecycleStatus,
                    Message = snapshot.Migration.Message,
                    PlanAvailable = snapshot.Migration.PlanAvailable,
                    UpgradeRequired = snapshot.Migration.UpgradeRequired,
                    PendingScripts = snapshot.Migration.PendingScripts,
                    ExecutedButNotDiscoveredScripts = snapshot.Migration.ExecutedButNotDiscoveredScripts,
                    PlanError = snapshot.Migration.PlanError,
                    BackupHook = snapshot.Migration.BackupHook
                }
                : null,
            DatabaseCompatibility = includeDiagnostics && snapshot.DatabaseCompatibility != null
                ? new DeployPreflightDatabaseCompatibility
                {
                    IsCompatible = snapshot.DatabaseCompatibility.IsCompatible,
                    EngineVersion = snapshot.DatabaseCompatibility.EngineVersion,
                    PostGisVersion = snapshot.DatabaseCompatibility.PostGisVersion,
                    PostGisRasterVersion = snapshot.DatabaseCompatibility.PostGisRasterVersion,
                    InstalledExtensions = snapshot.DatabaseCompatibility.InstalledExtensions,
                    Warnings = snapshot.DatabaseCompatibility.Warnings,
                    ErrorMessage = snapshot.DatabaseCompatibility.ErrorMessage
                }
                : null,
            PlatformRelease = includeDiagnostics
                ? BuildPlatformReleaseView(controlPlaneOptions.CurrentValue)
                : null
        };

        return Results.Json(response, DeployControlJsonContext.Default.DeployPreflightResponse);
    }

    private static DeployPreflightPlatformRelease BuildPlatformReleaseView(ControlPlaneOptions options)
    {
        var release = options.PlatformRelease.ToDefinition();

        var servingEntries = options.DeployTargets
            .Where(target => !string.IsNullOrWhiteSpace(target.TargetId))
            .Select(target => new PlatformReleasePlaneEntry
            {
                Id = target.TargetId,
                RuntimeProfile = target.RuntimeProfile,
                ExplicitArtifactReference = target.ArtifactReference
            })
            .ToArray();

        var executionEntries = options.ExecutionWorkloads
            .Where(workload => !string.IsNullOrWhiteSpace(workload.WorkloadId))
            .Select(workload => new PlatformReleasePlaneEntry
            {
                Id = workload.WorkloadId,
                RuntimeProfile = workload.RuntimeProfile,
                ExplicitArtifactReference = workload.ArtifactReference
            })
            .ToArray();

        var skew = PlatformReleaseProjection.BuildSkewSnapshot(release, servingEntries, executionEntries);

        return new DeployPreflightPlatformRelease
        {
            ReleaseVersion = skew.ReleaseVersion,
            ReleaseDeclared = skew.ReleaseDeclared,
            IsCoVersioned = skew.IsCoVersioned,
            Serving = skew.Serving.Select(MapPlaneProjection).ToArray(),
            Execution = skew.Execution.Select(MapPlaneProjection).ToArray(),
            SkewedIds = skew.SkewedIds
        };
    }

    private static DeployPreflightPlaneProjection MapPlaneProjection(PlatformReleasePlaneProjection projection)
        => new()
        {
            Id = projection.Id,
            RuntimeProfile = projection.RuntimeProfile,
            EffectiveArtifactReference = projection.EffectiveArtifactReference,
            ProjectedFromRelease = projection.ProjectedFromRelease,
            Skewed = projection.Skewed
        };

    private static bool ShouldIncludeDiagnostics(HttpContext context)
        => context.Request.Query.TryGetValue("includeDiagnostics", out var values) &&
            bool.TryParse(values.ToString(), out var includeDiagnostics) &&
            includeDiagnostics;

    private static async Task<IResult> HandlePlanDeployOperation(
        [FromBody] DeployPlanRequest request,
        [FromServices] DeployWorkflowService deployWorkflowService,
        HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(request.TargetId) || string.IsNullOrWhiteSpace(request.DesiredRevision))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "Both targetId and desiredRevision are required.");
        }

        var result = await deployWorkflowService.PlanAsync(
                request.TargetId,
                request.DesiredRevision,
                request.CurrentRevision,
                request.Parameters,
                context.User,
                context.RequestAborted)
            .ConfigureAwait(false);

        if (result == null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status404NotFound,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                $"Deploy target '{request.TargetId}' was not found.");
        }

        return Results.Json(MapPlanResponse(result), DeployControlJsonContext.Default.DeployPlanResponse);
    }

    private static async Task<IResult> HandleCreateDeployOperation(
        [FromBody] CreateDeployOperationRequest request,
        [FromServices] DeployWorkflowService deployWorkflowService,
        HttpContext context)
    {
        // Approval gating is handled by DeployWorkflowService.CreateAsync which bridges
        // the canonical evaluator and persists AwaitingApproval status when required.
        // Do not gate creation here — the workflow must be allowed to persist the operation.

        if (string.IsNullOrWhiteSpace(request.TargetId) || string.IsNullOrWhiteSpace(request.DesiredRevision))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "Both targetId and desiredRevision are required.");
        }

        if (!TryParsePriority(request.Priority, out var priority))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                $"Invalid priority '{request.Priority}'. Valid values: {string.Join(", ", Enum.GetNames<OperationPriority>())}.");
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
                    context.User,
                    context.RequestAborted)
                .ConfigureAwait(false);

            if (operation == null)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status404NotFound,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                    $"Deploy target '{request.TargetId}' was not found.");
            }

            return Results.Json(MapOperationResponse(operation), DeployControlJsonContext.Default.DeployOperationResponse, statusCode: StatusCodes.Status201Created);
        }
        catch (ResourceConflictException)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status409Conflict,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status409Conflict),
                DeployConflictMessage);
        }
        catch (InvalidOperationException)
        {
            return Results.Problem(
                title: ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                detail: DeployControlUnavailableMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleListDeployOperations(
        HttpRequest request,
        [FromServices] DeployWorkflowService deployWorkflowService,
        CancellationToken cancellationToken)
    {
        var query = request.Query;

        WorkflowOperationKind? kind = null;
        var rawKind = QueryFilterParsers.GetString(query, "kind");
        if (!string.IsNullOrWhiteSpace(rawKind))
        {
            if (!QueryFilterParsers.TryParseDefinedEnum<WorkflowOperationKind>(rawKind, out var parsedKind))
            {
                return ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status400BadRequest,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                    $"'kind' contains unsupported value '{rawKind}'.");
            }

            kind = parsedKind;
        }

        WorkflowOperationStatus? status = null;
        var rawStatus = QueryFilterParsers.GetString(query, "status");
        if (!string.IsNullOrWhiteSpace(rawStatus))
        {
            if (!QueryFilterParsers.TryParseDefinedEnum<WorkflowOperationStatus>(rawStatus, out var parsedStatus))
            {
                return ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status400BadRequest,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                    $"'status' contains unsupported value '{rawStatus}'.");
            }

            status = parsedStatus;
        }

        if (!QueryFilterParsers.TryParseInt(query, "page", out var pageValue, out var parseError) ||
            !QueryFilterParsers.TryParseInt(query, "pageSize", out var pageSizeValue, out parseError))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                parseError);
        }

        var page = pageValue ?? 1;
        var pageSize = pageSizeValue ?? 50;
        if (page < 1 || pageSize < 1)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "'page' and 'pageSize' must be greater than or equal to 1.");
        }

        try
        {
            var result = await deployWorkflowService
                .ListDeployOperationsAsync(status, kind, page, pageSize, cancellationToken)
                .ConfigureAwait(false);

            var response = new DeployOperationListResponse
            {
                Items = result.Items.Select(MapOperationResponse).ToArray(),
                Page = result.Page,
                PageSize = result.PageSize,
                TotalCount = result.TotalCount,
                HasMore = result.HasMore
            };

            return Results.Json(response, DeployControlJsonContext.Default.DeployOperationListResponse);
        }
        catch (InvalidOperationException)
        {
            return Results.Problem(
                title: ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                detail: DeployControlUnavailableMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleGetDeployOperation(
        string operationId,
        [FromServices] DeployWorkflowService deployWorkflowService,
        HttpContext context,
        [FromServices] IWorkflowOperationReconciler? reconciler = null)
    {
        try
        {
            var operation = await deployWorkflowService.GetAsync(operationId, context.RequestAborted).ConfigureAwait(false);
            if (operation == null)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status404NotFound,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                    $"Deploy operation '{operationId}' was not found.");
            }

            if (operation.Status is WorkflowOperationStatus.Submitted or WorkflowOperationStatus.Reconciling or WorkflowOperationStatus.RollbackRequested
                && reconciler != null)
            {
                await reconciler.ReconcileWorkflowOperationAsync(operationId, context.RequestAborted).ConfigureAwait(false);
                operation = await deployWorkflowService.GetAsync(operationId, context.RequestAborted).ConfigureAwait(false) ?? operation;
            }

            return Results.Json(MapOperationResponse(operation), DeployControlJsonContext.Default.DeployOperationResponse);
        }
        catch (InvalidOperationException)
        {
            return Results.Problem(
                title: ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                detail: DeployControlUnavailableMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleSubmitDeployOperation(
        string operationId,
        [FromBody] SubmitDeployOperationRequest? request,
        [FromServices] DeployWorkflowService deployWorkflowService,
        HttpContext context)
    {
        // Submit is the manual approval action — an operator explicitly advancing an
        // AwaitingApproval operation. Re-gating here would make approval-gated deploys
        // permanently unsubmittable. Rollback retains its own destructive-action gate.

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
                return ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status404NotFound,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                    $"Deploy operation '{operationId}' was not found.");
            }

            return Results.Json(MapOperationResponse(operation), DeployControlJsonContext.Default.DeployOperationResponse);
        }
        catch (ResourceConflictException)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status409Conflict,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status409Conflict),
                DeployConflictMessage);
        }
        catch (InvalidOperationException)
        {
            return Results.Problem(
                title: ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                detail: DeployControlUnavailableMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandlePromoteDeployOperation(
        string operationId,
        [FromBody] PromoteDeployOperationRequest? request,
        [FromServices] DeployWorkflowService deployWorkflowService,
        HttpContext context)
    {
        // Manual promotion is the operator escape hatch for a deploy parked awaiting promotion (for
        // example an on-prem rolling deploy with no telemetry gate to auto-clear). It is a forward,
        // non-destructive cutover, so it rides the group-level admin authorization plus audit logging
        // rather than the rollback destructive-action gate — the whole point is that it stays reachable.
        try
        {
            var result = await deployWorkflowService.PromoteAsync(
                    operationId,
                    ResolveRequestedBy(context),
                    request?.Reason,
                    context.RequestAborted)
                .ConfigureAwait(false);

            switch (result.Outcome)
            {
                case DeployWorkflowService.DeployPromotionOutcome.NotFound:
                    return ProblemDetailsHelpers.CreateAdminProblem(
                        StatusCodes.Status404NotFound,
                        ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                        $"Deploy operation '{operationId}' was not found.");
                case DeployWorkflowService.DeployPromotionOutcome.PreconditionFailed:
                    return ProblemDetailsHelpers.CreateAdminProblem(
                        StatusCodes.Status409Conflict,
                        ProblemDetailsHelpers.GetTitle(StatusCodes.Status409Conflict),
                        result.PreconditionDetail ?? DeployConflictMessage);
                default:
                    return Results.Json(
                        MapOperationResponse(result.Operation!),
                        DeployControlJsonContext.Default.DeployOperationResponse);
            }
        }
        catch (ResourceConflictException)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status409Conflict,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status409Conflict),
                DeployConflictMessage);
        }
        catch (InvalidOperationException)
        {
            return Results.Problem(
                title: ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                detail: DeployControlUnavailableMessage,
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
            var existing = await deployWorkflowService.GetAsync(operationId, context.RequestAborted).ConfigureAwait(false);
            if (existing == null)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status404NotFound,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                    $"Deploy operation '{operationId}' was not found.");
            }

            var rollbackPlan = existing.MetadataRelease?.RollbackPlan;
            var isDestructive = rollbackPlan?.IsDataAffecting ?? true;
            var requiresExplicitApproval = rollbackPlan?.RequiresExplicitApproval == true;
            var gate = context.RequestServices.GetRequiredService<OperatorApprovalGate>();
            var approvalResult = gate.EvaluateApproval(
                context,
                OperatorResourceType.Deployment,
                OperatorOperation.Execute,
                resourceId: operationId,
                isDestructive: isDestructive,
                requiresExplicitApproval: requiresExplicitApproval,
                approvalPolicyRef: rollbackPlan?.ApprovalPolicyRef,
                approvalReasonCodes: ResolveMetadataRollbackApprovalReasonCodes(rollbackPlan));
            if (approvalResult != null) return approvalResult;

            var operation = await deployWorkflowService.RequestRollbackAsync(
                    operationId,
                    ResolveRequestedBy(context),
                    request?.Reason,
                    approvedMetadataReleaseIsDataAffecting: isDestructive,
                    approvedMetadataReleaseRequiresApproval: isDestructive || requiresExplicitApproval,
                    cancellationToken: context.RequestAborted)
                .ConfigureAwait(false);

            if (operation == null)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status404NotFound,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                    $"Deploy operation '{operationId}' was not found.");
            }

            return Results.Json(MapOperationResponse(operation), DeployControlJsonContext.Default.DeployOperationResponse);
        }
        catch (ResourceConflictException)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status409Conflict,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status409Conflict),
                DeployConflictMessage);
        }
        catch (InvalidOperationException)
        {
            return Results.Problem(
                title: ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                detail: DeployControlUnavailableMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private const string PlatformReleaseWorkerNote =
        "Worker images are not deployed by converge; they converge at the next geoprocessing dispatch via PlatformReleaseProjection.ResolveWorkerArtifact.";

    /// <summary>
    /// Actuates the declared platform release across the serving deploy targets (ADR-0060 WS2, #2564).
    /// Takes no version argument: it always converges to the release declared in
    /// <c>ControlPlane:PlatformRelease</c>. Per-target divergence is decided against the pinned
    /// contract — a target's last-applied revision is the <c>DesiredRevision</c> of its most recent
    /// terminal-Succeeded deploy operation; a target with no such operation is unknown and treated as
    /// divergent. Config-pinned targets (an explicit artifact diverging from the release) are skipped.
    /// Each divergent target's deploy routes through the guardrail gateway like any other Deploy, keyed
    /// by <c>converge:{version}:{targetId}</c> so a repeated converge folds onto the in-flight operation.
    /// Worker images are not deployed here.
    /// </summary>
    private static async Task<IResult> HandleConvergePlatformRelease(
        [FromBody] PlatformReleaseConvergeRequest? request,
        [FromServices] IOptionsMonitor<ControlPlaneOptions> controlPlaneOptions,
        [FromServices] DeployWorkflowService deployWorkflowService,
        HttpContext context,
        [FromServices] IOperationGateway? gateway = null)
    {
        var options = controlPlaneOptions.CurrentValue;
        var release = options.PlatformRelease.ToDefinition();

        // Validate co-versioning FIRST: a release must bind both planes and declare a serving artifact
        // before the serving plane can be actuated.
        var failures = new List<string>();
        if (release is null)
        {
            failures.Add($"{ControlPlaneOptions.SectionName}:PlatformRelease is not declared; there is nothing to converge.");
        }
        else
        {
            PlatformReleaseValidation.Validate(release, $"{ControlPlaneOptions.SectionName}:PlatformRelease", failures);
        }

        if (failures.Count > 0)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                string.Join(" ", failures));
        }

        if (release is null)
        {
            // Unreachable: PlatformReleaseValidation always adds a failure when release is
            // null, and the check above already returned in that case. Guard explicitly
            // rather than null-forgiving so this stays correct if that invariant changes.
            return Results.Problem(
                title: ProblemDetailsHelpers.GetTitle(StatusCodes.Status500InternalServerError),
                detail: "Platform release validation state is inconsistent.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        // Co-versioning validation guarantees a serving artifact is declared.
        var declaredServing = release.ServingArtifactReference!;
        var declaredVersion = release.Version;

        // Converge routes through the guardrail gateway like any Deploy (no direct-execute bypass). The
        // gateway is only registered when durable control-plane storage is configured.
        if (gateway is null)
        {
            return Results.Problem(
                title: ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                detail: DeployControlUnavailableMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var requestedBy = ResolveRequestedBy(context);
        var reason = string.IsNullOrWhiteSpace(request?.Reason)
            ? $"Converge serving targets to platform release {declaredVersion}."
            : request!.Reason!;

        var outcomes = new List<PlatformReleaseConvergeTargetOutcome>();
        var actuated = false;

        foreach (var target in options.DeployTargets.Where(candidate => !string.IsNullOrWhiteSpace(candidate.TargetId)))
        {
            // Config-pinned targets that pin a DIFFERENT artifact are skipped: config-derived skew
            // cannot be cleared at runtime. A target pinning the SAME artifact is still convergable.
            var explicitPin = target.ArtifactReference;
            if (!string.IsNullOrWhiteSpace(explicitPin) &&
                !string.Equals(explicitPin, declaredServing, StringComparison.Ordinal))
            {
                outcomes.Add(new PlatformReleaseConvergeTargetOutcome
                {
                    TargetId = target.TargetId,
                    Outcome = ConvergeOutcomes.SkippedPinned,
                    Message = "Target pins an explicit artifact that diverges from the release; config-derived skew cannot be cleared at runtime.",
                });
                continue;
            }

            // Divergence contract: last-applied revision = DesiredRevision of the most recent
            // terminal-Succeeded deploy operation for this target. The lookup tolerates the durable
            // store's lazy prune-on-read of demoted (rolled-back) operations — a pruned target simply
            // resolves to null and is treated as divergent below.
            var lastOp = await deployWorkflowService
                .GetMostRecentSucceededDeployAsync(target.TargetId, context.RequestAborted)
                .ConfigureAwait(false);
            var lastApplied = lastOp?.Deploy?.DesiredRevision;

            if (!string.IsNullOrWhiteSpace(lastApplied) &&
                string.Equals(lastApplied, declaredServing, StringComparison.Ordinal))
            {
                // declared == observed: converge is a no-op. NEVER actuate.
                outcomes.Add(new PlatformReleaseConvergeTargetOutcome
                {
                    TargetId = target.TargetId,
                    Outcome = ConvergeOutcomes.AlreadyConverged,
                    LastAppliedRevision = lastApplied,
                    Message = "Last-applied revision already matches the declared serving release.",
                });
                continue;
            }

            var isUnknown = string.IsNullOrWhiteSpace(lastApplied);
            var outcome = await RouteConvergeDeployAsync(
                    gateway,
                    target.TargetId,
                    declaredServing,
                    declaredVersion,
                    requestedBy,
                    reason,
                    request?.CorrelationId,
                    isUnknown,
                    lastApplied,
                    context.RequestAborted)
                .ConfigureAwait(false);
            outcomes.Add(outcome);
            if (outcome.OperationId != null || outcome.ProposalId != null)
            {
                actuated = true;
            }
        }

        var response = new PlatformReleaseConvergeResponse
        {
            ReleaseVersion = declaredVersion,
            ServingArtifactReference = declaredServing,
            Converged = !actuated,
            WorkersDeferred = true,
            WorkerConvergenceNote = PlatformReleaseWorkerNote,
            Targets = outcomes,
            GeneratedAt = DateTimeOffset.UtcNow,
        };

        return Results.Json(response, DeployControlJsonContext.Default.PlatformReleaseConvergeResponse);
    }

    private static async Task<PlatformReleaseConvergeTargetOutcome> RouteConvergeDeployAsync(
        IOperationGateway gateway,
        string targetId,
        string declaredServing,
        string declaredVersion,
        string? requestedBy,
        string reason,
        string? correlationId,
        bool isUnknown,
        string? lastApplied,
        CancellationToken cancellationToken)
    {
        var payload = new DeployExecutionPayload
        {
            TargetId = targetId,
            DesiredRevision = declaredServing,
        }.Serialize();

        var gatewayRequest = new OperationGatewayRequest
        {
            Kind = OperationClass.Deploy,
            RequestedBy = requestedBy,
            Reason = reason,
            CorrelationId = correlationId,
            // Stable idempotency key so a double-converge folds onto one operation/proposal per target.
            IdempotencyKey = $"converge:{declaredVersion}:{targetId}",
            ExecutionPayload = payload,
        };

        var result = await gateway.RouteAsync(gatewayRequest, cancellationToken).ConfigureAwait(false);

        var divergentOutcome = isUnknown
            ? ConvergeOutcomes.UnknownTreatedDivergent
            : ConvergeOutcomes.OperationCreated;

        return result.Outcome switch
        {
            OperationGatewayOutcome.Executed => new PlatformReleaseConvergeTargetOutcome
            {
                TargetId = targetId,
                Outcome = divergentOutcome,
                OperationId = result.ExecutionOperationId,
                LastAppliedRevision = lastApplied,
                Message = result.Message,
            },
            OperationGatewayOutcome.ProposalCreated => new PlatformReleaseConvergeTargetOutcome
            {
                TargetId = targetId,
                Outcome = divergentOutcome,
                ProposalId = result.ProposalId,
                LastAppliedRevision = lastApplied,
                Message = result.Message,
            },
            // Blocked / NotSupported: the guardrail gateway denied or cannot run the deploy. No
            // operation was created — report a defensive blocked outcome so callers see nothing ran.
            _ => new PlatformReleaseConvergeTargetOutcome
            {
                TargetId = targetId,
                Outcome = ConvergeOutcomes.Blocked,
                LastAppliedRevision = lastApplied,
                Message = result.Message,
            },
        };
    }

    private static class ConvergeOutcomes
    {
        public const string OperationCreated = "operation-created";
        public const string SkippedPinned = "skipped-pinned";
        public const string AlreadyConverged = "already-converged";
        public const string UnknownTreatedDivergent = "unknown-treated-divergent";
        public const string Blocked = "blocked";
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

    internal static DeployOperationResponse MapOperationResponse(WorkflowOperationRecord operation)
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
            MetadataRelease = operation.MetadataRelease == null ? null : MapMetadataReleaseResponse(operation.MetadataRelease),
            CreatedAt = operation.CreatedAt,
            UpdatedAt = operation.UpdatedAt,
            CompletedAt = operation.CompletedAt
        };

    private static MetadataReleaseContextResponse MapMetadataReleaseResponse(MetadataReleaseContext metadataRelease)
        => new()
        {
            PackageId = metadataRelease.PackageId,
            GitOperationId = metadataRelease.GitOperationId,
            PrUrl = metadataRelease.PrUrl,
            CommitSha = metadataRelease.CommitSha,
            DesiredRevision = metadataRelease.DesiredRevision,
            TargetEnvironment = metadataRelease.TargetEnvironment,
            DeployOperationId = metadataRelease.DeployOperationId,
            JobIds = metadataRelease.JobIds,
            EvidenceRefs = metadataRelease.EvidenceRefs
                .Select(MapEvidenceRefResponse)
                .ToArray(),
            CurrentStage = metadataRelease.CurrentStage.ToString(),
            RollbackPlan = metadataRelease.RollbackPlan == null ? null : MapRollbackPlanResponse(metadataRelease.RollbackPlan),
            Blockers = metadataRelease.Blockers,
            Warnings = metadataRelease.Warnings
        };

    private static MetadataRollbackPlanResponse MapRollbackPlanResponse(MetadataRollbackPlan rollbackPlan)
        => new()
        {
            Class = rollbackPlan.Class.ToString(),
            IsDataAffecting = rollbackPlan.IsDataAffecting,
            RequiresExplicitApproval = rollbackPlan.RequiresExplicitApproval || rollbackPlan.IsDataAffecting,
            Steps = rollbackPlan.Steps,
            EvidenceRequired = rollbackPlan.EvidenceRequired,
            ApprovalPolicyRef = rollbackPlan.ApprovalPolicyRef
        };

    private static MetadataEvidenceRefResponse MapEvidenceRefResponse(MetadataEvidenceRef evidenceRef)
        => new()
        {
            Kind = evidenceRef.Kind,
            RefId = evidenceRef.RefId,
            Uri = evidenceRef.Uri,
            At = evidenceRef.At
        };

    private static string[] ResolveMetadataRollbackApprovalReasonCodes(MetadataRollbackPlan? rollbackPlan)
        => rollbackPlan?.RequiresExplicitApproval == true
            ? ["metadata-rollback-explicit-approval"]
            : Array.Empty<string>();

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

        // Shared admin API-key auth carries no Name claim; attribute the action to the
        // authenticated key (mirrors AdminApiKeyEndpoints.ResolveCreator). Do not fall back
        // to caller-supplied headers (e.g. X-Admin-Principal) — they are spoofable and would
        // let any credential holder fabricate the audit identity for deploy/rollback actions.
        var apiKeyId = context.User.FindFirst("api_key_id")?.Value;
        if (!string.IsNullOrWhiteSpace(apiKeyId))
        {
            return $"api-key:{apiKeyId}";
        }

        return null;
    }
}
