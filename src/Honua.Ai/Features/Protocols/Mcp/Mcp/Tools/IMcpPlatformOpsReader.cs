// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// Server-implemented adapter for admin-gated platform-release and deploy-operation
/// projections exposed through MCP platform-ops tools.
/// </summary>
internal interface IMcpPlatformOpsReader
{
    /// <summary>Gets the declared platform-release status projection.</summary>
    Task<JsonElement> GetPlatformReleaseStatusAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    /// <summary>Lists deploy operations or fetches one deploy operation by id.</summary>
    Task<JsonElement> GetDeployOperationsAsync(
        ClaimsPrincipal principal,
        McpDeployOperationsArgument argument,
        CancellationToken cancellationToken);

    /// <summary>Proposes a forward deploy to a prior revision as a rollback.</summary>
    Task<McpProposeOperationOutput> ProposeRollbackAsync(
        ClaimsPrincipal principal,
        McpProposeRollbackArgument argument,
        CancellationToken cancellationToken);
}
