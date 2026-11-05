// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp;

// Studio and canonical encodings of the same principal differ, so both are accepted or the owner is refused.
internal static class McpProposalOwnership
{
    private const string StudioOperationPrefix = "studio.";

    public static bool IsProposer(HttpContext context, ClaimsPrincipal principal, OperationProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(proposal);

        if (string.IsNullOrWhiteSpace(proposal.RequestedBy))
        {
            return false;
        }

        // Resolution is deliberately null-tolerant: a principal the framework cannot
        // name canonically (for example the bootstrap admin key, which carries no
        // api_key_id claim) simply owns nothing. It must not fail the whole read.
        if (Matches(proposal.RequestedBy, CanonicalSecurityActor.Resolve(principal)?.ActorId))
        {
            return true;
        }

        if (proposal.OperationId is not { } operationId
            || !operationId.StartsWith(StudioOperationPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        return Matches(
            proposal.RequestedBy,
            context.RequestServices.GetService<IStudioAuthorizationService>()?.ResolveCallerId(principal));
    }

    private static bool Matches(string requestedBy, string? candidate) =>
        !string.IsNullOrWhiteSpace(candidate)
        && string.Equals(requestedBy, candidate, StringComparison.Ordinal);
}
