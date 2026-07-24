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

    public AddStudioLayerTool(
        IStudioPackageLifecycleService lifecycleService,
        IGeoprocessingJobService jobService,
        ILogger<AddStudioLayerTool> logger)
        : base(lifecycleService, jobService, logger)
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

    public RemoveStudioLayerTool(
        IStudioPackageLifecycleService lifecycleService,
        IGeoprocessingJobService jobService,
        ILogger<RemoveStudioLayerTool> logger)
        : base(lifecycleService, jobService, logger)
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

    public SetStudioLayerStyleTool(
        IStudioPackageLifecycleService lifecycleService,
        IGeoprocessingJobService jobService,
        ILogger<SetStudioLayerStyleTool> logger)
        : base(lifecycleService, jobService, logger)
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
            draftId,
            generation,
            body => StudioCompositionBodyEditor.SetLayerStyleRef(body, argument.LayerId!, argument.StyleRef),
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

    public SetStudioViewTool(
        IStudioPackageLifecycleService lifecycleService,
        IGeoprocessingJobService jobService,
        ILogger<SetStudioViewTool> logger)
        : base(lifecycleService, jobService, logger)
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

    public AddStudioWidgetTool(
        IStudioPackageLifecycleService lifecycleService,
        IGeoprocessingJobService jobService,
        ILogger<AddStudioWidgetTool> logger)
        : base(lifecycleService, jobService, logger)
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

    public RemoveStudioWidgetTool(
        IStudioPackageLifecycleService lifecycleService,
        IGeoprocessingJobService jobService,
        ILogger<RemoveStudioWidgetTool> logger)
        : base(lifecycleService, jobService, logger)
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
            draftId,
            generation,
            body => StudioCompositionBodyEditor.RemoveWidget(body, argument.WidgetId!),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }
}
