// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Console;
using Honua.Server.Features.Operations;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Middleware;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Honua.Core.Features.MultiTenancy.Abstractions;
using CanonicalOperationExecutor = Honua.Core.Features.Operations.Abstractions.IOperationExecutor;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Console-facing REST API for the agent-operation approval surface: list pending
/// proposals, inspect a proposal's plan/diff/dry-run/risk, and approve or reject
/// one. Approval is gated by the RBAC <see cref="AuthorizationOperation.Approve"/>
/// grant (distinct from the proposer's grant) and every decision is audited
/// through the gateway (#1694).
/// </summary>
internal static class ProposalEndpoints
{
    // Reserved RBAC scope for operation-approval decisions.
    private const string ApprovalScope = "__operations__";

    public static void MapProposalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/proposals")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Proposals");

        group.MapGet("/", HandleListProposals)
            .RequireAdminAuthorization()
            .WithDisplayName("List Operation Proposals")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ProposalListResponse>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/{id}", HandleGetProposal)
            .RequireAdminAuthorization()
            .WithDisplayName("Get Operation Proposal")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ProposalDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/{id}/approve", HandleApproveProposal)
            .RequireAdminApproveAuthorization()
            .WithDisplayName("Approve Operation Proposal")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<ProposalDetailResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/{id}/reject", HandleRejectProposal)
            .RequireAdminApproveAuthorization()
            .WithDisplayName("Reject Operation Proposal")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<ProposalDetailResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// The durable control plane (<see cref="IOperationProposalStore"/>,
    /// <see cref="IOperationGateway"/>) is composed only when Redis is configured, but these
    /// routes are mapped unconditionally. Before honua-release#202 that combination produced an
    /// untyped <c>500</c> leaking the unresolved DI service name on a Redis-less install; the
    /// approval surface is journey-critical, so it now refuses with the same machine-readable
    /// capability-unavailable receipt the geoprocessing job surfaces emit.
    /// </summary>
    private static IResult ControlPlaneUnavailable(HttpContext context)
    {
        // Report the composition cause, not a blanket "add Redis": on a host where Redis is
        // running but the Pro `caching.redis` entitlement is missing, telling the operator to
        // configure Redis is remediation that cannot work (honua-release#202).
        var substrate = context.RequestServices.GetService<IOptions<DurableJobSubstrateOptions>>()?.Value
            ?? new DurableJobSubstrateOptions();
        var unentitled = substrate.Classify(jobStorePresent: false, jobQueuePresent: false)
            == DurableJobSubstrateCause.RedisNotEntitled;

        return unentitled
            ? ProblemDetailsHelpers.CreateCapabilityUnavailableProblem(
                context,
                CapabilityUnavailableCodes.UnentitledControlPlaneDetail,
                missingDependency: null,
                CapabilityUnavailableCodes.EntitlementRemediation,
                CapabilityUnavailableCodes.EntitlementRemediationRef,
                errorCode: CapabilityUnavailableCodes.EntitlementErrorCode,
                missingEntitlement: CapabilityUnavailableCodes.RedisCacheEntitlement)
            : ProblemDetailsHelpers.CreateCapabilityUnavailableProblem(
                context,
                CapabilityUnavailableCodes.DurableControlPlaneDetail,
                CapabilityUnavailableCodes.RedisDependency,
                CapabilityUnavailableCodes.RedisRemediation,
                CapabilityUnavailableCodes.RedisRemediationRef);
    }

    private static async Task<IResult> HandleListProposals(
        [FromQuery] string? status,
        [FromQuery] string? kind,
        [FromQuery] string? requestedBy,
        HttpContext context,
        [FromServices] IOperationProposalStore? proposalStore = null,
        [FromServices] ITenantContext? tenantContext = null)
    {
        if (proposalStore is null)
        {
            return ControlPlaneUnavailable(context);
        }

        OperationClass? kindFilter = null;
        if (!string.IsNullOrWhiteSpace(kind) && Enum.TryParse<OperationClass>(kind, ignoreCase: true, out var parsedKind))
        {
            kindFilter = parsedKind;
        }

        var proposals = await proposalStore.ListActiveAsync(kindFilter, context.RequestAborted).ConfigureAwait(false);
        var filtered = proposals.Where(proposal =>
        {
            if (!OperationTenantAuthorization.CanAccess(context, proposal.TenantId)
                || (proposal.Evidence is not null
                    && !string.Equals(proposal.Evidence.TenantId, tenantContext?.TenantId, StringComparison.Ordinal)))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(status) &&
                !string.Equals(proposal.Status.ToString(), status, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(requestedBy) &&
                !string.Equals(proposal.RequestedBy, requestedBy, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        });

        var response = new ProposalListResponse
        {
            Proposals = filtered.Select(ToSummary).ToArray()
        };

        return Results.Json(response, ProposalJsonContext.Default.ProposalListResponse);
    }

    private static async Task<IResult> HandleGetProposal(
        string id,
        HttpContext context,
        [FromServices] IOperationProposalStore? proposalStore = null,
        [FromServices] ITenantContext? tenantContext = null)
    {
        if (proposalStore is null)
        {
            return ControlPlaneUnavailable(context);
        }

        var proposal = await proposalStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
        return proposal == null || !OperationTenantAuthorization.CanAccess(context, proposal.TenantId)
            || (proposal.Evidence is not null
                && !string.Equals(proposal.Evidence.TenantId, tenantContext?.TenantId, StringComparison.Ordinal))
            ? Results.NotFound()
            : Results.Json(ToDetail(proposal), ProposalJsonContext.Default.ProposalDetailResponse);
    }

    private static async Task<IResult> HandleApproveProposal(
        string id,
        [FromServices] IPermissionResolver permissionResolver,
        HttpContext context,
        [FromServices] IOperationGateway? gateway = null,
        [FromServices] IOperationProposalStore? proposalStore = null,
        [FromServices] IEnumerable<CanonicalOperationExecutor>? operationExecutors = null,
        [FromServices] ITenantContext? tenantContext = null)
    {
        if (gateway is null || proposalStore is null)
        {
            return ControlPlaneUnavailable(context);
        }

        var actor = ConsolePrincipal.ResolveActorId(context.User);
        var denied = await EnsureApproverAsync(permissionResolver, proposalStore, id, actor, context).ConfigureAwait(false);
        if (denied != null)
        {
            return denied;
        }

        var proposal = await proposalStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
        var operationId = proposal?.OperationId ?? (proposal is null ? null : LegacyOperationIds.For(proposal.Kind));
        if (proposal != null &&
            (operationExecutors is null || !operationExecutors.Any(executor =>
                string.Equals(executor.OperationId, operationId, StringComparison.Ordinal))))
        {
            return Results.Problem(
                title: "Operation executor unavailable",
                detail: $"No operation executor is registered for proposal type '{proposal.Kind}' " +
                    $"(operation '{operationId}'). " +
                    "Register an executor for this operation before approving the proposal.",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "operation-executor-unavailable",
                    ["operationType"] = proposal.Kind.ToString(),
                    ["operationId"] = operationId,
                });
        }

        try
        {
            if (proposal?.Evidence is not null && string.IsNullOrWhiteSpace(tenantContext?.TenantId))
            {
                return Results.Problem(
                    detail: "A resolved tenant is required to approve this proposal.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            // Preserve the established actor identity for legacy proposals. Evidence-bound
            // proposals use the same scheme-qualified actor stamped at the MCP boundary so
            // separation-of-duties and credential revocation revalidation compare like-for-like.
            var approvalActor = proposal?.Evidence is null
                ? actor!
                : AuditContextResolver.ResolveActor(context, out _);
            var resolved = await gateway.ApplyApprovedProposalAsync(
                id,
                new OperationProposalApprovalContext
                {
                    ApprovedBy = approvalActor,
                    TenantId = tenantContext?.TenantId ?? string.Empty,
                },
                context.RequestAborted)
                .ConfigureAwait(false);
            return resolved == null
                ? Results.NotFound()
                : Results.Json(ToDetail(resolved), ProposalJsonContext.Default.ProposalDetailResponse);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> HandleRejectProposal(
        string id,
        [FromBody] RejectProposalRequest? request,
        [FromServices] IPermissionResolver permissionResolver,
        HttpContext context,
        [FromServices] IOperationGateway? gateway = null,
        [FromServices] IOperationProposalStore? proposalStore = null,
        [FromServices] ITenantContext? tenantContext = null)
    {
        if (gateway is null || proposalStore is null)
        {
            return ControlPlaneUnavailable(context);
        }

        if (string.IsNullOrWhiteSpace(request?.Reason))
        {
            return Results.Problem(detail: "A rejection reason is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = ConsolePrincipal.ResolveActorId(context.User);
        var denied = await EnsureApprovePermissionAsync(permissionResolver, actor, context).ConfigureAwait(false);
        if (denied != null)
        {
            return denied;
        }

        var proposal = await proposalStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
        if (proposal is null || !OperationTenantAuthorization.CanAccess(context, proposal.TenantId)
            || (proposal.Evidence is not null
                && !string.Equals(proposal.Evidence.TenantId, tenantContext?.TenantId, StringComparison.Ordinal)))
        {
            return Results.NotFound();
        }

        try
        {
            var resolved = await gateway.RejectProposalAsync(id, actor!, request.Reason, context.RequestAborted)
                .ConfigureAwait(false);
            return resolved == null
                ? Results.NotFound()
                : Results.Json(ToDetail(resolved), ProposalJsonContext.Default.ProposalDetailResponse);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult?> EnsureApproverAsync(
        IPermissionResolver permissionResolver,
        IOperationProposalStore proposalStore,
        string proposalId,
        string? actor,
        HttpContext context)
    {
        var denied = await EnsureApprovePermissionAsync(permissionResolver, actor, context).ConfigureAwait(false);
        if (denied != null)
        {
            return denied;
        }

        var proposal = await proposalStore.GetAsync(proposalId, context.RequestAborted).ConfigureAwait(false);
        if (proposal is null || !OperationTenantAuthorization.CanAccess(context, proposal.TenantId))
        {
            return Results.NotFound();
        }

        // Separation of duties: the proposer cannot approve their own proposal.
        if (proposal != null &&
            !string.IsNullOrWhiteSpace(actor) &&
            string.Equals(proposal.RequestedBy, actor, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(
                detail: "Separation of duties: the requester of a proposal cannot approve it.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }

    private static async Task<IResult?> EnsureApprovePermissionAsync(
        IPermissionResolver permissionResolver,
        string? actor,
        HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Results.Problem(detail: "Unauthenticated.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var roles = context.User.FindAll(System.Security.Claims.ClaimTypes.Role)
            .Select(claim => claim.Value)
            .ToArray();

        var decision = await permissionResolver.AuthorizeAsync(
                actor,
                roles,
                ApprovalScope,
                layer: null,
                AuthorizationOperation.Approve,
                isAuthenticated: true,
                context.RequestAborted)
            .ConfigureAwait(false);

        // Allow when an explicit 'approve' grant matches. When no per-operation
        // grant is configured for the principal's roles the resolver reports
        // NoMatchingGrant; per the shared RBAC seam's documented fallback, defer
        // to the coarse admin-policy gate already enforced on this route group
        // (RequireAdminAuthorization) rather than hard-denying. Only an explicitly
        // denied/anonymous principal is forbidden here.
        return decision.IsAllowed || decision.HasNoMatchingGrant
            ? null
            : Results.Problem(
                detail: "Approving an operation proposal requires the 'approve' permission.",
                statusCode: StatusCodes.Status403Forbidden);
    }

    private static ProposalSummaryResponse ToSummary(OperationProposal proposal) => new()
    {
        ProposalId = proposal.ProposalId,
        Kind = proposal.Kind.ToString(),
        Status = proposal.Status.ToString(),
        RequestedBy = proposal.RequestedBy,
        RequestedByAgent = proposal.RequestedByAgent,
        FindingId = proposal.AutonomyMetadata?.FindingId,
        AutonomyRule = proposal.AutonomyMetadata?.Rule,
        ActionDiscriminator = proposal.AutonomyMetadata?.ActionDiscriminator,
        Summary = proposal.Plan.Summary,
        RiskLevel = proposal.Plan.RiskLevel.ToString(),
        CreatedAt = proposal.CreatedAt,
        UpdatedAt = proposal.UpdatedAt,
    };

    private static ProposalDetailResponse ToDetail(OperationProposal proposal) => new()
    {
        ProposalId = proposal.ProposalId,
        Kind = proposal.Kind.ToString(),
        Status = proposal.Status.ToString(),
        RequestedBy = proposal.RequestedBy,
        RequestedByAgent = proposal.RequestedByAgent,
        FindingId = proposal.AutonomyMetadata?.FindingId,
        AutonomyRule = proposal.AutonomyMetadata?.Rule,
        ActionDiscriminator = proposal.AutonomyMetadata?.ActionDiscriminator,
        Summary = proposal.Plan.Summary,
        Diff = proposal.Plan.Diff,
        DryRun = proposal.Plan.DryRun,
        RiskLevel = proposal.Plan.RiskLevel.ToString(),
        BlockingReasons = proposal.Plan.BlockingReasons,
        Warnings = proposal.Plan.Warnings,
        GuardrailTier = proposal.GuardrailDecision?.Tier.ToString(),
        ResolvedBy = proposal.ResolvedBy,
        ResolutionReason = proposal.ResolutionReason,
        ExecutionOperationId = proposal.ExecutionOperationId,
        CreatedAt = proposal.CreatedAt,
        UpdatedAt = proposal.UpdatedAt,
        ResolvedAt = proposal.ResolvedAt,
    };
}
