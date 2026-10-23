// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AgentWorkspace.Domain;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.Server.Features.AgentWorkspace;
using System.Text.Json;

namespace Honua.Server.Features.Operations;

/// <summary>Captured agent authority; serialized only by the trusted operation preparer.</summary>
internal sealed record WorkspaceAgentPublicationAuthority(
    AgentCredentialSnapshot Credential, AgentWorkspaceTurnAuthorization Turn);

internal static class ServicePublishAgentAuthority
{
    internal const string Parameter = "__agentPublicationAuthority";

    internal static async Task<OperationRequest> PrepareAsync(IServiceProvider services, OperationRequest request,
        OperationPolicyContext context, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(context.ApprovedProposalId))
        {
            await VerifyAsync(services, request, context, cancellationToken).ConfigureAwait(false);
            return request;
        }
        var http = services.GetService<IHttpContextAccessor>()?.HttpContext;
        if (http is null || !AgentCredentialAuthority.IsAgent(http.User))
        {
            if (request.Parameters.ContainsKey(Parameter)) Deny();
            return request;
        }
        if (context.RequiredApprovalPolicyRef != AgentPublicationProposalAuthorization.ApprovalPolicyRef ||
            !await AgentPublicationProposalAuthorization.IsAuthorizedAsync(http, http.User, cancellationToken).ConfigureAwait(false)) Deny();
        var turn = await AgentStudioAuthorization.BindAsync(http, http.User, cancellationToken).ConfigureAwait(false);
        var credential = await AgentCredentialAuthority.CaptureAsync(http.User, services, cancellationToken).ConfigureAwait(false);
        if (turn is null || credential is null || CanonicalSecurityActor.Resolve(http.User)?.ActorId != context.PrincipalId) Deny();
        return request with { Parameters = new Dictionary<string, string?>(request.Parameters, StringComparer.Ordinal)
        {
            [Parameter] = JsonSerializer.Serialize(new WorkspaceAgentPublicationAuthority(credential!, turn!),
                ServicePublishApprovalJsonContext.Default.WorkspaceAgentPublicationAuthority),
        } };
    }

    internal static async Task VerifyAsync(IServiceProvider services, OperationRequest request,
        OperationPolicyContext context, CancellationToken cancellationToken)
    {
        if (!request.Parameters.TryGetValue(Parameter, out var serialized)) return;
        var authority = JsonSerializer.Deserialize(serialized!, ServicePublishApprovalJsonContext.Default.WorkspaceAgentPublicationAuthority);
        var expectedPayload = JsonSerializer.Serialize(ServicePublishApprovalPayload.From(request, context),
            ServicePublishApprovalJsonContext.Default.ServicePublishApprovalPayload);
        var (http, turns) = await AgentPublicationReplay.VerifyAsync(services, request, context,
            authority, expectedPayload, false, cancellationToken).ConfigureAwait(false);
        if (!await AgentPublicationProposalAuthorization.IsAuthorizedAsync(http, http.User, cancellationToken, turns).ConfigureAwait(false)) Deny();
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Deny() => throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
}

internal static class AgentPublicationReplay
{
    internal static bool RequiresHumanApproval(OperationProposal proposal)
    {
        var hasSnapshot = proposal.Plan.ExecutionPayload is { } payload && (proposal.OperationId switch
        {
            ServicePublishOperation.OperationId => JsonSerializer.Deserialize(payload,
                ServicePublishApprovalJsonContext.Default.ServicePublishApprovalPayload)?.Parameters.ContainsKey(ServicePublishAgentAuthority.Parameter) == true,
            StudioDraftOperations.CreatePublicationRequest => JsonSerializer.Deserialize(payload,
                StudioDraftOperationJsonContext.Default.StudioPublicationRequestPayload)?.AgentAuthority is not null,
            _ => false,
        });
        if (proposal.Audit.Reason == AgentPublicationProposalAuthorization.ApprovalPolicyRef &&
            proposal.OperationId is ServicePublishOperation.OperationId or StudioDraftOperations.CreatePublicationRequest && !hasSnapshot)
            throw new InvalidOperationException("This agent publication proposal predates authority sealing; create a new proposal.");
        return hasSnapshot || proposal.Audit.Reason == AgentPublicationProposalAuthorization.ApprovalPolicyRef;
    }

