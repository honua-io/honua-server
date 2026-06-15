// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Console;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Mvc;

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
            .WithTags("Admin", "Proposals")
            .RequireAdminAuthorization();

        group.MapGet("/", HandleListProposals)
            .WithDisplayName("List Operation Proposals")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ProposalListResponse>();

        group.MapGet("/{id}", HandleGetProposal)
            .WithDisplayName("Get Operation Proposal")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ProposalDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id}/approve", HandleApproveProposal)
            .WithDisplayName("Approve Operation Proposal")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<ProposalDetailResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id}/reject", HandleRejectProposal)
            .WithDisplayName("Reject Operation Proposal")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<ProposalDetailResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> HandleListProposals(
        [FromServices] IOperationProposalStore proposalStore,
        [FromQuery] string? status,
        [FromQuery] string? kind,
        [FromQuery] string? requestedBy,
        HttpContext context)
    {
        OperationClass? kindFilter = null;
        if (!string.IsNullOrWhiteSpace(kind) && Enum.TryParse<OperationClass>(kind, ignoreCase: true, out var parsedKind))
        {
            kindFilter = parsedKind;
        }

        var proposals = await proposalStore.ListActiveAsync(kindFilter, context.RequestAborted).ConfigureAwait(false);
        var filtered = proposals.Where(proposal =>
        {
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
        [FromServices] IOperationProposalStore proposalStore,
        HttpContext context)
    {
        var proposal = await proposalStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
        return proposal == null
            ? Results.NotFound()
            : Results.Json(ToDetail(proposal), ProposalJsonContext.Default.ProposalDetailResponse);
    }

    private static async Task<IResult> HandleApproveProposal(
        string id,
        [FromServices] IOperationGateway gateway,
        [FromServices] IPermissionResolver permissionResolver,
        [FromServices] IOperationProposalStore proposalStore,
        HttpContext context)
    {
        var actor = ConsolePrincipal.ResolveActorId(context.User);
        var denied = await EnsureApproverAsync(permissionResolver, proposalStore, id, actor, context).ConfigureAwait(false);
        if (denied != null)
        {
            return denied;
        }

        try
        {
            var resolved = await gateway.ApplyApprovedProposalAsync(id, actor!, context.RequestAborted)
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
        [FromServices] IOperationGateway gateway,
        [FromServices] IPermissionResolver permissionResolver,
        HttpContext context)
    {
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

        // Separation of duties: the proposer cannot approve their own proposal.
        var proposal = await proposalStore.GetAsync(proposalId, context.RequestAborted).ConfigureAwait(false);
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
