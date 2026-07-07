// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;

namespace Honua.Ai.Protocols.Mcp.MapTools;

/// <summary>
/// MCP tool that applies a named style preset — a reusable catalog
/// <c>styleId</c> — as the primary/default style of a published layer. It is a
/// thin adapter over the canonical styling pipeline (ADR-0048): it validates the
/// preset exists in the styleId-keyed <see cref="IStyleCatalog"/>, binds it to
/// the layer at ordinal 0 via <see cref="IStyleCatalog.AssociateLayerAsync"/>,
/// then reconciles the Metadata v2 graph through
/// <see cref="IMetadataV2StyleGraphSync.SyncLayerStylesAsync"/> — the same seams
/// the <c>/ogc/styles</c> authoring surface drives. It persists presentation
/// metadata on the hosted service so a subsequent <c>honua_render_map</c>
/// resolves the applied style; it never edits feature records (ADR-0028).
/// </summary>
internal sealed class ApplyStylePresetTool : IMcpTool
{
    /// <summary>The advertised <c>tools/list</c> name of this tool.</summary>
    public const string ToolName = "honua_apply_style_preset";

    private const string ToolDescription =
        "Apply a named style preset (a reusable catalog styleId) as a published layer's primary/default style, addressed by serviceId/layerId. "
        + "The preset must already exist in the style catalog — discover the valid presets with honua_get_style (omit styleId to list them); an unknown preset is rejected and the valid presets are named. "
        + "The applied style persists through the canonical OGC API - Styles pipeline (styleId-keyed catalog + Metadata v2 graph), so a subsequent honua_render_map for the layer resolves the new style. "
        + "It authors presentation metadata only and never edits feature records. Re-applying the same preset is a no-op (idempotent). "
        + "Styled-map arc: query/analyze -> honua_publish_result (analysis result -> serviceId/layerId) -> honua_apply_style_preset -> honua_render_map.";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<ApplyStylePresetTool> _logger;

    public ApplyStylePresetTool(IGeoprocessingJobService jobService, ILogger<ApplyStylePresetTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Apply style preset",
        Description = ToolDescription,
        InputSchema = MapToolSchemas.ApplyStylePresetArgumentSchema,
        OutputSchema = McpToolOutputSchemas.ApplyStylePresetOutputSchema,
        // Write tool: it re-points the layer's primary style binding (authoring
        // presentation metadata) rather than destroying data, and re-applying the
        // same preset yields the same binding — non-destructive and idempotent.
        Annotations = McpToolAnnotationSets.Write("Apply style preset", destructive: false, idempotent: true)
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("ApplyStylePreset");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        // Applying a preset mutates the hosted service's presentation metadata, so
        // it is gated on the authoring/publish grant the /ogc/styles authoring path
        // enforces (admin authorization there; the operator model maps it to a
        // PublishedService.Publish write grant). This is deliberately stronger than
        // the generic Process.Execute data-plane gate: a query-only principal
        // (Read/Discover grants) is denied.
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.PublishedService, OperatorOperation.Publish, cancellationToken)
            .ConfigureAwait(false);

        var argument = McpToolHelpers.ParseArguments(arguments, MapToolJsonContext.Default.McpApplyStylePresetArgument);

        if (string.IsNullOrWhiteSpace(argument.StyleId))
        {
            throw new GeoprocessingValidationException("'styleId' is required and must name a style preset.");
        }

        var styleCatalog = httpContext.RequestServices.GetService<IStyleCatalog>()
            ?? throw new GeoprocessingStoreUnavailableException("The style catalog is not available on this server.");
        var graphSync = httpContext.RequestServices.GetService<IMetadataV2StyleGraphSync>()
            ?? throw new GeoprocessingStoreUnavailableException("The style graph synchronizer is not available on this server.");
        var graphProvider = httpContext.RequestServices.GetService<IMetadataV2GraphProvider>()
            ?? throw new GeoprocessingStoreUnavailableException("The metadata catalog is not available on this server.");

        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var layer = MapToolLayerResolver.Resolve(snapshot, argument.ServiceId, argument.LayerId);

        var styleId = argument.StyleId!.Trim();
        var preset = await styleCatalog.GetStyleAsync(styleId, cancellationToken).ConfigureAwait(false);
        if (preset is null)
        {
            var available = await styleCatalog.ListStylesAsync(cancellationToken).ConfigureAwait(false);
            var presetIds = available.Count == 0
                ? "(none available)"
                : string.Join(", ", available.Select(s => s.StyleId));
            throw new GeoprocessingValidationException(
                $"Unknown style preset '{styleId}'. Valid presets: {presetIds}. "
                + "List presets with honua_get_style (omit styleId).");
        }

        // Bind the preset as the layer's primary (ordinal 0) style, then reconcile
        // the canonical graph so StyleResourceIds[0] and the Type=Style resource
        // reflect it — the same sequence LayerStyleService uses on the HTTP path.
        await styleCatalog.AssociateLayerAsync(layer.StorageLayerId, styleId, ordinal: 0, cancellationToken)
            .ConfigureAwait(false);
        await graphSync.SyncLayerStylesAsync(layer.StorageLayerId, cancellationToken).ConfigureAwait(false);

        var output = new McpApplyStylePresetOutput
        {
            ServiceId = layer.Service.Metadata.Id,
            LayerId = argument.LayerId!.Value,
            StyleId = preset.StyleId,
            Title = preset.Title,
            StyleVersion = preset.StyleVersion,
            Applied = true
        };
        return McpToolHelpers.SuccessResult(output, MapToolJsonContext.Default.McpApplyStylePresetOutput);
    }
}
