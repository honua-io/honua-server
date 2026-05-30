// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Grounding;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// MCP tool that runs a single grounding pass over a natural-language operator
/// goal. Delegates to <see cref="IGroundingService.GroundAsync"/> so the MCP
/// surface shares its classification, ranking, and clarification logic with
/// any future gRPC adapters.
/// </summary>
internal sealed class GroundCandidatesTool : IMcpTool
{
    public const string ToolName = "honua_ground_candidates";

    private static readonly JsonElement InputSchemaElement = GroundingToolSchemas.GroundCandidatesArgumentSchema;

    private readonly IGroundingService _groundingService;
    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<GroundCandidatesTool> _logger;

    public GroundCandidatesTool(
        IGroundingService groundingService,
        IGeoprocessingJobService jobService,
        ILogger<GroundCandidatesTool> logger)
    {
        _groundingService = groundingService;
        _jobService = jobService;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Planning;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Description = "Ground a natural-language goal to a workflow family, ranked catalog candidates, a draft intent, and an optional clarification envelope.",
        InputSchema = InputSchemaElement
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GroundCandidates");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        _jobService.EnsureCallerAuthorized(principal, OperatorResourceType.Catalog, OperatorOperation.Discover);

        var argument = McpToolHelpers.ParseArguments(arguments, GroundingJsonContext.Default.McpGroundCandidatesArgument);
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
