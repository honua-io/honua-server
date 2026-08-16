// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;

namespace Honua.Ai.Protocols.Mcp.Studio;

/// <summary>
/// MCP tool that adds a layer to a map/app-family Studio draft's composition
/// (honua-server#3002, REQ-002). Mirrors the honua-sdk-js agent-tools
/// <c>addLayer(layer, beforeId?)</c> shape so the SDK and server stay
/// vocabulary-aligned.
/// </summary>
internal sealed class AddStudioLayerTool : StudioCompositionToolBase, IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_studio_add_layer";

    private readonly ILogger<AddStudioLayerTool> _typedLogger;

    public AddStudioLayerTool(IGeoprocessingJobService jobService, ILogger<AddStudioLayerTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Add Studio layer",
        Description =
            "Add a layer to a map/app-family Studio draft's composition, with optimistic-generation checking. "
            + "Fails with invalid_argument if a layer with the same id already exists, or if the draft's family is not map/app.",
        InputSchema = StudioMcpSchemas.AddLayerArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        Annotations = McpToolAnnotationSets.Write("Add Studio layer", destructive: false, idempotent: false)
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioAddLayer");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Create, cancellationToken)
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
            principal,
            ToolName,
            lifecycleService,
            draftId,
            generation,
            body => StudioCompositionBodyEditor.AddLayer(body, layer, argument.BeforeId),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }

    internal static long RequireGeneration(long? generation) => generation
        ?? throw new GeoprocessingValidationException("'generation' is required.");
}

/// <summary>
/// MCP tool that removes a layer from a map/app-family Studio draft's
/// composition (honua-server#3002, REQ-002).
/// </summary>
internal sealed class RemoveStudioLayerTool : StudioCompositionToolBase, IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_studio_remove_layer";

    private readonly ILogger<RemoveStudioLayerTool> _typedLogger;

    public RemoveStudioLayerTool(IGeoprocessingJobService jobService, ILogger<RemoveStudioLayerTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Remove Studio layer",
        Description =
            "Remove a layer from a map/app-family Studio draft's composition by id, with optimistic-generation checking. "
            + "Fails with not_found if no layer with that id exists.",
        InputSchema = StudioMcpSchemas.RemoveLayerArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        // Destructive: it removes composed state. Undo is a fresh
        // honua_studio_add_layer call or a draft revision reopen — not this tool.
        Annotations = McpToolAnnotationSets.Write("Remove Studio layer", destructive: true, idempotent: false)
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioRemoveLayer");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Create, cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioRemoveLayerArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var generation = AddStudioLayerTool.RequireGeneration(argument.Generation);
        if (string.IsNullOrWhiteSpace(argument.LayerId))
        {
            throw new GeoprocessingValidationException("'layerId' is required.");
        }

        var updated = await MutateCompositionAsync(
            principal,
            ToolName,
            lifecycleService,
            draftId,
            generation,
            body => StudioCompositionBodyEditor.RemoveLayer(body, argument.LayerId!),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }
}

/// <summary>
/// MCP tool that sets (or clears) a layer's bound style reference in a
/// map/app-family Studio draft's composition (honua-server#3002, REQ-002).
/// </summary>
internal sealed class SetStudioLayerStyleTool : StudioCompositionToolBase, IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_studio_set_layer_style";

    private readonly ILogger<SetStudioLayerStyleTool> _typedLogger;

    public SetStudioLayerStyleTool(IGeoprocessingJobService jobService, ILogger<SetStudioLayerStyleTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Set Studio layer style",
        Description =
            "Set (or, when styleRef is omitted/null, clear) a layer's bound style reference in a map/app-family Studio draft's "
            + "composition, with optimistic-generation checking. Fails with not_found if no layer with that id exists. "
            + "This binds a style reference on the composed layer; it does not validate the reference against a style catalog.",
        InputSchema = StudioMcpSchemas.SetLayerStyleArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        // Re-pointing a style binding is non-destructive; re-applying the same
        // styleRef is idempotent.
        Annotations = McpToolAnnotationSets.Write("Set Studio layer style", destructive: false, idempotent: true)
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioSetLayerStyle");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Create, cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioSetLayerStyleArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var generation = AddStudioLayerTool.RequireGeneration(argument.Generation);
        if (string.IsNullOrWhiteSpace(argument.LayerId))
        {
            throw new GeoprocessingValidationException("'layerId' is required.");
        }

        var updated = await MutateCompositionAsync(
            principal,
            ToolName,
            lifecycleService,
            draftId,
            generation,
            body => StudioCompositionBodyEditor.SetLayerStyleRef(body, argument.LayerId!, argument.StyleRef),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }
}

