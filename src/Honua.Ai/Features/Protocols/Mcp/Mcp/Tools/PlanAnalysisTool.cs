// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Ai.AiBuilder.Planning;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

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
        Description = "Compile an INTENT into a plan or spec draft when you already know what you want done. Explore first with honua_ground_candidates. An engine of fixture denotes a capability demo using a template. A live engine uses an LLM.",
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

        // Disclose which planner ran: "live" plans are compiled from the
        // intent; "fixture" plans are canned deterministic templates returned when
        // no LLM provider is configured. Sourced from the resolved service seam so
        // it never string-matches an implementation type name.
        output.Engine = _planAnalysisService.Engine;

        // In fixture (demo) mode the returned plan is not compiled from the caller's
        // intent, so a caller relying on it would dead-end. Hand them an explicit
        // next step: read the process catalog and hand-author a plan, then validate it
        // — instead of silently treating the demo template as a tailored plan.
        if (string.Equals(output.Engine, "fixture", StringComparison.Ordinal))
        {
            output.NextSteps ??=
                "Plan analysis is running in fixture (demo) mode, so this plan is a capability demo, not "
                + "compiled from your intent. To proceed with a real analysis, read the process catalog at "
                + $"{McpResourceUris.CatalogProcesses}, hand-author an AnalysisPlan from its process ids, and "
                + $"confirm it with {ValidatePlanTool.ToolName} (and {DryRunPlanTool.ToolName}) before "
                + $"{ExecutePlanTool.ToolName}. This server performs no model inference of its own "
                + "(ADR-0076), so compiling an arbitrary intent into plan steps is your client model's job.";
        }

        return McpToolHelpers.SuccessResult(output, McpJsonContext.Default.McpPlanAnalysisOutput);
    }
}
