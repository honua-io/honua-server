// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Exceptions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Security;

namespace Honua.Server.Features.Operations;

/// <summary>
/// HTTP surface for the Honua Operations Toolset (deterministic, no AI): list the grounding
/// catalog, validate and submit operations through the policy-gated dispatcher, and read a
/// submitted handle's status. Admin-authorized.
/// </summary>
internal static class OperationsEndpoints
{
    internal sealed class OperationsEndpointsLog;

    public static void MapOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/operations")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Operations")
            .RequireAuthorization();

        group.MapGet("/", HandleListOperations)
            .WithName("ListOperations")
            .WithSummary("List the operation grounding catalog")
            .Produces<ApiResponse<OperationCatalogSnapshot>>();

        group.MapPost("/{id}/validate", HandleValidateOperation)
            .WithName("ValidateOperation")
            .WithSummary("Validate (plan) an operation without side effects")
            .Accepts<OperationInvokeRequest>("application/json")
            .Produces<ApiResponse<OperationValidation>>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id}/submit", HandleSubmitOperation)
            .WithName("SubmitOperation")
            .WithSummary("Submit an operation through the policy-gated dispatcher")
            .Accepts<OperationInvokeRequest>("application/json")
            .Produces<ApiResponse<OperationHandle>>()
            .Produces(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/handles/{handleId}", HandleGetHandleStatus)
            .WithName("GetOperationHandleStatus")
            .WithSummary("Get the status of a submitted operation handle")
            .Produces<ApiResponse<OperationStatus>>()
            .Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> HandleListOperations(
        HttpContext context,
        IOperationCatalog catalog,
        IEnumerable<IOperationApprovalRequestMapper> requestMappers,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        if (!await OperationAdminAuthorization.IsAuthorizedAsync(
                context,
                context.User,
                OperationSideEffectClass.ReadOnly,
                cancellationToken).ConfigureAwait(false))
        {
            return Results.Forbid();
        }

        var snapshot = await catalog.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var mapperCounts = OperationDescriptorPublication.CountMappings(requestMappers);
        snapshot = snapshot with
        {
            Operations = snapshot.Operations
                .Where(descriptor => OperationDescriptorPublication.CanAdvertise(descriptor, mapperCounts))
                .ToArray(),
        };
        context.Response.Headers.ETag = $"\"{snapshot.CatalogVersion}\"";
        return Results.Json(
            ApiResponse<OperationCatalogSnapshot>.CreateSuccess(snapshot),
            OperationsJsonContext.Default.ApiResponseOperationCatalogSnapshot);
    }

    private static async Task<IResult> HandleValidateOperation(
        HttpContext context,
        string id,
        OperationInvokeRequest request,
        IOperationInvoker invoker,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        if (!await OperationAdminAuthorization.IsAuthorizedAsync(
                context,
                context.User,
                OperationSideEffectClass.ReadOnly,
                cancellationToken).ConfigureAwait(false))
        {
            return Results.Forbid();
        }

        try
        {
            var validation = await invoker
                .ValidateAsync(ToRequest(id, request), cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(
                ApiResponse<OperationValidation>.CreateSuccess(validation),
                OperationsJsonContext.Default.ApiResponseOperationValidation);
        }
        catch (OperationNotFoundException)
        {
            return NotFound(context, $"Operation '{id}' was not found.");
        }
        catch (ArgumentException ex)
        {
            return BadRequest(context, ex.Message);
        }
        catch (ResourceNotFoundException)
        {
            return NotFound(context, "The requested resource was not found.");
        }
    }

    private static async Task<IResult> HandleSubmitOperation(
        HttpContext context,
        string id,
        OperationInvokeRequest request,
        IOperationCatalog catalog,
        IOperationInvoker invoker,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);

        // Preserve the admin-only discovery boundary before resolving the descriptor.
        // The second check below applies the descriptor's semantic side-effect class.
        if (!await OperationAdminAuthorization.IsAuthorizedAsync(
                context,
                context.User,
                OperationSideEffectClass.ReadOnly,
                cancellationToken).ConfigureAwait(false))
        {
            return Results.Forbid();
        }

        var descriptor = await catalog.GetDescriptorAsync(id, cancellationToken).ConfigureAwait(false);
        if (descriptor is null)
        {
            return NotFound(context, $"Operation '{id}' was not found.");
        }

        if (!await OperationAdminAuthorization.IsAuthorizedAsync(
                context,
                context.User,
                descriptor.Policy.SideEffectClass,
                cancellationToken).ConfigureAwait(false))
        {
            return Results.Forbid();
        }

        // Only descriptors that explicitly select the HTTP operator gate enter this
        // approval lane. All operations still pass through the dispatcher policy point;
        // ApprovalModel.None means no operator gate, not a bypass of policy decisions.
        if (descriptor.ApprovalModel == OperationApprovalModel.OperatorGate)
        {
            var gate = context.RequestServices.GetRequiredService<OperatorApprovalGate>();
            var approvalResult = gate.EvaluateApproval(
                context, OperatorResourceType.Catalog, OperatorOperation.Publish);
            if (approvalResult != null)
            {
                return approvalResult;
            }
        }

        try
        {
            // Surface the caller's identity AND role(s) into the policy decision point so a
            // tier/role-aware engine (Phase 4) can decide per descriptor blast-radius. The
            // Community pass-through default ignores them. Tier is left unset here until the
            // tenant-tier resolver lands (deferred — see PR notes).
            var policyContext = new OperationPolicyContext
            {
                PrincipalId = CanonicalSecurityActor.Resolve(context.User)?.ActorId,
                TenantId = context.RequestServices.GetService<ITenantContext>()?.TenantId,
                SchemaName = context.RequestServices.GetService<ISchemaContext>()?.CurrentSchema,
                AuthorizationOutcome = "authorized",
                Roles = context.User.FindAll(ClaimTypes.Role)
                    .Select(claim => claim.Value)
                    .ToArray(),
                ScopeGoverned = OperatorScopeCatalog.IsScopeGoverned(context.User),
                RecognizedScopes = OperatorScopeCatalog.CollectRecognizedScopes(context.User)
                    .OrderBy(scope => scope, StringComparer.Ordinal)
                    .ToArray(),
            };

            var handle = await invoker
                .SubmitAsync(ToRequest(id, request), policyContext, cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(
                ApiResponse<OperationHandle>.CreateSuccess(handle),
                OperationsJsonContext.Default.ApiResponseOperationHandle);
        }
        catch (OperationNotFoundException)
        {
            return NotFound(context, $"Operation '{id}' was not found.");
        }
        catch (ArgumentException ex)
        {
            return BadRequest(context, ex.Message);
        }
        catch (ResourceNotFoundException)
        {
            return NotFound(context, "The requested resource was not found.");
        }
    }

    private static async Task<IResult> HandleGetHandleStatus(
        HttpContext context,
        string handleId,
        IOperationInstanceStore instanceStore,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        if (!await OperationAdminAuthorization.IsAuthorizedAsync(
                context,
                context.User,
                OperationSideEffectClass.ReadOnly,
                cancellationToken).ConfigureAwait(false))
        {
            return Results.Forbid();
        }

        var handle = await instanceStore.GetAsync(handleId, cancellationToken).ConfigureAwait(false);
        if (handle is null)
        {
            return NotFound(context, $"Operation handle '{handleId}' was not found.");
        }

        var status = new OperationStatus
        {
            OperationInstanceId = handle.OperationInstanceId,
            OperationId = handle.OperationId,
            CorrelationId = handle.CorrelationId,
            AuditId = handle.AuditId,
            ProposalId = handle.ProposalId,
            CreatedAt = handle.CreatedAt,
            UpdatedAt = handle.UpdatedAt,
            AuthorizationOutcome = handle.AuthorizationOutcome,
            PolicyDecision = handle.PolicyDecision,
            Status = handle.Status,
            Result = handle.Result,
            JobId = handle.JobId,
            MetadataRevision = handle.MetadataRevision,
            ApprovalLane = handle.ApprovalLane,
            Reason = handle.Reason,
            ResourceIds = handle.ResourceIds,
            EvidenceRefs = handle.EvidenceRefs,
        };

        return Results.Json(
            ApiResponse<OperationStatus>.CreateSuccess(status),
            OperationsJsonContext.Default.ApiResponseOperationStatus);
    }

    private static OperationRequest ToRequest(string operationId, OperationInvokeRequest request)
        => new()
        {
            OperationId = operationId,
            Parameters = request.Parameters,
            ConnectionId = request.ConnectionId,
            ServiceName = request.ServiceName,
            Fields = request.Fields,
            DryRun = request.DryRun
        };

    private static IResult BadRequest(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(
            context,
            StatusCodes.Status400BadRequest,
            ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
            detail);

    private static IResult NotFound(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(
            context,
            StatusCodes.Status404NotFound,
            ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
            detail);

    private static void SetNoStore(HttpContext context)
        => context.Response.Headers.CacheControl = "no-store";
}
