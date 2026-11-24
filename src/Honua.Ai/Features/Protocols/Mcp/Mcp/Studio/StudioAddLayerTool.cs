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

internal sealed class AddStudioLayerTool : StudioCompositionToolBase, IMcpTool
{
    public const string ToolName = "honua_studio_add_layer";

    private readonly ILogger<AddStudioLayerTool> _typedLogger;

    public AddStudioLayerTool(IGeoprocessingJobService jobService, ILogger<AddStudioLayerTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Add Studio layer",
        Description = "Add a layer to a map/app/dashboard draft. Set layer.sourceId to studioSourceId from honua_list_layers; layer.id is presentation identity. Needs no sourceBindings or mapSpec export. Duplicate ids or other families fail invalid_argument.",
        InputSchema = StudioMcpSchemas.AddLayerArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        Annotations = McpToolAnnotationSets.Write("Add Studio layer", destructive: false, idempotent: false)
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioAddLayer");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(
                httpContext, OperatorOperation.Create, StudioAuthorizationOperation.UpdateDraft, cancellationToken)
            .ConfigureAwait(false);
        if (await TryResumeUpdateAsync(httpContext, principal, cancellationToken).ConfigureAwait(false) is { } resumed)
        {
            return resumed;
        }

        var lifecycleService = RequireLifecycleService(httpContext);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioAddLayerArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var generation = RequireGeneration(argument.Generation);
        var layer = RequireLayer(argument);

        var updated = await MutateCompositionAsync(
            new CompositionEdit(httpContext, principal, lifecycleService, ToolName, draftId, generation),
            body => StudioCompositionBodyEditor.AddLayer(body, layer, argument.BeforeId),
            cancellationToken).ConfigureAwait(false);

        return updated.ToMcpResult();
    }

    private static StudioCompositionLayer RequireLayer(McpStudioAddLayerArgument argument)
    {
        var layerInput = argument.Layer ?? throw new GeoprocessingValidationException("'layer' is required.");
        if (string.IsNullOrWhiteSpace(layerInput.Id))
        {
            throw new GeoprocessingValidationException("'layer.id' is required.");
        }

        return new StudioCompositionLayer
        {
            Id = layerInput.Id,
            SourceId = layerInput.SourceId,
            Type = layerInput.Type,
            Title = layerInput.Title,
            Visible = layerInput.Visible ?? true,
            StyleRef = layerInput.StyleRef,
            Metadata = layerInput.Metadata,
        };

    }

    internal static long RequireGeneration(long? generation) => generation
        ?? throw new GeoprocessingValidationException("'generation' is required.");
}