/// <summary>
/// MCP tool that shows or hides a layer in a map/app-family Studio draft's
/// composition (honua-server#3199), the server-side execution path for the
/// ADR-0030 <c>setVisibility</c> action verb.
/// </summary>
/// <remarks>
/// <c>visible</c> is part of the stored <c>StudioCompositionLayer</c> wire shape, so a draft sync
/// overwrites a client-local visibility toggle with the stored value. Before this tool the only
/// writer of <c>visible</c> was <c>honua_studio_add_layer</c>, which sets it once and rejects
/// duplicate ids — leaving composition state with no way to change a layer's visibility at all.
/// </remarks>
internal sealed class SetStudioLayerVisibilityTool : StudioCompositionToolBase, IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_studio_set_layer_visibility";

    private readonly ILogger<SetStudioLayerVisibilityTool> _typedLogger;

    public SetStudioLayerVisibilityTool(IGeoprocessingJobService jobService, ILogger<SetStudioLayerVisibilityTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Set Studio layer visibility",
        Description =
            "Show or hide a layer in a map/app-family Studio draft's composition, with optimistic-generation "
            + "checking. This is the persisted counterpart of the ADR-0030 'setVisibility' action verb: a "
            + "client-local toggle is overwritten by the next draft sync, a toggle written here is not. "
            + "Fails with not_found if no layer with that id exists.",
        InputSchema = StudioMcpSchemas.SetLayerVisibilityArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        // Hiding a layer removes no composed state (the layer, its source and its style binding
        // all survive), and re-applying the same visibility is idempotent.
        Annotations = McpToolAnnotationSets.Write("Set Studio layer visibility", destructive: false, idempotent: true)
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioSetLayerVisibility");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Create, cancellationToken)
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
            principal,
            ToolName,
            lifecycleService,
            draftId,
            generation,
            body => StudioCompositionBodyEditor.SetLayerVisibility(body, argument.LayerId!, visible),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }
}

/// <summary>
/// MCP tool that replaces a map/app-family Studio draft's composition view
/// (honua-server#3002, REQ-002). Mirrors the honua-sdk-js agent-tools
/// <c>setViewport</c> shape (bbox, center, zoom, pitch, bearing, crs).
/// </summary>
internal sealed class SetStudioViewTool : StudioCompositionToolBase, IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_studio_set_view";

    private readonly ILogger<SetStudioViewTool> _typedLogger;

    public SetStudioViewTool(IGeoprocessingJobService jobService, ILogger<SetStudioViewTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Set Studio view",
        Description =
            "Replace a map/app-family Studio draft's composition view (bbox, center, zoom, pitch, bearing, crs), "
            + "with optimistic-generation checking.",
        InputSchema = StudioMcpSchemas.SetViewArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        Annotations = McpToolAnnotationSets.Write("Set Studio view", destructive: false, idempotent: true)
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioSetView");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Create, cancellationToken)
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
            principal,
            ToolName,
            lifecycleService,
            draftId,
            generation,
            body => StudioCompositionBodyEditor.SetView(body, view),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }
}

/// <summary>
/// MCP tool that adds a widget to an app-family Studio draft's composition
/// (honua-server#3002, REQ-002).
/// </summary>
internal sealed class AddStudioWidgetTool : StudioCompositionToolBase, IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_studio_add_widget";

    private readonly ILogger<AddStudioWidgetTool> _typedLogger;

    public AddStudioWidgetTool(IGeoprocessingJobService jobService, ILogger<AddStudioWidgetTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Add Studio widget",
        Description =
            "Add a widget to a map/app-family Studio draft's composition, with optimistic-generation checking. "
            + "Fails with invalid_argument if a widget with the same id already exists, or if the draft's family is not map/app.",
        InputSchema = StudioMcpSchemas.AddWidgetArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        Annotations = McpToolAnnotationSets.Write("Add Studio widget", destructive: false, idempotent: false)
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioAddWidget");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Create, cancellationToken)
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
            principal,
            ToolName,
            lifecycleService,
            draftId,
            generation,
            body => StudioCompositionBodyEditor.AddWidget(body, widget),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }
}

