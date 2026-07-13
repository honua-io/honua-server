// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Ai.AiBuilder.Planning;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

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
        Title = "Plan analysis",
        Description = "Routing: a fully-formed INTENT -> an executable plan; use it when you already know what you want done (contrast honua_ground_candidates, which explores options). Compiles a natural-language intent into an executable analysis plan or canonical spec draft. The output's engine field reports whether the plan was compiled by a live model (\"live\") or replayed from a canned deterministic fixture template (\"fixture\", returned when no LLM provider is configured); an engine:\"fixture\" plan is a capability demo, not compiled from your intent, so do not treat its steps as tailored to your request.",
        InputSchema = InputSchemaElement,
        OutputSchema = McpToolOutputSchemas.PlanAnalysisOutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("Plan analysis")
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("PlanAnalysis");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Process, OperatorOperation.Read, cancellationToken)
            .ConfigureAwait(false);

        var argument = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.McpPlanAnalysisArgument);
        if (string.IsNullOrWhiteSpace(argument.Intent))
        {
            throw new GeoprocessingValidationException("Plan analysis requires a non-empty intent.");
        }

        var output = await _planAnalysisService
            .PlanAsync(argument.Intent!, argument.Context, cancellationToken)
            .ConfigureAwait(false);

        // Disclose which planner ran (#2485): "live" plans are compiled from the
        // intent; "fixture" plans are canned deterministic templates returned when
        // no LLM provider is configured. Sourced from the resolved service seam so
        // it never string-matches an implementation type name.
        output.Engine = _planAnalysisService.Engine;

        // In fixture (demo) mode the returned plan is not compiled from the caller's
        // intent, so a caller relying on it would dead-end. Hand them an explicit
        // next step: read the process catalog and hand-author a plan, then validate it
        // — instead of silently treating the demo template as a tailored plan (#2815).
        if (string.Equals(output.Engine, "fixture", StringComparison.Ordinal))
        {
            output.NextSteps ??=
                "Plan analysis is running in fixture (demo) mode, so this plan is a capability demo, not "
                + "compiled from your intent. To proceed with a real analysis, read the process catalog at "
                + $"{McpResourceUris.CatalogProcesses}, hand-author an AnalysisPlan from its process ids, and "
                + $"confirm it with {ValidatePlanTool.ToolName} (and {DryRunPlanTool.ToolName}) before "
                + $"{ExecutePlanTool.ToolName}. To compile arbitrary intents automatically, enable a live "
                + "planner (see docs/guides/connect/mcp-live-planner.md).";
        }

        return McpToolHelpers.SuccessResult(output, McpJsonContext.Default.McpPlanAnalysisOutput);
    }
}
