// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AgentWorkspace.Domain;

namespace Honua.Core.Features.AgentWorkspace.Abstractions;

/// <summary>Checks an actual agent principal against explicit workspace membership and a captured turn.</summary>
public interface IAgentWorkspaceTurnAuthorizer
{
    /// <summary>
    /// Returns the immutable authorized binding after rechecking tenant, target and resource authority;
    /// returns null for missing membership, revoked grants, unknown turns or unsupported signing policy.
    /// </summary>
    Task<AgentWorkspaceTurnAuthorization?> AuthorizeAsync(
        ClaimsPrincipal principal,
        string workspaceId,
        string turnId,
        CancellationToken cancellationToken = default);
}
