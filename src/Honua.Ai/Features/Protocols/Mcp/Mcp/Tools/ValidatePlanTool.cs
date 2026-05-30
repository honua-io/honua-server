// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// MCP tool that performs structural and policy validation for an analysis plan.
/// Delegates to <see cref="IGeoprocessingJobService.ValidatePlan"/> so the MCP surface
/// shares its decision logic with the gRPC and GPServer adapters.
/// </summary>
internal sealed class ValidatePlanTool : IMcpTool
{
    public const string ToolName = "honua_validate_plan";

    private static readonly JsonElement InputSchemaElement = McpToolSchemas.PlanArgumentSchema;

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<ValidatePlanTool> _logger;

    public ValidatePlanTool(IGeoprocessingJobService jobService, ILogger<ValidatePlanTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Planning;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Description = "Validate an analysis plan for executability, policy gates, and approvals.",
        InputSchema = InputSchemaElement
    };

    public Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("ValidatePlan");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        _jobService.EnsureCallerAuthorized(principal, OperatorResourceType.Process, OperatorOperation.Read);

        var argument = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.McpPlanArgument);
        var plan = McpToolHelpers.ToDomainPlan(argument.Plan);
        var result = _jobService.ValidatePlan(plan, principal);

        var output = new McpValidatePlanOutput
        {
            IsExecutable = result.IsExecutable,
            RequiresApproval = result.RequiresApproval,
            Violations = result.Violations
                .Select(v => new McpValidationViolation
                {
                    Code = v.Code,
                    Message = v.Message,
                    FieldPath = v.FieldPath
                })
                .ToList(),
            Warnings = result.Warnings
        };

        var callResult = McpToolHelpers.SuccessResult(output, McpJsonContext.Default.McpValidatePlanOutput);
        return Task.FromResult(callResult);
    }
}