/// <summary>
/// MCP tool that removes a widget from an app-family Studio draft's
/// composition (honua-server#3002, REQ-002).
/// </summary>
internal sealed class RemoveStudioWidgetTool : StudioCompositionToolBase, IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_studio_remove_widget";

    private readonly ILogger<RemoveStudioWidgetTool> _typedLogger;

    public RemoveStudioWidgetTool(IGeoprocessingJobService jobService, ILogger<RemoveStudioWidgetTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Remove Studio widget",
        Description =
            "Remove a widget from a map/app-family Studio draft's composition by id, with optimistic-generation checking. "
            + "Fails with not_found if no widget with that id exists.",
        InputSchema = StudioMcpSchemas.RemoveWidgetArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        Annotations = McpToolAnnotationSets.Write("Remove Studio widget", destructive: true, idempotent: false)
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioRemoveWidget");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Create, cancellationToken)
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
            principal,
            ToolName,
            lifecycleService,
            draftId,
            generation,
            body => StudioCompositionBodyEditor.RemoveWidget(body, argument.WidgetId!),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }
}

/// <summary>
/// MCP tool that adds or replaces one declarative event→action binding in a
/// map/app-family Studio draft's composition — the reference implementation of
/// the geospatial-mcp standard's <c>bind_interaction</c> (ADR-0030, <c>composition</c>
/// profile). The standard-level target is a composition document
/// (<c>mapPackageId</c>/<c>appPackageId</c>); Honua authors compositions through the
/// draft lifecycle, and that <c>draftId</c> + <c>generation</c> spelling is admitted
/// upstream as <c>x-honua-reference-shape</c>.
/// </summary>
internal sealed class BindStudioInteractionTool : StudioCompositionToolBase, IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_studio_bind_interaction";

    private readonly ILogger<BindStudioInteractionTool> _typedLogger;

    public BindStudioInteractionTool(IGeoprocessingJobService jobService, ILogger<BindStudioInteractionTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Bind Studio interaction",
        Description =
            "Add or replace (by id) one declarative event→action binding in a map/app-family Studio draft's "
            + "composition, with optimistic-generation checking. Bindings are data, not code: arguments are static "
            + "JSON plus '$event.' path substitution, and actions never emit events, so bindings cannot cascade. "
            + "Fails with invalid_argument when the event/verb is outside the closed sets, when on.ref/do.ref does "
            + "not resolve to a component declared in the same document (a 'control:{id}' ref resolves against the "
            + "document's controls collection — add it with honua_studio_add_control first), or when "
            + "the binding would push a single (on.ref, on.event) source past "
            + $"{StudioInteractionVocabulary.MaxInteractionsPerEventSource} interactions.",
        InputSchema = StudioMcpSchemas.BindInteractionArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        // Re-binding the same id with the same body is idempotent; the tool never
        // removes composed state, so it is not destructive.
        Annotations = McpToolAnnotationSets.Write("Bind Studio interaction", destructive: false, idempotent: true)
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioBindInteraction");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Create, cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioBindInteractionArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var generation = AddStudioLayerTool.RequireGeneration(argument.Generation);
        var interaction = BuildInteraction(argument.Interaction);

        var updated = await MutateCompositionAsync(
            principal,
            ToolName,
            lifecycleService,
            draftId,
            generation,
            body => StudioCompositionBodyEditor.BindInteraction(body, interaction),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }

    /// <summary>
    /// Maps the wire input onto the domain <see cref="StudioInteraction"/>, enforcing the
    /// required members and the closed event/verb vocabulary in the HANDLER. The advertised
    /// <c>inputSchema</c> states the same contract, but MCP dispatch does not evaluate it,
    /// so the schema is documentation and this is the gate.
    /// </summary>
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

/// <summary>
/// MCP tool that removes one declarative event→action binding, by id, from a
/// map/app-family Studio draft's composition — the reference implementation of the
/// geospatial-mcp standard's <c>remove_interaction</c> (ADR-0030, <c>composition</c>
/// profile). Removing an unknown id is an error, not a no-op.
/// </summary>
internal sealed class RemoveStudioInteractionTool : StudioCompositionToolBase, IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_studio_remove_interaction";

    private readonly ILogger<RemoveStudioInteractionTool> _typedLogger;

    public RemoveStudioInteractionTool(IGeoprocessingJobService jobService, ILogger<RemoveStudioInteractionTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Remove Studio interaction",
        Description =
            "Remove one declarative event→action binding from a map/app-family Studio draft's composition by id, "
            + "with optimistic-generation checking. Fails with not_found if no interaction with that id exists.",
        InputSchema = StudioMcpSchemas.RemoveInteractionArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        // Destructive: it removes composed wiring. Undo is a fresh
        // honua_studio_bind_interaction call, not this tool.
        Annotations = McpToolAnnotationSets.Write("Remove Studio interaction", destructive: true, idempotent: false)
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioRemoveInteraction");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Create, cancellationToken)
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
            principal,
            ToolName,
            lifecycleService,
            draftId,
            generation,
            body => StudioCompositionBodyEditor.RemoveInteraction(body, argument.InteractionId!),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }
}

