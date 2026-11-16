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
        var lifecycleService = RequireLifecycleService(httpContext);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioAddLayerArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var generation = RequireGeneration(argument.Generation);
        var layerInput = argument.Layer ?? throw new GeoprocessingValidationException("'layer' is required.");
        if (string.IsNullOrWhiteSpace(layerInput.Id))
        {
            throw new GeoprocessingValidationException("'layer.id' is required.");
        }

        var layer = new StudioCompositionLayer
        {
            Id = layerInput.Id,
            SourceId = layerInput.SourceId,
            Type = layerInput.Type,
            Title = layerInput.Title,
            Visible = layerInput.Visible ?? true,
            StyleRef = layerInput.StyleRef,
            Metadata = layerInput.Metadata,
        };

        var updated = await MutateCompositionAsync(
            new CompositionEdit(httpContext, principal, lifecycleService, ToolName, draftId, generation),
            body => StudioCompositionBodyEditor.AddLayer(body, layer, argument.BeforeId),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }

    internal static long RequireGeneration(long? generation) => generation
        ?? throw new GeoprocessingValidationException("'generation' is required.");
}

internal sealed class RemoveStudioLayerTool : StudioCompositionToolBase, IMcpTool
{
    public const string ToolName = "honua_studio_remove_layer";

    private readonly ILogger<RemoveStudioLayerTool> _typedLogger;

    public RemoveStudioLayerTool(IGeoprocessingJobService jobService, ILogger<RemoveStudioLayerTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Remove Studio layer",
        Description = "Remove a map/app/dashboard draft layer by id. Prune bindings, inline style and mapSpec entries only it used; unknown ids fail not_found.",
        InputSchema = StudioMcpSchemas.RemoveLayerArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        // Destructive: it removes composed state. Undo is a fresh
        // honua_studio_add_layer call or a draft revision reopen — not this tool.
        Annotations = McpToolAnnotationSets.Write("Remove Studio layer", destructive: true, idempotent: false)
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioRemoveLayer");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(
                httpContext, OperatorOperation.Create, StudioAuthorizationOperation.UpdateDraft, cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioRemoveLayerArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var generation = AddStudioLayerTool.RequireGeneration(argument.Generation);
        if (string.IsNullOrWhiteSpace(argument.LayerId))
        {
            throw new GeoprocessingValidationException("'layerId' is required.");
        }

        var updated = await MutateEnvelopeAsync(
            new CompositionEdit(httpContext, principal, lifecycleService, ToolName, draftId, generation),
            envelope => StudioCompositionDocumentEditor.RemoveLayer(envelope, argument.LayerId!),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }
}

internal sealed class SetStudioLayerStyleTool : StudioCompositionToolBase, IMcpTool
{
    public const string ToolName = "honua_studio_set_layer_style";

    private readonly ILogger<SetStudioLayerStyleTool> _typedLogger;

    public SetStudioLayerStyleTool(IGeoprocessingJobService jobService, ILogger<SetStudioLayerStyleTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Set Studio layer style",
        Description = "Set a composed layer's styleRef; omit or null to clear. styleBody stores inline style. Requires a map/app/dashboard draft. Unknown ids fail not_found; style references are not catalog-checked.",
        InputSchema = StudioMcpSchemas.SetLayerStyleArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        // Re-pointing a style binding is non-destructive; re-applying the same
        // styleRef is idempotent.
        Annotations = McpToolAnnotationSets.Write("Set Studio layer style", destructive: false, idempotent: true)
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioSetLayerStyle");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(
                httpContext, OperatorOperation.Create, StudioAuthorizationOperation.UpdateDraft, cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioSetLayerStyleArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var generation = AddStudioLayerTool.RequireGeneration(argument.Generation);
        if (string.IsNullOrWhiteSpace(argument.LayerId))
        {
            throw new GeoprocessingValidationException("'layerId' is required.");
        }

        var edit = new CompositionEdit(httpContext, principal, lifecycleService, ToolName, draftId, generation);
        var updated = RequestsInlineStyle(argument)
            ? await MutateEnvelopeAsync(
                edit,
                envelope => StudioCompositionDocumentEditor.SetLayerInlineStyle(envelope, argument.LayerId!, argument.StyleBody),
                cancellationToken).ConfigureAwait(false)
            : await MutateCompositionAsync(
                edit,
                body => StudioCompositionBodyEditor.SetLayerStyleRef(body, argument.LayerId!, argument.StyleRef),
                cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }

    private static bool RequestsInlineStyle(McpStudioSetLayerStyleArgument argument)
    {
        if (argument.StyleBody.ValueKind is JsonValueKind.Undefined)
        {
            return false;
        }

        if (argument.StyleRef is not null)
        {
            throw new GeoprocessingValidationException("Inline styling cannot also select a shared style.");
        }

        return true;
    }
}

internal sealed class SetStudioLayerVisibilityTool : StudioCompositionToolBase, IMcpTool
{
    public const string ToolName = "honua_studio_set_layer_visibility";

