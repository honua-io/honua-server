// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;

namespace Honua.Ai.Protocols.Mcp.Studio;

internal sealed class SetStudioMapStyleTool : StudioCompositionToolBase, IMcpTool
{
    public const string ToolName = "honua_studio_set_map_style";

    private readonly ILogger<SetStudioMapStyleTool> _typedLogger;

    public SetStudioMapStyleTool(IGeoprocessingJobService jobService, ILogger<SetStudioMapStyleTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Set Studio map style",
        Description =
            "Replace a map/app/dashboard-family Studio draft's mapSpec with an edited MapLibre v8 style, with "
            + "optimistic-generation checking. Only appearance may change: sources and existing layers' source bindings stay. "
            + "Clears styleRefs and layer style bindings. Fails with invalid_argument otherwise or when no mapSpec exists.",
        InputSchema = StudioMcpSchemas.SetMapStyleArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        Annotations = McpToolAnnotationSets.Write("Set Studio map style", destructive: true, idempotent: true)
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioSetMapStyle");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(
                httpContext, OperatorOperation.Create, StudioAuthorizationOperation.UpdateDraft, cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioSetMapStyleArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var generation = AddStudioLayerTool.RequireGeneration(argument.Generation);
        if (argument.MapSpec.ValueKind != JsonValueKind.Object)
        {
            throw new GeoprocessingValidationException("'mapSpec' is required and must be a JSON object.");
        }

        var updated = await MutateEnvelopeAsync(
            new CompositionEdit(httpContext, principal, lifecycleService, ToolName, draftId, generation),
            envelope => StudioCompositionDocumentEditor.SetMapStyle(envelope, argument.MapSpec),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }
}
