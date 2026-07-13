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
        Title = "Dry-run plan",
        Description = "Read-only pre-flight over the same analysis plan object as honua_validate_plan: returns estimatedDurationSeconds (null when estimateAvailable is false — no duration model is wired, so it is not a fabricated 0), estimatedArtifacts (artifact kinds), and sideEffects without executing. Prefer it to gauge a plan's cost and impact; prefer honua_validate_plan to check structural/policy validity. Neither executes the plan — honua_execute_plan does.",
        InputSchema = McpToolSchemas.PlanArgumentSchema,
        OutputSchema = McpToolOutputSchemas.DryRunOutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("Dry-run plan")
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("DryRunPlan");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Process, OperatorOperation.Read, cancellationToken)
            .ConfigureAwait(false);

        var argument = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.McpPlanArgument);
        var plan = McpToolHelpers.ToDomainPlan(argument.Plan);
        var result = _jobService.DryRunPlan(plan, principal);

        var output = new McpDryRunOutput
        {
            // Emit a real number only when the domain reports an estimate is available;
            // otherwise null so the agent sees "no estimate" rather than a fabricated 0 (#2806).
            EstimatedDurationSeconds = result.DurationEstimateAvailable
                ? result.EstimatedDurationSeconds
                : null,
            EstimateAvailable = result.DurationEstimateAvailable,
            EstimatedArtifacts = result.EstimatedArtifacts.Select(a => a.ToString()).ToList(),
            SideEffects = result.SideEffects
        };

        var callResult = McpToolHelpers.SuccessResult(output, McpJsonContext.Default.McpDryRunOutput);
        return callResult;
    }
}
