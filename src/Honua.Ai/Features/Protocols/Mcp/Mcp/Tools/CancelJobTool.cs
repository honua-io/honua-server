// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Geoprocessing;
using Honua.Server.Features.Protocols.Mcp.Models;

namespace Honua.Server.Features.Protocols.Mcp.Tools;

/// <summary>
/// MCP server-extension tool that requests cancellation of an in-flight
/// execution job. The authorization and provider-specific cancellation logic
/// lives in <see cref="IGeoprocessingJobService.CancelJobAsync"/>.
/// </summary>
internal sealed class CancelJobTool : IMcpTool
{
    public const string ToolName = "honua_cancel_job";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<CancelJobTool> _logger;

    public CancelJobTool(IGeoprocessingJobService jobService, ILogger<CancelJobTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Description = "Request cancellation of an in-flight geoprocessing job by identifier.",
        InputSchema = McpToolSchemas.CancelJobArgumentSchema
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("CancelJob");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);

        var argument = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.McpCancelJobArgument);
        if (string.IsNullOrWhiteSpace(argument.JobId))
        {
            throw new GeoprocessingValidationException("jobId is required.");
        }

        await _jobService
            .CancelJobAsync(argument.JobId, principal, cancellationToken)
            .ConfigureAwait(false);

        var output = new McpCancelJobOutput
        {
            JobId = argument.JobId,
            Status = "cancellation_requested",
            CancellationRequested = true
        };

        return McpToolHelpers.SuccessResult(output, McpJsonContext.Default.McpCancelJobOutput);
    }
}
