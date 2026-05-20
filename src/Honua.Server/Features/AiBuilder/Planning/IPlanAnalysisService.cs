// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Server.Features.Protocols.Mcp.Models;

namespace Honua.Server.Features.AiBuilder.Planning;

/// <summary>
/// Contract for the service backing the <c>honua_plan_analysis</c> MCP tool.
/// Converts a natural-language intent into one of:
/// <list type="bullet">
///   <item>an executable <see cref="McpAnalysisPlanOutput"/> ready for
///   <c>validate_plan</c>/<c>execute_plan</c>;</item>
///   <item>a canonical <see cref="McpSpecDraftOutput"/> for app-builder
///   workflows that own a spec/plan/apply pipeline of their own;</item>
///   <item>a structured non-success state (clarification required, capability
///   unsupported, oversized estimate, …).</item>
/// </list>
/// </summary>
internal interface IPlanAnalysisService
{
    Task<McpPlanAnalysisOutput> PlanAsync(
        string intent,
        JsonElement? context,
        CancellationToken cancellationToken);
}