    internal static async Task<(HttpContext Http, AgentWorkspaceTurnAuthorizer Turns)> VerifyAsync(
        IServiceProvider services, OperationRequest request, OperationPolicyContext context,
        WorkspaceAgentPublicationAuthority? authority, string expectedPayload, bool studioActor,
        CancellationToken cancellationToken)
    {
        if (authority is null || string.IsNullOrWhiteSpace(context.ApprovedProposalId) ||
            string.IsNullOrWhiteSpace(context.ApprovedPlanHash)) Deny();
        var proposal = await services.GetRequiredService<IOperationProposalStore>()
            .GetAsync(context.ApprovedProposalId!, cancellationToken).ConfigureAwait(false);
        if (proposal is null || proposal.Status != OperationProposalStatus.Executing ||
            proposal.OperationId != request.OperationId || proposal.RequestedBy != context.PrincipalId ||
            proposal.Audit.OperationInstanceId != context.OperationInstanceId ||
            proposal.SealedPlanHash != context.ApprovedPlanHash ||
            proposal.SealedPlanHash != OperationApprovalPlanSeal.Compute(proposal.Plan) ||
            proposal.Plan.ExecutionPayload != expectedPayload) Deny();
        if (authority!.Turn.TenantId != context.TenantId ||
            authority.Turn.TenantId != services.GetRequiredService<ITenantContext>().TenantId) Deny();
        var principal = await AgentCredentialAuthority.RevalidateAsync(authority.Credential, services, cancellationToken).ConfigureAwait(false);
        if (principal is null || !AgentCredentialAuthority.IsAgent(principal)) Deny();
        var actor = studioActor ? services.GetRequiredService<IStudioAuthorizationService>().ResolveCallerId(principal!)
            : CanonicalSecurityActor.Resolve(principal)?.ActorId;
        if (actor != context.PrincipalId) Deny();
        var http = new DefaultHttpContext { RequestServices = services, User = principal!, RequestAborted = cancellationToken };
        http.Request.Method = HttpMethods.Post;
        http.Request.Headers["X-Honua-Workspace"] = authority.Turn.WorkspaceId;
        http.Request.Headers["X-Honua-Turn"] = authority.Turn.TurnId;
        var turns = new AgentWorkspaceTurnAuthorizer(services.GetRequiredService<IAgentWorkspaceStore>(),
            new PrivateAccessor { HttpContext = http }, services.GetRequiredService<IConfiguration>());
        var binding = await turns.AuthorizeAsync(principal!, authority.Turn.WorkspaceId, authority.Turn.TurnId, cancellationToken).ConfigureAwait(false);
        if (binding != authority.Turn) Deny();
        return (http, turns);
    }

    private sealed class PrivateAccessor : IHttpContextAccessor { public HttpContext? HttpContext { get; set; } }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Deny() => throw new GeoprocessingAuthorizationException(requiresAuthentication: false,
        message: "The original agent authority or human-approved publication seal is no longer valid.");
}

/// <summary>Rechecks the original agent without borrowing the approving human's identity.</summary>
internal static class StudioAgentPublicationReplay
{
    internal static async Task VerifyAsync(IServiceProvider services, OperationRequest request,
        OperationPolicyContext context, StudioPublicationRequestPayload payload, CancellationToken cancellationToken)
    {
        var (http, turns) = await AgentPublicationReplay.VerifyAsync(services, request, context,
            payload.AgentAuthority, request.Parameters[StudioDraftOperations.PayloadParameter]!, true, cancellationToken).ConfigureAwait(false);
        var pointers = await services.GetRequiredService<IStudioPackageLifecycleService>()
            .GetPointersAsync(payload.ItemId, cancellationToken).ConfigureAwait(false);
        if (pointers is null || pointers.CurrentVersionId != payload.VersionId) Deny();
        var decision = await AgentStudioAuthorization.AuthorizeAsync(http, http.User, StudioAuthorizationOperation.PublishRequest,
            pointers!.TenantId, "studio-content-item", payload.ItemId.ToString("D"), cancellationToken, turns).ConfigureAwait(false);
        if (!decision.IsAllowed) Deny();
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Deny() => throw new GeoprocessingAuthorizationException(requiresAuthentication: false,
        message: "The original Studio agent authority or human-approved publication seal is no longer valid.");
}
