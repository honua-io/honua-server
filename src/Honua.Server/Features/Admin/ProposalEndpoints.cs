// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.ControlPlane;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Console;
using Honua.Server.Features.Operations;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Honua.Core.Features.MultiTenancy.Abstractions;
using CanonicalOperationExecutor = Honua.Core.Features.Operations.Abstractions.IOperationExecutor;

namespace Honua.Server.Features.Admin;

internal static class ProposalEndpoints
{
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
            .WithMetadata(AdminHumanApprovalEndpointMetadata.Approve)
            .WithDisplayName("Approve Operation Proposal")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<ProposalDetailResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/{id}/approval-review", HandleOpenApprovalReview)
            .RequireAdminApproveAuthorization()
            .WithMetadata(AdminHumanApprovalEndpointMetadata.Review)
            .WithDisplayName("Open Human Approval Review")
            .ExcludeFromDescription()
            .Produces<ProposalHumanReviewResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
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
                CapabilityUnavailableCodes.ControlPlaneProposalsCapability,
                errorCode: CapabilityUnavailableCodes.EntitlementErrorCode,
                missingEntitlement: CapabilityUnavailableCodes.RedisCacheEntitlement)
            : ProblemDetailsHelpers.CreateCapabilityUnavailableProblem(
                context,
                CapabilityUnavailableCodes.DurableControlPlaneDetail,
                CapabilityUnavailableCodes.RedisDependency,
                CapabilityUnavailableCodes.RedisRemediation,
                CapabilityUnavailableCodes.RedisRemediationRef,
                CapabilityUnavailableCodes.ControlPlaneProposalsCapability);
    }

    private static async Task<IResult> HandleListProposals(
        [FromQuery] string? status,
        [FromQuery] string? kind,
        [FromQuery] string? requestedBy,
        HttpContext context,
        [FromServices] IOperationProposalStore? proposalStore = null,
        [FromServices] ITenantContext? tenantContext = null)
    {
        if (!UnavailableOperationProposalStore.IsDurable(proposalStore))
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
        [FromServices] ITenantContext? tenantContext = null,
        [FromServices] Honua.Core.Features.Operations.Abstractions.IOperationInstanceStore? instanceStore = null)
    {
        if (!UnavailableOperationProposalStore.IsDurable(proposalStore))
        {
            return ControlPlaneUnavailable(context);
        }

        var proposal = await proposalStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
        if (proposal == null || !OperationTenantAuthorization.CanAccess(context, proposal.TenantId)
            || (proposal.Evidence is not null
                && !string.Equals(proposal.Evidence.TenantId, tenantContext?.TenantId, StringComparison.Ordinal)))
            return Results.NotFound();
        var execution = instanceStore is not null && proposal.Audit.OperationInstanceId is { Length: > 0 } instanceId
            ? await instanceStore.GetAsync(instanceId, context.RequestAborted).ConfigureAwait(false)
            : null;
        return Results.Json(ToDetail(proposal, OperationProposalExecutionView.From(proposal, execution)),
            ProposalJsonContext.Default.ProposalDetailResponse);
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
        if (gateway is null || !UnavailableOperationProposalStore.IsDurable(proposalStore))
        {
            return ControlPlaneUnavailable(context);
        }

        var actor = ConsolePrincipal.ResolveActorId(context.User);
        var denied = await EnsureApproverAsync(permissionResolver, proposalStore, id, actor, context)
            .ConfigureAwait(false);
        if (denied != null)
        {
            return denied;
        }

        var proposal = await proposalStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
        if (proposal is null) return Results.NotFound();
        try
        {
            OperationHumanApprovalAuthority.RefuseNonHumanTransport(context);
        }
        catch (HumanApprovalRequiredException exception)
        {
            await OperationApprovalDenialRecorder.RecordAsync(
                context.RequestServices.GetRequiredService<IServiceScopeFactory>(), proposal, actor!,
                OperationApprovalDenialReason.HumanSessionRequired, context.RequestAborted).ConfigureAwait(false);
            return Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status403Forbidden);
        }

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

            var approvalActor = CanonicalSecurityActor.Resolve(context.User)?.ActorId;
            if (approvalActor is null) throw new HumanApprovalRequiredException();
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
        catch (HumanApprovalRequiredException exception)
        {
            return Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (InvalidOperationException ex) when (ex is not ServiceUnavailableException)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> HandleOpenApprovalReview(
        string id, HttpContext context,
        [FromServices] OperationHumanApprovalAuthority authority,
        [FromServices] IOperationProposalStore? proposalStore = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!UnavailableOperationProposalStore.IsDurable(proposalStore)) return ControlPlaneUnavailable(context);
        try
        {
            OperationHumanApprovalAuthority.RefuseNonHumanTransport(context);
            var proposal = await proposalStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
            if (proposal is null || !OperationTenantAuthorization.CanAccess(context, proposal.TenantId)) return Results.NotFound();
            var review = await authority.OpenReviewAsync(proposal, context).ConfigureAwait(false);
            return Results.Json(new ProposalHumanReviewResponse { Proposal = ToDetail(proposal), Review = review },
                ProposalHumanReviewJsonContext.Default.ProposalHumanReviewResponse);
        }
        catch (HumanApprovalRequiredException exception)
        {
            return Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (InvalidOperationException exception) when (exception is not ServiceUnavailableException)
        {
            return Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status409Conflict);
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
        if (gateway is null || !UnavailableOperationProposalStore.IsDurable(proposalStore))
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

        var scopeDenied = EnsureProposalScope(context, proposal);
        if (scopeDenied != null)
        {
            return scopeDenied;
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

        var scopeDenied = EnsureProposalScope(context, proposal);
        if (scopeDenied != null)
        {
            return scopeDenied;
        }

        return null;
    }

    private static IResult? EnsureProposalScope(HttpContext context, OperationProposal proposal) =>
        OperationApprovalAuthorization.PermitsOperationScope(context.User, proposal)
            ? null
            : Results.Problem(
                detail: "The bearer scope does not authorize a decision on this operation proposal.",
                statusCode: StatusCodes.Status403Forbidden);

    private static async Task<IResult?> EnsureApprovePermissionAsync(
        IPermissionResolver permissionResolver,
        string? actor,
        HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Results.Problem(detail: "Unauthenticated.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var authorization = new OperationApprovalAuthorization(
            permissionResolver,
            context.RequestServices.GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationService>());
        return await authorization.CanDecideAsync(context.User, context.RequestAborted).ConfigureAwait(false)
            ? null
            : Results.Problem(
                detail: "Approving an operation proposal requires the 'approve' permission.",
                statusCode: StatusCodes.Status403Forbidden);
    }

    // The filtered list indexes approval records; detail reads include the bound execution outcome.
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

    private static bool RequiresFreshProposal(OperationProposal proposal) =>
        proposal.Status is OperationProposalStatus.Planned or OperationProposalStatus.AwaitingApproval
        && proposal.Initiation is not { Version: OperationInitiationEvidence.CurrentVersion };

    private static ProposalDetailResponse ToDetail(OperationProposal proposal, OperationProposalExecutionView? execution = null) => new()
    {
        ProposalId = proposal.ProposalId,
        Version = proposal.Version,
        PlanHash = proposal.SealedPlanHash,
        Initiation = proposal.Initiation?.Kind.ToString() ?? "Unknown",
        ApprovalChannel = proposal.HumanApproval?.Channel,
        RequiresReproposal = RequiresFreshProposal(proposal),
        ApprovalRemediation = RequiresFreshProposal(proposal)
            ? "Create a fresh proposal with a new idempotency key, then reopen it in Console for human approval."
            : null,
        Kind = proposal.Kind.ToString(),
        Status = execution?.Status ?? proposal.Status.ToString(),
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
        ResolutionReason = execution?.ResolutionReason ?? proposal.ResolutionReason,
        ExecutionOperationId = proposal.ExecutionOperationId,
        CreatedAt = proposal.CreatedAt,
        UpdatedAt = execution?.UpdatedAt ?? proposal.UpdatedAt,
        ResolvedAt = proposal.ResolvedAt,
    };
}