    private readonly ILogger<SetStudioLayerVisibilityTool> _typedLogger;

    public SetStudioLayerVisibilityTool(IGeoprocessingJobService jobService, ILogger<SetStudioLayerVisibilityTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Set Studio layer visibility",
        Description = "Persist a map/app/dashboard layer's visibility. Draft sync overwrites client-local toggles, not this. Unknown ids fail not_found.",
        InputSchema = StudioMcpSchemas.SetLayerVisibilityArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        // Hiding a layer removes no composed state (the layer, its source and its style binding
        // all survive), and re-applying the same visibility is idempotent.
        Annotations = McpToolAnnotationSets.Write("Set Studio layer visibility", destructive: false, idempotent: true)
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioSetLayerVisibility");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(
                httpContext, OperatorOperation.Create, StudioAuthorizationOperation.UpdateDraft, cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);

        var argument = McpToolHelpers.ParseArguments(
            arguments, StudioMcpJsonContext.Default.McpStudioSetLayerVisibilityArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var generation = AddStudioLayerTool.RequireGeneration(argument.Generation);
        if (string.IsNullOrWhiteSpace(argument.LayerId))
        {
            throw new GeoprocessingValidationException("'layerId' is required.");
        }

        // MCP dispatch does not evaluate the advertised inputSchema, so the required 'visible'
        // is enforced HERE. Defaulting an omitted value would silently show or hide a layer the
        // caller never named.
        var visible = argument.Visible
            ?? throw new GeoprocessingValidationException("'visible' is required and must be a JSON boolean.");

        var updated = await MutateCompositionAsync(
            new CompositionEdit(httpContext, principal, lifecycleService, ToolName, draftId, generation),
            body => StudioCompositionBodyEditor.SetLayerVisibility(body, argument.LayerId!, visible),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }
}

internal sealed class SetStudioViewTool : StudioCompositionToolBase, IMcpTool
{
    public const string ToolName = "honua_studio_set_view";

    private readonly ILogger<SetStudioViewTool> _typedLogger;

    public SetStudioViewTool(IGeoprocessingJobService jobService, ILogger<SetStudioViewTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Set Studio view",
        Description = "Update bbox, center, zoom, pitch, bearing or crs in a map/app/dashboard draft. Omitted values stay; out-of-range camera settings fail invalid_argument.",
        InputSchema = StudioMcpSchemas.SetViewArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        Annotations = McpToolAnnotationSets.Write("Set Studio view", destructive: false, idempotent: true)
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioSetView");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(
                httpContext, OperatorOperation.Create, StudioAuthorizationOperation.UpdateDraft, cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioSetViewArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var generation = AddStudioLayerTool.RequireGeneration(argument.Generation);
        var viewInput = argument.View ?? throw new GeoprocessingValidationException("'view' is required.");

        var view = new StudioCompositionView
        {
            Bbox = viewInput.Bbox,
            Center = viewInput.Center,
            Zoom = viewInput.Zoom,
            Pitch = viewInput.Pitch,
            Bearing = viewInput.Bearing,
            Crs = viewInput.Crs,
        };

        var updated = await MutateCompositionAsync(
            new CompositionEdit(httpContext, principal, lifecycleService, ToolName, draftId, generation),
            body => StudioCompositionViewEditor.MergeView(body, view),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }
}

internal sealed class AddStudioWidgetTool : StudioCompositionToolBase, IMcpTool
{
    public const string ToolName = "honua_studio_add_widget";

    private readonly ILogger<AddStudioWidgetTool> _typedLogger;

