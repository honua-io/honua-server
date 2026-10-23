// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Decides whether the caller of an MCP request is the principal that proposed a
/// durable operation. Every candidate identity is derived by the framework from the
/// authenticated principal, never read from the request body or a header, so a match
/// can only ever mean "this proposal records an id that names this caller".
/// </summary>
/// <remarks>
/// Two write paths record a proposer id, and they resolve it with different rules
/// (honua-server#4910). Canonical-actor call sites -- MCP <c>save_version</c>,
/// <c>reopen_version</c>, the publish tools -- record
/// <see cref="CanonicalSecurityActor"/>'s scheme-qualified actor id. The Studio
/// mutation surfaces (MCP <c>propose_publication</c>, <c>create_draft</c>,
/// <c>update_draft</c> and their REST twins) record the Studio owner key from
/// <see cref="IStudioAuthorizationService.ResolveCallerId"/>, which for an
/// issuer-bearing subject is <c>subject:{iss}:{sub}@tenant:{tenant}</c> and for an
/// API key is the bare key id. For every principal that carries an issuer or an API
/// key the two encodings differ, so comparing a Studio-recorded proposer against the
/// canonical id alone refused the proposal's own owner. Both encodings of the SAME
/// principal are accepted here; the Studio owner key is only consulted for Studio
/// operations, which are the only operations that record it.
/// </remarks>
internal static class McpProposalOwnership
{
    private const string StudioOperationPrefix = "studio.";

    /// <summary>
    /// Returns whether <paramref name="principal"/> is the recorded proposer of
    /// <paramref name="proposal"/>.
    /// </summary>
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
