// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AgentWorkspace.Abstractions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Infrastructure.Authentication;

/// <summary>Admits a bound workspace agent to propose service publication, never to execute it.</summary>
internal static class AgentPublicationProposalAuthorization
{
    internal const string Permission = "agent:propose-publication";
    internal const string ApprovalPolicyRef = "agent-workspace.human-approval";

    internal static bool IsHumanApprover(ClaimsPrincipal principal)
        => principal.Identity?.IsAuthenticated == true && !AgentCredentialAuthority.IsAgent(principal)
            && !AgentCredentialAuthority.IsDelegated(principal)
            && (StudioAiInteractivePrincipal.IsInteractive(principal) || principal.Identities.Any(identity =>
                identity.IsAuthenticated && identity.AuthenticationType == OidcAuthenticationExtensions.OperatorBearerScheme));

    internal static async Task<bool> IsAuthorizedAsync(
        HttpContext context,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken,
        IAgentWorkspaceTurnAuthorizer? turnAuthorizer = null)
    {
        if (!AgentCredentialAuthority.IsAgent(principal) || !principal.HasClaim("permission", Permission))
            return false;
        if (!context.RequestServices.GetRequiredService<IOperatorScopeAuthorizer>()
            .Evaluate(principal, OperatorResourceType.Catalog, OperatorOperation.Publish).IsAllowed) return false;
        var workspace = context.Request.Headers["X-Honua-Workspace"];
        var turn = context.Request.Headers["X-Honua-Turn"];
        if (workspace.Count != 1 || turn.Count != 1 || string.IsNullOrWhiteSpace(workspace[0]) || string.IsNullOrWhiteSpace(turn[0]))
            return false;
        if (!await AgentCredentialAuthority.IsCurrentAsync(principal, context.RequestServices, cancellationToken).ConfigureAwait(false))
            return false;
        var authorizer = turnAuthorizer ?? context.RequestServices.GetService<IAgentWorkspaceTurnAuthorizer>();
        // The shared authorizer re-reads membership, tenant, target, release and captured-resource
        // permissions. A request header alone never establishes proposal authority.
        return authorizer is not null && await authorizer.AuthorizeAsync(principal, workspace[0]!, turn[0]!, cancellationToken)
            .ConfigureAwait(false) is not null;
    }
}