    public AddStudioWidgetTool(IGeoprocessingJobService jobService, ILogger<AddStudioWidgetTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Add Studio widget",
        Description = "Add a widget to a map/app/dashboard draft. Duplicate ids and other families fail invalid_argument.",
        InputSchema = StudioMcpSchemas.AddWidgetArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        Annotations = McpToolAnnotationSets.Write("Add Studio widget", destructive: false, idempotent: false)
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioAddWidget");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(
                httpContext, OperatorOperation.Create, StudioAuthorizationOperation.UpdateDraft, cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioAddWidgetArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var generation = AddStudioLayerTool.RequireGeneration(argument.Generation);
        var widgetInput = argument.Widget ?? throw new GeoprocessingValidationException("'widget' is required.");
        if (string.IsNullOrWhiteSpace(widgetInput.Id))
        {
            throw new GeoprocessingValidationException("'widget.id' is required.");
        }

        if (string.IsNullOrWhiteSpace(widgetInput.Kind))
        {
            throw new GeoprocessingValidationException("'widget.kind' is required.");
        }

        var widget = new StudioCompositionWidget
        {
            Id = widgetInput.Id,
            Kind = widgetInput.Kind,
            Title = widgetInput.Title,
            SourceId = widgetInput.SourceId,
            Config = widgetInput.Config,
        };

        var updated = await MutateCompositionAsync(
            new CompositionEdit(httpContext, principal, lifecycleService, ToolName, draftId, generation),
            body => StudioCompositionBodyEditor.AddWidget(body, widget),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }
}

internal sealed class RemoveStudioWidgetTool : StudioCompositionToolBase, IMcpTool
{
    public const string ToolName = "honua_studio_remove_widget";

    private readonly ILogger<RemoveStudioWidgetTool> _typedLogger;

    public RemoveStudioWidgetTool(IGeoprocessingJobService jobService, ILogger<RemoveStudioWidgetTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Remove Studio widget",
        Description = "Remove a map/app/dashboard draft widget by id; unknown ids fail not_found.",
        InputSchema = StudioMcpSchemas.RemoveWidgetArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        Annotations = McpToolAnnotationSets.Write("Remove Studio widget", destructive: true, idempotent: false)
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioRemoveWidget");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(
                httpContext, OperatorOperation.Create, StudioAuthorizationOperation.UpdateDraft, cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioRemoveWidgetArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var generation = AddStudioLayerTool.RequireGeneration(argument.Generation);
        if (string.IsNullOrWhiteSpace(argument.WidgetId))
        {
            throw new GeoprocessingValidationException("'widgetId' is required.");
        }

        var updated = await MutateCompositionAsync(
            new CompositionEdit(httpContext, principal, lifecycleService, ToolName, draftId, generation),
            body => StudioCompositionBodyEditor.RemoveWidget(body, argument.WidgetId!),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }
}

internal sealed class BindStudioInteractionTool : StudioCompositionToolBase, IMcpTool
{
    public const string ToolName = "honua_studio_bind_interaction";

    private readonly ILogger<BindStudioInteractionTool> _typedLogger;

