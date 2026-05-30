// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// MCP server-extension tool that estimates cost, artifacts, and side effects
/// for a plan without executing it. Delegates to
/// <see cref="IGeoprocessingJobService.DryRunPlan"/> so it stays consistent with
/// gRPC and GPServer dry-run semantics.
/// </summary>
internal sealed class DryRunPlanTool : IMcpTool
{
    public const string ToolName = "honua_dry_run_plan";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<DryRunPlanTool> _logger;

    public DryRunPlanTool(IGeoprocessingJobService jobService, ILogger<DryRunPlanTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Planning;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Description = "Estimate duration, artifact kinds, and side effects for a plan without executing it.",
        InputSchema = McpToolSchemas.PlanArgumentSchema
    };

    public Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("DryRunPlan");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        _jobService.EnsureCallerAuthorized(principal, OperatorResourceType.Process, OperatorOperation.Read);

        var argument = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.McpPlanArgument);
        var plan = McpToolHelpers.ToDomainPlan(argument.Plan);
        var result = _jobService.DryRunPlan(plan, principal);

        var output = new McpDryRunOutput
        {
            EstimatedDurationSeconds = result.EstimatedDurationSeconds,
            EstimatedArtifacts = result.EstimatedArtifacts.Select(a => a.ToString()).ToList(),
            SideEffects = result.SideEffects
        };

        var callResult = McpToolHelpers.SuccessResult(output, McpJsonContext.Default.McpDryRunOutput);
        return Task.FromResult(callResult);
    }
}
