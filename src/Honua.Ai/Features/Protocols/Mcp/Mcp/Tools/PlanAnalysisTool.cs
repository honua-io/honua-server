// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Server.Features.AiBuilder.Planning;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Protocols.Mcp.Models;

namespace Honua.Server.Features.Protocols.Mcp.Tools;

/// <summary>
/// MCP tool that compiles a natural-language intent into a structured analysis
/// plan or canonical spec draft via <see cref="IPlanAnalysisService"/>. The
/// service deliberately decouples the wire surface from the backing engine so
/// fixture replay, deterministic compilation, and live planning can each plug
/// in without the tool growing.
/// </summary>
internal sealed class PlanAnalysisTool : IMcpTool
{
    public const string ToolName = "honua_plan_analysis";

    private static readonly JsonElement InputSchemaElement = McpToolSchemas.PlanAnalysisArgumentSchema;

    private readonly IPlanAnalysisService _planAnalysisService;
    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<PlanAnalysisTool> _logger;

    public PlanAnalysisTool(
        IPlanAnalysisService planAnalysisService,
        IGeoprocessingJobService jobService,
        ILogger<PlanAnalysisTool> logger)
    {
        _planAnalysisService = planAnalysisService;
        _jobService = jobService;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Planning;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Description = "Compile a natural-language intent into an executable analysis plan or canonical spec draft.",
        InputSchema = InputSchemaElement
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("PlanAnalysis");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        _jobService.EnsureCallerAuthorized(principal, OperatorResourceType.Process, OperatorOperation.Read);

        var argument = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.McpPlanAnalysisArgument);
        if (string.IsNullOrWhiteSpace(argument.Intent))
        {
            throw new GeoprocessingValidationException("Plan analysis requires a non-empty intent.");
        }

        var output = await _planAnalysisService
            .PlanAsync(argument.Intent!, argument.Context, cancellationToken)
            .ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(output, McpJsonContext.Default.McpPlanAnalysisOutput);
    }
}
