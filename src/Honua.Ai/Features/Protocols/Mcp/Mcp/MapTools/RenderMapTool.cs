// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Services;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;

namespace Honua.Ai.Protocols.Mcp.MapTools;

/// <summary>
/// MCP tool that renders a map image for one or more published layers and
/// returns it as a fetchable artifact reference (<c>resource_link</c> href) by
/// default, or as an inline base64 <c>image</c> content block when the caller
/// opts in via <c>maxInlineBytes</c> and the encoded image fits. Thin adapter
/// over the canonical <see cref="IRasterMapRenderer"/> pipeline — the same
/// renderer the OGC API Maps / MapServer export / WMS GetMap surfaces drive —
/// so no rasterization or styling logic is reimplemented here. Layers are
/// resolved through the same Metadata v2 snapshot the GeoServices surfaces use
/// and passed bottom-to-top. Each layer renders with its currently applied style:
/// the primary binding in the styleId-keyed style catalog (what
/// <c>honua_apply_style_preset</c> writes), or the layer's stored default style when
/// no catalog style is applied. Style constructs the server rasterizer cannot draw
/// are reported per layer as <c>unsupported-style-construct</c> rather than silently
/// replaced by the default style.
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
        Description = "Render a map image (PNG) for one or more published layers over a bbox. Layers draw bottom-to-top. Width/height are capped at 1024 px. "
            + "By default the result is a fetchable artifact reference (resource_link href) with the image dimensions and byte size in text — NOT an inline base64 image — so a multi-megabyte render never floods the model context. "
            + "Set maxInlineBytes to opt into inlining the base64 PNG when the encoded image is at or below that size. "
            + "Each layer renders with its currently applied style (the primary style bound by honua_apply_style_preset; discover presets with honua_get_style), or its stored default style when none is applied. "
            + "Each entry in layers reports styleId and styleRendering: 'applied' (the applied style was drawn), 'default' (no applied style; stored default drawn), or 'unsupported-style-construct' (unsupportedStyleConstructs lists what was not drawn; the server rasterizes MapLibre fill, line, and circle layers). "
            + "To render analysis results as a styled map: run the analysis, then honua_publish_result to promote the result to a serviceId/layerId, then optionally honua_apply_style_preset, then render that layer here.",
        InputSchema = MapToolSchemas.RenderMapArgumentSchema,
        OutputSchema = McpToolOutputSchemas.RenderMapOutputSchema,
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
        // writes and the /ogc/styles surface authors. Each layer's primary catalog style
        // is resolved here and handed to the renderer (AppliedStyleJsonByLayerId), so the
        // shared styled-vector path draws the applied style instead of the layer's stored
        // default. The per-layer styleRendering outcome reports what was actually drawn.
        var styleCatalog = httpContext.RequestServices.GetService<IStyleCatalog>();

        var storageLayerIds = new int[argument.Layers.Count];
        var renderedLayers = new McpRenderedLayer[argument.Layers.Count];
        var resolvedLayers = new ResolvedMapLayer[argument.Layers.Count];
        var appliedStyleJson = new Dictionary<int, string>();
        var hasRasterCoverage = false;
        var hasVectorLayer = false;
        for (var i = 0; i < argument.Layers.Count; i++)
        {
            var layerRef = argument.Layers[i];
            var resolved = await MapToolLayerResolver.ResolveForReadAsync(
                httpContext, snapshot, layerRef.ServiceId, layerRef.LayerId, AuthorizationOperation.Query, cancellationToken)
                .ConfigureAwait(false);
            EnsureDistinctStorageIdentity(resolvedLayers, i, resolved);
            storageLayerIds[i] = resolved.StorageLayerId;
            resolvedLayers[i] = new ResolvedMapLayer(resolved.StorageLayerId, resolved.Resource.Metadata.Id);
            if (IsRasterCoverage(resolved.Resource))
            {
                hasRasterCoverage = true;
            }
            else
            {
                hasVectorLayer = true;
            }

            if (hasRasterCoverage && hasVectorLayer)
            {
                // The renderer returns raster coverage pixels as soon as any requested layer
                // has coverage and never composites vector layers over them, so a mixed
                // request would silently drop the vector layers.
                throw new GeoprocessingValidationException(
                    "'layers' mixes raster coverage layers with vector feature layers, which honua_render_map cannot composite in one image. "
                    + "Render the raster coverage layers and the vector layers in separate honua_render_map calls.");
            }

            var appliedStyle = await ResolveAppliedStyleAsync(styleCatalog, resolved.StorageLayerId, cancellationToken)
                .ConfigureAwait(false);
            if (appliedStyle is not null)
            {
                // Forwarded even when the document has no style layers: the renderer then
                // draws nothing for the layer rather than the stored default style.
                appliedStyleJson[resolved.StorageLayerId] = appliedStyle.MapLibreStyleJson ?? string.Empty;
            }

            var (styleRendering, unsupported) = ClassifyStyleRendering(resolved.Resource, appliedStyle);
            renderedLayers[i] = new McpRenderedLayer
            {
                ServiceId = resolved.Service.Metadata.Id,
                LayerId = layerRef.LayerId!.Value,
                StyleId = appliedStyle?.StyleId,
                StyleRendering = styleRendering,
                UnsupportedStyleConstructs = unsupported
            };
        }

        var request = new MapRenderRequest
        {
            BoundingBox = bbox,
            Width = width,
            Height = height,
            BoundingBoxCrs = bboxSrid,
            Crs = bboxSrid,
            Format = RasterFormat.PNG,
            Transparent = argument.Transparent ?? false,
            ResolvedLayers = resolvedLayers,
            AppliedStyleJsonByLayerId = appliedStyleJson.Count > 0 ? appliedStyleJson : null
        };

        var renderer = httpContext.RequestServices.GetRequiredService<IRasterMapRenderer>();
        var result = await renderer
            .RenderDatasetMapAsync(storageLayerIds, request, cancellationToken)
            .ConfigureAwait(false);

        if (result.Data.Length == 0)
        {
            // An empty payload here means the layer(s) yielded no pixels — no raster
            // coverage and no renderable vector features in the requested window. A genuine
            // renderer-capability failure (e.g. the native SkiaSharp library missing on a
            // serverless/AOT image) is raised as RasterRenderingUnavailableException before
            // this point and mapped to a failed_precondition capability error (#2770), so
            // this message stays specific to the no-data case rather than a generic failure.
            throw new GeoprocessingStoreUnavailableException(
                "Map rendering produced no output: the requested layer(s) have no raster coverage or renderable features within the requested bounding box.");
        }

        var extentDescription = string.Format(
            CultureInfo.InvariantCulture,
            "over bbox [{0}, {1}, {2}, {3}] (SRID {4})",
            bbox[0],
            bbox[1],
            bbox[2],
            bbox[3],
            bboxSrid);
        var layerCountDescription = string.Format(
            CultureInfo.InvariantCulture,
            "{0} layer{1}",
            storageLayerIds.Length,
            storageLayerIds.Length == 1 ? string.Empty : "s");

        // The effective per-layer styles are reported on both result shapes so an
        // applied preset (honua_apply_style_preset) stays observable in the caption.
        var styleNote = BuildStyleNote(renderedLayers);

        // Default: hand back a fetchable artifact href instead of inlining a
        // multi-megabyte base64 PNG into the model context. Inline only when the
        // caller opts in via maxInlineBytes AND the encoded image fits under it.
        var maxInlineBytes = argument.MaxInlineBytes ?? 0;
        if (maxInlineBytes > 0 && result.Data.Length <= maxInlineBytes)
        {
            var base64 = Convert.ToBase64String(result.Data);
            var output = BuildRenderOutput(
                result,
                bbox,
                bboxSrid,
                renderedLayers,
                new McpRenderedImage
                {
                    Format = result.ContentType,
                    Width = result.Width,
                    Height = result.Height,
                    ByteLength = result.Data.Length,
                    Base64 = base64,
                    Inlined = true
                });
            var (structuredContent, _) = McpToolHelpers.SerializeStructured(
                output,
                MapToolJsonContext.Default.McpRenderMapOutput);
            var inlineCaption = string.Format(
                CultureInfo.InvariantCulture,
                "Rendered {0} at {1}x{2} px {3} ({4}, {5:N0} bytes, inlined).",
                layerCountDescription,
                result.Width,
                result.Height,
                extentDescription,
                result.ContentType,
                result.Data.Length);
            if (styleNote is not null)
            {
                inlineCaption = inlineCaption + " " + styleNote;
            }

            return new McpToolsCallResult
            {
                IsError = false,
                StructuredContent = structuredContent,
                Content =
                [
                    new McpContentBlock { Type = "text", Text = inlineCaption },
                    new McpContentBlock
                    {
                        Type = "image",
                        Data = base64,
                        MimeType = result.ContentType
                    }
                ]
            };
        }

        var temporaryFileService = httpContext.RequestServices.GetRequiredService<ITemporaryFileService>();
        var href = await temporaryFileService
            .StoreTemporaryFileAsync(
                result.Data,
                result.ContentType,
                TimeSpan.FromHours(1),
                principal: httpContext.User,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var linkedOutput = BuildRenderOutput(
            result,
            bbox,
            bboxSrid,
            renderedLayers,
            new McpRenderedImage
            {
                Format = result.ContentType,
                Width = result.Width,
                Height = result.Height,
                ByteLength = result.Data.Length,
                Uri = href,
                Inlined = false
            });
        var (linkedStructuredContent, _) = McpToolHelpers.SerializeStructured(
            linkedOutput,
            MapToolJsonContext.Default.McpRenderMapOutput);

        var caption = string.Format(
            CultureInfo.InvariantCulture,
            "Rendered {0} at {1}x{2} px {3} ({4}, {5:N0} bytes). Fetch the image at {6} (expires in 1h); returned as an artifact reference to conserve context. Pass maxInlineBytes >= {5} to inline it instead.",
            layerCountDescription,
            result.Width,
            result.Height,
            extentDescription,
            result.ContentType,
            result.Data.Length,
            href);
        if (styleNote is not null)
        {
            caption = caption + " " + styleNote;
        }

        return new McpToolsCallResult
        {
            IsError = false,
            StructuredContent = linkedStructuredContent,
            Content =
            [
                new McpContentBlock { Type = "text", Text = caption },
                new McpContentBlock
                {
                    Type = "resource_link",
                    Uri = href,
                    Name = "rendered-map",
                    MimeType = result.ContentType
                }
            ]
        };
    }

    private static McpRenderMapOutput BuildRenderOutput(
        RasterResult result,
        IReadOnlyList<double> bbox,
        int bboxSrid,
        IReadOnlyList<McpRenderedLayer> layers,
        McpRenderedImage image) => new()
        {
            Format = result.ContentType,
            Width = result.Width,
            Height = result.Height,
            ByteLength = result.Data.Length,
            Bbox = bbox,
            BboxSrid = bboxSrid,
            Layers = layers,
            Image = image
        };

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

    private static async Task<StyleCatalogRecord?> ResolveAppliedStyleAsync(
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
        return styles.Count > 0 ? styles[0] : null;
    }

    private static (string StyleRendering, IReadOnlyList<string>? Unsupported) ClassifyStyleRendering(
        MetadataV2Resource resource,
        StyleCatalogRecord? appliedStyle)
    {
        if (appliedStyle is null)
        {
            return (AppliedStyleRenderSupport.Default, null);
        }

        var geometryType = resource.ReadGeometryType();
        if (IsRasterCoverage(resource))
        {
            // A raster coverage renders its native pixels; a vector style bound to it is not drawn.
            return (AppliedStyleRenderSupport.UnsupportedStyleConstruct,
                ["the applied vector style is not drawn on a raster coverage layer (coverage pixels render natively)"]);
        }

        var unsupported = AppliedStyleRenderSupport.FindUnsupportedConstructs(appliedStyle.MapLibreStyleJson, geometryType);
        return unsupported.Count == 0
            ? (AppliedStyleRenderSupport.Applied, null)
            : (AppliedStyleRenderSupport.UnsupportedStyleConstruct, unsupported);
    }

    // Mirrors the vector-aware renderer's geometry test: a resource without a geometry
    // type or geometry field renders from raster coverage pixels.
    private static bool IsRasterCoverage(MetadataV2Resource resource)
        => resource.ReadGeometryType() == MetadataV2GeometryType.None && resource.FindPrimaryGeometryField() is null;

    // The renderer resolves each layer's resource by storage layer id, so two requested
    // layers sharing a storage layer id but bound to different Metadata v2 resources
    // would both render with the first resource's identity.
    private static void EnsureDistinctStorageIdentity(
        ResolvedMapLayer[] resolvedSoFar,
        int count,
        MapToolLayerContext resolved)
    {
        for (var i = 0; i < count; i++)
        {
            var earlier = resolvedSoFar[i];
            if (earlier.LayerId == resolved.StorageLayerId &&
                !string.Equals(earlier.ResourceId, resolved.Resource.Metadata.Id, StringComparison.Ordinal))
            {
                throw new GeoprocessingValidationException(
                    $"'layers' entries {i} and {count} resolve to the same storage layer ({resolved.StorageLayerId.ToString(CultureInfo.InvariantCulture)}) "
                    + "through different published resources, which cannot be rendered with distinct identities in one image. "
                    + "Render them in separate honua_render_map calls.");
            }
        }
    }

    private static string? BuildStyleNote(McpRenderedLayer[] layers)
    {
        if (layers.Length == 0 || Array.TrueForAll(layers, layer => layer.StyleId is null))
        {
            return null;
        }

        var builder = new StringBuilder("Layer styles: ");
        for (var i = 0; i < layers.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            var layer = layers[i];
            builder.Append(layer.StyleId ?? "(default)");
            if (string.Equals(layer.StyleRendering, AppliedStyleRenderSupport.UnsupportedStyleConstruct, StringComparison.Ordinal))
            {
                builder.Append(" [unsupported-style-construct: ");
                builder.AppendJoin("; ", layer.UnsupportedStyleConstructs ?? []);
                builder.Append(']');
            }
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
