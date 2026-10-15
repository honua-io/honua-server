// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// Server-side seam behind the job diagnosis MCP tools (honua-server#6219). The
/// composition root implements it over the same admin job diagnostics projection
/// that <c>GET /api/v1/admin/jobs/{jobId}/diagnostics</c> returns, so an agent and
/// an operator read identical evidence under identical authorization.
/// </summary>
internal interface IMcpJobDiagnosticsReader
{
    /// <summary>
    /// Diagnose one job: observed facts (recorded timestamps, heartbeat, failure
    /// message, structured error, release identity, artifacts) separated from
    /// inferred explanations (canonical phase, liveness verdict, failure
    /// classification, recovery advice, retry disposition), plus the evidence gaps.
    /// </summary>
    Task<JsonElement> DiagnoseJobAsync(
        ClaimsPrincipal principal,
        McpDiagnoseJobArgument argument,
        CancellationToken cancellationToken);

    /// <summary>
    /// Retrieve the bounded evidence behind a diagnosis: sanitized steps, the most
    /// recent events (bounded by <see cref="McpJobEvidenceArgument.Events"/>) and
    /// artifact availability, without the inferred layer.
    /// </summary>
    Task<JsonElement> GetJobEvidenceAsync(
        ClaimsPrincipal principal,
        McpJobEvidenceArgument argument,
        CancellationToken cancellationToken);
}