    public BindStudioInteractionTool(IGeoprocessingJobService jobService, ILogger<BindStudioInteractionTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Bind Studio interaction",
        Description =
            "Add/replace a map/app/dashboard draft event→action binding by id with generation checking. Arguments are "
            + "static JSON plus '$event.' paths; actions emit no events, preventing cascades. invalid_argument: "
            + "unknown event/verb, on.ref/do.ref absent from this document, or too many bindings per (on.ref, on.event): "
            + $"{StudioInteractionVocabulary.MaxInteractionsPerEventSource}. Add control refs with honua_studio_add_control first.",
        InputSchema = StudioMcpSchemas.BindInteractionArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        // Re-binding the same id with the same body is idempotent; the tool never
        // removes composed state, so it is not destructive.
        Annotations = McpToolAnnotationSets.Write("Bind Studio interaction", destructive: false, idempotent: true)
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioBindInteraction");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(
                httpContext, OperatorOperation.Create, StudioAuthorizationOperation.UpdateDraft, cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioBindInteractionArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var generation = AddStudioLayerTool.RequireGeneration(argument.Generation);
        var interaction = BuildInteraction(argument.Interaction);

        var updated = await MutateCompositionAsync(
            new CompositionEdit(httpContext, principal, lifecycleService, ToolName, draftId, generation),
            body => StudioCompositionBodyEditor.BindInteraction(body, interaction),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }

    private static StudioInteraction BuildInteraction(McpStudioInteractionInput? input)
    {
        var interaction = input ?? throw new GeoprocessingValidationException("'interaction' is required.");
        if (string.IsNullOrWhiteSpace(interaction.Id))
        {
            throw new GeoprocessingValidationException("'interaction.id' is required.");
        }

        if (interaction.Id.Length > StudioInteractionVocabulary.MaxInteractionIdLength)
        {
            throw new GeoprocessingValidationException(
                $"'interaction.id' must be {StudioInteractionVocabulary.MaxInteractionIdLength} characters or fewer.");
        }

        var on = interaction.On ?? throw new GeoprocessingValidationException("'interaction.on' is required.");
        var action = interaction.Do ?? throw new GeoprocessingValidationException("'interaction.do' is required.");
        if (string.IsNullOrWhiteSpace(on.Ref))
        {
            throw new GeoprocessingValidationException("'interaction.on.ref' is required.");
        }

        if (string.IsNullOrWhiteSpace(action.Ref))
        {
            throw new GeoprocessingValidationException("'interaction.do.ref' is required.");
        }

        if (!StudioInteractionVocabulary.IsEventName(on.Event))
        {
            throw new GeoprocessingValidationException(
                $"'interaction.on.event' must be one of: {string.Join(", ", StudioInteractionVocabulary.EventNames)}. "
                + $"Got '{on.Event}'.");
        }

        if (!StudioInteractionVocabulary.IsActionVerb(action.Verb))
        {
            throw new GeoprocessingValidationException(
                $"'interaction.do.verb' must be one of: {string.Join(", ", StudioInteractionVocabulary.ActionVerbs)}. "
                + $"Got '{action.Verb}'.");
        }

        if (action.Args.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
        {
            throw new GeoprocessingValidationException("'interaction.do.args' must be a JSON object.");
        }

        if (interaction.Disabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Undefined))
        {
            throw new GeoprocessingValidationException("'interaction.disabled' must be a JSON boolean.");
        }

        return new StudioInteraction
        {
            Id = interaction.Id!,
            On = new StudioInteractionEvent { Ref = on.Ref!, Event = on.Event! },
            Do = new StudioInteractionAction
            {
                Ref = action.Ref!,
                Verb = action.Verb!,
                Args = action.Args.ValueKind == JsonValueKind.Undefined ? null : action.Args,
            },
            Disabled = interaction.Disabled.ValueKind == JsonValueKind.Undefined
                ? null
                : interaction.Disabled.GetBoolean(),
        };
    }
}

internal sealed class RemoveStudioInteractionTool : StudioCompositionToolBase, IMcpTool
{
    public const string ToolName = "honua_studio_remove_interaction";

    private readonly ILogger<RemoveStudioInteractionTool> _typedLogger;

    public RemoveStudioInteractionTool(IGeoprocessingJobService jobService, ILogger<RemoveStudioInteractionTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Remove Studio interaction",
        Description = "Remove an event/action binding from a map/app/dashboard draft by id; unknown ids fail not_found.",
        InputSchema = StudioMcpSchemas.RemoveInteractionArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        // Destructive: it removes composed wiring. Undo is a fresh
        // honua_studio_bind_interaction call, not this tool.
        Annotations = McpToolAnnotationSets.Write("Remove Studio interaction", destructive: true, idempotent: false)
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioRemoveInteraction");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(
                httpContext, OperatorOperation.Create, StudioAuthorizationOperation.UpdateDraft, cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioRemoveInteractionArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var generation = AddStudioLayerTool.RequireGeneration(argument.Generation);
        if (string.IsNullOrWhiteSpace(argument.InteractionId))
        {
            throw new GeoprocessingValidationException("'interactionId' is required.");
        }

        if (argument.InteractionId.Length > StudioInteractionVocabulary.MaxInteractionIdLength)
        {
            throw new GeoprocessingValidationException(
                $"'interactionId' must be {StudioInteractionVocabulary.MaxInteractionIdLength} characters or fewer.");
        }

        var updated = await MutateCompositionAsync(
            new CompositionEdit(httpContext, principal, lifecycleService, ToolName, draftId, generation),
            body => StudioCompositionBodyEditor.RemoveInteraction(body, argument.InteractionId!),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }
}

internal sealed class AddStudioControlTool : StudioCompositionToolBase, IMcpTool
{
    public const string ToolName = "honua_studio_add_control";

    private readonly ILogger<AddStudioControlTool> _typedLogger;