/// <summary>
/// MCP tool that adds or replaces one control in a map/app-family Studio draft's
/// composition — the reference implementation of the geospatial-mcp standard's
/// <c>add_control</c> (ADR-0031, <c>composition</c> profile). Controls are a peer
/// collection to layers and widgets, not a widget kind: a control is an input
/// affordance that emits <c>change</c> and is the target a <c>control:{id}</c>
/// interaction reference resolves against. The standard-level target is a composition
/// document (<c>mapPackageId</c>/<c>appPackageId</c>); Honua authors compositions
/// through the draft lifecycle, and that <c>draftId</c> + <c>generation</c> spelling is
/// admitted upstream as <c>x-honua-reference-shape</c>.
/// </summary>
internal sealed class AddStudioControlTool : StudioCompositionToolBase, IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_studio_add_control";

    private readonly ILogger<AddStudioControlTool> _typedLogger;

    public AddStudioControlTool(IGeoprocessingJobService jobService, ILogger<AddStudioControlTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Add Studio control",
        Description =
            "Add or replace (by id) one control in a map/app-family Studio draft's composition, with "
            + "optimistic-generation checking. Controls are input affordances (chrome), not layout grid items, and "
            + "are what 'control:{id}' interaction references resolve against. Fails with invalid_argument when the "
            + "kind is outside the closed vocabulary ("
            + $"{string.Join(", ", StudioInteractionVocabulary.ControlKinds)}), when a supplied sourceId does not "
            + "resolve to a layer or datasource declared in the same document, or when the draft's family is not map/app.",
        InputSchema = StudioMcpSchemas.AddControlArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        // Re-adding the same id with the same body is idempotent; the tool never
        // removes composed state, so it is not destructive.
        Annotations = McpToolAnnotationSets.Write("Add Studio control", destructive: false, idempotent: true)
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioAddControl");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Create, cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioAddControlArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var generation = AddStudioLayerTool.RequireGeneration(argument.Generation);
        var control = BuildControl(argument.Control);

        var updated = await MutateCompositionAsync(
            principal,
            ToolName,
            lifecycleService,
            draftId,
            generation,
            body => StudioCompositionBodyEditor.AddControl(body, control),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }

    /// <summary>
    /// Maps the wire input onto the domain <see cref="StudioCompositionControl"/>, enforcing
    /// the required members and the closed kind vocabulary in the HANDLER. The advertised
    /// <c>inputSchema</c> states the same contract, but MCP dispatch does not evaluate it,
    /// so the schema is documentation and this is the gate.
    /// </summary>
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

/// <summary>
/// MCP tool that removes one control, by id, from a map/app-family Studio draft's
/// composition — the reference implementation of the geospatial-mcp standard's
/// <c>remove_control</c> (ADR-0031, <c>composition</c> profile). Removing an unknown id
/// is an error, not a no-op, and removing a control an interaction still references
/// either fails (default) or removes those bindings with it
/// (<c>cascadeInteractions</c>): a document never silently retains a dangling
/// <c>control:{id}</c> binding.
/// </summary>
internal sealed class RemoveStudioControlTool : StudioCompositionToolBase, IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_studio_remove_control";

    private readonly ILogger<RemoveStudioControlTool> _typedLogger;

    public RemoveStudioControlTool(IGeoprocessingJobService jobService, ILogger<RemoveStudioControlTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Remove Studio control",
        Description =
            "Remove one control from a map/app-family Studio draft's composition by id, with "
            + "optimistic-generation checking. Fails with not_found if no control with that id exists, and with "
            + "invalid_argument when an interaction still references 'control:{id}' unless cascadeInteractions is "
            + "true, in which case those bindings are removed with the control.",
        InputSchema = StudioMcpSchemas.RemoveControlArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        // Destructive: it removes composed state (and, when cascading, its bindings).
        // Undo is a fresh honua_studio_add_control call, not this tool.
        Annotations = McpToolAnnotationSets.Write("Remove Studio control", destructive: true, idempotent: false)
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioRemoveControl");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Create, cancellationToken)
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
            principal,
            ToolName,
            lifecycleService,
            draftId,
            generation,
            body => StudioCompositionBodyEditor.RemoveControl(body, argument.ControlId!, cascade),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }
}
