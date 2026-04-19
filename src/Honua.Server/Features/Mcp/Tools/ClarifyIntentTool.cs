// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Server.Features.Grounding.Mcp;
using Honua.Server.Features.Mcp.Models;

namespace Honua.Server.Features.Mcp.Tools;

/// <summary>
/// MCP tool that resolves a clarification turn by re-running grounding with
/// the caller's responses merged into the request. The service is stateless —
/// the caller carries goal text, constraints, and explicit inputs across
/// turns — so this tool adds the <c>intentId</c> and <c>response</c>
/// requirements on top of <c>honua_ground_candidates</c>.
/// </summary>
internal sealed class ClarifyIntentTool : IMcpTool
{
    public const string ToolName = "honua_clarify_intent";

    private static readonly JsonElement InputSchemaElement = GroundingToolSchemas.ClarifyIntentArgumentSchema;

    private readonly IGroundingService _groundingService;
    private readonly ILogger<ClarifyIntentTool> _logger;

    public ClarifyIntentTool(IGroundingService groundingService, ILogger<ClarifyIntentTool> logger)
    {
        _groundingService = groundingService;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Planning;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Description = "Continue a grounding pass with operator clarification answers; returns an updated draft intent and (if ambiguity remains) another clarification envelope.",
        InputSchema = InputSchemaElement
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("ClarifyIntent");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);

        var argument = McpToolHelpers.ParseArguments(arguments, GroundingJsonContext.Default.McpClarifyIntentArgument);
        var request = GroundingToolMapper.ToDomain(argument);

        var result = await _groundingService.GroundAsync(request, principal, cancellationToken).ConfigureAwait(false);
        var output = GroundingToolMapper.ToWire(result);

        McpTelemetry.RecordGroundingResult(
            engine: result.Engine,
            workflowFamily: result.WorkflowFamily.Value.ToString(),
            clarified: result.Clarification is not null);

        return McpToolHelpers.SuccessResult(output, GroundingJsonContext.Default.McpGroundingOutput);
    }
}