    public AddStudioControlTool(IGeoprocessingJobService jobService, ILogger<AddStudioControlTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Add Studio control",
        Description =
            "Add/replace a map/app/dashboard draft control by id with generation checking. Controls are input chrome, "
            + "not grid items; 'control:{id}' references resolve here. invalid_argument: wrong family, sourceId absent "
            + "from this document's layers/datasources, or kind outside: "
            + $"{string.Join(", ", StudioInteractionVocabulary.ControlKinds)}.",
        InputSchema = StudioMcpSchemas.AddControlArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        // Re-adding the same id with the same body is idempotent; the tool never
        // removes composed state, so it is not destructive.
        Annotations = McpToolAnnotationSets.Write("Add Studio control", destructive: false, idempotent: true)
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioAddControl");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(
                httpContext, OperatorOperation.Create, StudioAuthorizationOperation.UpdateDraft, cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioAddControlArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var generation = AddStudioLayerTool.RequireGeneration(argument.Generation);
        var control = BuildControl(argument.Control);

        var updated = await MutateCompositionAsync(
            new CompositionEdit(httpContext, principal, lifecycleService, ToolName, draftId, generation),
            body => StudioCompositionBodyEditor.AddControl(body, control),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }

    private static StudioCompositionControl BuildControl(McpStudioControlInput? input)
    {
        var control = input ?? throw new GeoprocessingValidationException("'control' is required.");
        if (string.IsNullOrWhiteSpace(control.Id))
        {
            throw new GeoprocessingValidationException("'control.id' is required.");
        }

        if (control.Id.Length > StudioInteractionVocabulary.MaxControlIdLength)
        {
            throw new GeoprocessingValidationException(
                $"'control.id' must be {StudioInteractionVocabulary.MaxControlIdLength} characters or fewer.");
        }

        if (control.Title is { Length: > StudioInteractionVocabulary.MaxControlTitleLength })
        {
            throw new GeoprocessingValidationException(
                $"'control.title' must be {StudioInteractionVocabulary.MaxControlTitleLength} characters or fewer.");
        }

        if (control.SourceId is { Length: > StudioInteractionVocabulary.MaxControlSourceIdLength })
        {
            throw new GeoprocessingValidationException(
                $"'control.sourceId' must be {StudioInteractionVocabulary.MaxControlSourceIdLength} characters or fewer.");
        }

        if (!StudioInteractionVocabulary.IsControlKind(control.Kind))
        {
            throw new GeoprocessingValidationException(
                $"'control.kind' must be one of: {string.Join(", ", StudioInteractionVocabulary.ControlKinds)}. "
                + $"Got '{control.Kind}'.");
        }

        if (control.Config.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
        {
            throw new GeoprocessingValidationException("'control.config' must be a JSON object.");
        }

        return new StudioCompositionControl
        {
            Id = control.Id!,
            Kind = control.Kind!,
            Title = control.Title,
            SourceId = control.SourceId,
            Config = control.Config.ValueKind == JsonValueKind.Undefined ? null : control.Config,
        };
    }
}

internal sealed class RemoveStudioControlTool : StudioCompositionToolBase, IMcpTool
{
    public const string ToolName = "honua_studio_remove_control";

    private readonly ILogger<RemoveStudioControlTool> _typedLogger;

    public RemoveStudioControlTool(IGeoprocessingJobService jobService, ILogger<RemoveStudioControlTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Remove Studio control",
        Description = "Remove a map/app/dashboard control by id. Unknown ids fail not_found. References to control:{id} fail invalid_argument unless cascadeInteractions also removes the interactions.",
        InputSchema = StudioMcpSchemas.RemoveControlArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        // Destructive: it removes composed state (and, when cascading, its bindings).
        // Undo is a fresh honua_studio_add_control call, not this tool.
        Annotations = McpToolAnnotationSets.Write("Remove Studio control", destructive: true, idempotent: false)
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioRemoveControl");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(
                httpContext, OperatorOperation.Create, StudioAuthorizationOperation.UpdateDraft, cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioRemoveControlArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var generation = AddStudioLayerTool.RequireGeneration(argument.Generation);
        if (string.IsNullOrWhiteSpace(argument.ControlId))
        {
            throw new GeoprocessingValidationException("'controlId' is required.");
        }

        if (argument.ControlId.Length > StudioInteractionVocabulary.MaxControlIdLength)
        {
            throw new GeoprocessingValidationException(
                $"'controlId' must be {StudioInteractionVocabulary.MaxControlIdLength} characters or fewer.");
        }

        var cascade = argument.CascadeInteractions ?? false;
        var updated = await MutateCompositionAsync(
            new CompositionEdit(httpContext, principal, lifecycleService, ToolName, draftId, generation),
            body => StudioCompositionBodyEditor.RemoveControl(body, argument.ControlId!, cascade),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }
}
