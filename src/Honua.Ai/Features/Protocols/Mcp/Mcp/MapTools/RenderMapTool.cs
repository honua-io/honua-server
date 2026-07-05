// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;

namespace Honua.Ai.Protocols.Mcp.MapTools;

/// <summary>
/// MCP tool that renders a map image for one or more published layers and
/// returns it as an MCP <c>image</c> content block (base64 PNG) so the client
/// can display it inline. Thin adapter over the canonical
/// <see cref="IRasterMapRenderer"/> pipeline — the same renderer the OGC API
/// Maps / MapServer export / WMS GetMap surfaces drive — so no rasterization or
/// styling logic is reimplemented here. Layers are resolved through the same
/// Metadata v2 snapshot the GeoServices surfaces use and passed bottom-to-top.
/// </summary>
internal sealed class RenderMapTool : IMcpTool
{
    public const string ToolName = "honua_render_map";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<RenderMapTool> _logger;

    public RenderMapTool(IGeoprocessingJobService jobService, ILogger<RenderMapTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Render map",
        Description = "Render a map image (PNG) for one or more published layers over a bbox and return it as an inline image. Layers draw bottom-to-top. Width/height are capped at 1024 px. "
            + "Each layer renders with its primary/default style, which the caption reports; change a layer's style first with honua_apply_style_preset (discover presets with honua_get_style) and re-render to reflect it. "
            + "To render analysis results as a styled map: run the analysis, then honua_publish_result to promote the result to a serviceId/layerId, then optionally honua_apply_style_preset, then render that layer here.",
        InputSchema = MapToolSchemas.RenderMapArgumentSchema,
        // Read-only render. No OutputSchema: this tool returns an image content
        // block, not a structuredContent payload, so there is no structured
        // result shape to describe.
        Annotations = McpToolAnnotationSets.ReadOnly("Render map")
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("RenderMap");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Process, OperatorOperation.Read, cancellationToken)
            .ConfigureAwait(false);

        var argument = McpToolHelpers.ParseArguments(arguments, MapToolJsonContext.Default.McpRenderMapArgument);

        if (argument.Layers is not { Count: > 0 })
        {
            throw new GeoprocessingValidationException("'layers' is required and must contain at least one layer.");
        }

        var bbox = ResolveBbox(argument.Bbox);
        var bboxSrid = argument.BboxSrid ?? 4326;
        if (bboxSrid <= 0)
        {
            throw new GeoprocessingValidationException("'bboxSrid' must be a positive SRID/WKID.");
        }

        var width = ResolveSize(argument.Width, "width");
        var height = ResolveSize(argument.Height, "height");

        var graphProvider = httpContext.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        // The styleId-keyed catalog is the canonical binding honua_apply_style_preset
        // writes and the /ogc/styles surface authors. Resolving each layer's primary
        // style here makes the applied preset observable in a subsequent render (the
        // caption reports it). Rasterizing vector styles at the IRasterMapRenderer
        // seam is not yet supported (RenderStyledMapAsync throws) and is out of scope
        // for this tool; the pixels come from the raster mosaic path.
        var styleCatalog = httpContext.RequestServices.GetService<IStyleCatalog>();

        var storageLayerIds = new int[argument.Layers.Count];
        var effectiveStyleIds = new string?[argument.Layers.Count];
        for (var i = 0; i < argument.Layers.Count; i++)
        {
            var layerRef = argument.Layers[i];
            var resolved = MapToolLayerResolver.Resolve(snapshot, layerRef.ServiceId, layerRef.LayerId);
            storageLayerIds[i] = resolved.StorageLayerId;
            effectiveStyleIds[i] = await ResolveEffectiveStyleIdAsync(styleCatalog, resolved.StorageLayerId, cancellationToken)
                .ConfigureAwait(false);
        }

        var request = new MapRenderRequest
        {
            BoundingBox = bbox,
            Width = width,
            Height = height,
            BoundingBoxCrs = bboxSrid,
            Crs = bboxSrid,
            Format = RasterFormat.PNG,
            Transparent = argument.Transparent ?? false
        };

        var renderer = httpContext.RequestServices.GetRequiredService<IRasterMapRenderer>();
        var result = await renderer
            .RenderDatasetMapAsync(storageLayerIds, request, cancellationToken)
            .ConfigureAwait(false);

        if (result.Data.Length == 0)
        {
            throw new GeoprocessingStoreUnavailableException("Map rendering produced an empty image.");
        }

        var base64 = Convert.ToBase64String(result.Data);
        var caption = string.Format(
            CultureInfo.InvariantCulture,
            "Rendered {0} layer{1} at {2}x{3} px over bbox [{4}, {5}, {6}, {7}] (SRID {8}).",
            storageLayerIds.Length,
            storageLayerIds.Length == 1 ? string.Empty : "s",
            result.Width,
            result.Height,
            bbox[0],
            bbox[1],
            bbox[2],
            bbox[3],
            bboxSrid);

        var styleNote = BuildStyleNote(effectiveStyleIds);
        if (styleNote is not null)
        {
            caption = caption + " " + styleNote;
        }

        return new McpToolsCallResult
        {
            IsError = false,
            Content =
            [
                new McpContentBlock { Type = "text", Text = caption },
                new McpContentBlock
                {
                    Type = "image",
                    Data = base64,
                    MimeType = result.ContentType
                }
            ]
        };
    }

    private static double[] ResolveBbox(IReadOnlyList<double>? bbox)
    {
        if (bbox is null || bbox.Count != 4)
        {
            throw new GeoprocessingValidationException("'bbox' must contain exactly four numbers: [minX, minY, maxX, maxY].");
        }

        var minX = bbox[0];
        var minY = bbox[1];
        var maxX = bbox[2];
        var maxY = bbox[3];
        if (maxX <= minX || maxY <= minY)
        {
            throw new GeoprocessingValidationException("'bbox' max ordinates must be greater than the min ordinates.");
        }

        return [minX, minY, maxX, maxY];
    }

    private static async Task<string?> ResolveEffectiveStyleIdAsync(
        IStyleCatalog? styleCatalog,
        int storageLayerId,
        CancellationToken cancellationToken)
    {
        if (styleCatalog is null)
        {
            return null;
        }

        var styles = await styleCatalog.GetStylesForLayerAsync(storageLayerId, cancellationToken).ConfigureAwait(false);
        // Ordinal 0 (first) is the primary/default style by convention.
        return styles.Count > 0 ? styles[0].StyleId : null;
    }

    private static string? BuildStyleNote(string?[] effectiveStyleIds)
    {
        if (effectiveStyleIds.Length == 0 || Array.TrueForAll(effectiveStyleIds, id => id is null))
        {
            return null;
        }

        var builder = new StringBuilder("Layer styles: ");
        for (var i = 0; i < effectiveStyleIds.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(effectiveStyleIds[i] ?? "(default)");
        }

        builder.Append('.');
        return builder.ToString();
    }

    private static int ResolveSize(int? requested, string field)
    {
        var size = requested ?? MapToolSchemas.DefaultRenderSize;
        if (size < 1)
        {
            throw new GeoprocessingValidationException($"'{field}' must be a positive integer.");
        }

        return Math.Min(size, MapToolSchemas.MaxRenderSize);
    }
}
