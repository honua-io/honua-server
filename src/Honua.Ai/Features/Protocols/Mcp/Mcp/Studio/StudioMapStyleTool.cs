// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.Studio;

/// <summary>Stores the visual editor's resolved appearance through the canonical draft lifecycle.</summary>
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
        Title = "Set Studio map-local style",
        Description = "Save a resolved MapLibre appearance in this draft under generation checking. "
            + "This map only: converts shared style bindings to a local snapshot and leaves the shared catalog unchanged. "
            + "Explain that impact before choosing full-style editing. Sources, glyphs, sprites and other external asset "
            + "bindings must match the existing authorized mapSpec; initialize that document through the composition workflow first. "
            + "Use honua_studio_set_layer_style with styleBody for a smaller layer-only change. Saving a draft does not publish it.",
        InputSchema = StudioMcpSchemas.SetMapStyleArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        Annotations = McpToolAnnotationSets.Write("Set Studio map-local style", destructive: false, idempotent: true)
    };

    public async Task<McpToolsCallResult> InvokeAsync(HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioSetMapStyle");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);
        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Create,
            StudioAuthorizationOperation.UpdateDraft, cancellationToken).ConfigureAwait(false);
        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioSetMapStyleArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var generation = AddStudioLayerTool.RequireGeneration(argument.Generation);
        var validator = httpContext.RequestServices.GetService<IMapStyleValidator>()
            ?? throw new GeoprocessingValidationException("Full map style editing is unavailable on this server.");
        var sourceAuthorizer = httpContext.RequestServices.GetService<IStudioMapStyleSourceAuthorizer>()
            ?? throw new GeoprocessingValidationException("Map source authorization is unavailable on this server.");
        var updated = await MutateCompositionAsync(httpContext, principal, ToolName,
            RequireLifecycleService(httpContext), draftId, generation, body =>
            {
                var candidate = StudioCompositionBodyEditor.SetMapStyle(body, argument.MapSpec);
                if (!validator.TryValidate(argument.MapSpec, out var error))
                {
                    throw new StudioCompositionBodyException(error ?? "The map style is not supported by this server.");
                }

                return candidate;
            }, cancellationToken, (body, token) => sourceAuthorizer.AuthorizeAsync(principal, body, token)).ConfigureAwait(false);
        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }
}
