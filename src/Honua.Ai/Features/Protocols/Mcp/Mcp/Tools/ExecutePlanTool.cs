// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Geoprocessing;
using Honua.Server.Features.Protocols.Mcp.Models;

namespace Honua.Server.Features.Protocols.Mcp.Tools;

/// <summary>
/// MCP tool that submits an analysis plan for asynchronous execution. Delegates
/// to <see cref="IGeoprocessingJobService.SubmitJobAsync"/> and returns the job
/// identifier along with the <c>honua://jobs/{jobId}</c> resource URI so clients
/// can poll lifecycle state via <c>resources/read</c>.
/// </summary>
internal sealed class ExecutePlanTool : IMcpTool
{
    public const string ToolName = "honua_execute_plan";

    private const string ProtocolMetadataSource = "honua.mcp";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<ExecutePlanTool> _logger;

    public ExecutePlanTool(IGeoprocessingJobService jobService, ILogger<ExecutePlanTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Description = "Submit an analysis plan for asynchronous execution and return the job identifier and resource URI.",
        InputSchema = McpToolSchemas.ExecutePlanArgumentSchema
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("ExecutePlan");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        _jobService.EnsureCallerAuthorized(principal, OperatorResourceType.Process, OperatorOperation.Execute);

        var argument = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.McpExecutePlanArgument);
        var plan = McpToolHelpers.ToDomainPlan(argument.Plan);
        var idempotencyKey = string.IsNullOrWhiteSpace(argument.IdempotencyKey)
            ? null
            : argument.IdempotencyKey;

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["submittedVia"] = ProtocolMetadataSource
        };

        var jobRecord = await _jobService
            .SubmitJobAsync(plan, idempotencyKey, principal, metadata, cancellationToken)
            .ConfigureAwait(false);

        var output = new McpExecuteOutput
        {
            JobId = jobRecord.OperationId,
            Status = jobRecord.Status.ToString(),
            CreatedAt = jobRecord.CreatedAt,
            ResourceUri = McpResourceUris.JobUri(jobRecord.OperationId)
        };

        return McpToolHelpers.SuccessResult(output, McpJsonContext.Default.McpExecuteOutput);
    }
}
