// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// Server-implemented reader for admin-gated operational observability
/// projections exposed through read-only MCP tools and resources.
/// </summary>
internal interface IMcpOpsObservabilityReader
{
    /// <summary>Gets the consolidated operational-health snapshot.</summary>
    Task<JsonElement> GetOpsHealthAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    /// <summary>Lists or fetches deterministic ops findings.</summary>
    Task<JsonElement> GetOpsFindingsAsync(
        ClaimsPrincipal principal,
        McpOpsFindingsArgument argument,
        CancellationToken cancellationToken);

    /// <summary>Lists alert events from the admin observability alert surface.</summary>
    Task<JsonElement> ListAlertEventsAsync(
        ClaimsPrincipal principal,
        McpAlertEventsArgument argument,
        CancellationToken cancellationToken);

    /// <summary>Lists normalized Operate timeline events.</summary>
    Task<JsonElement> ListOperateEventsAsync(
        ClaimsPrincipal principal,
        McpOperateEventsArgument argument,
        CancellationToken cancellationToken);
}
