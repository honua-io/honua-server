// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Progress;
using Honua.Infrastructure.Rendering;
using Honua.ServiceDefaults;
using Honua.Server.Features.PrintingTools.Layout;
using Honua.Server.Features.PrintingTools.Models;
using PrintLayoutTemplate = Honua.Server.Features.PrintingTools.Models.PrintLayoutTemplate;
using SkiaSharp;

namespace Honua.Server.Features.PrintingTools;

/// <summary>
/// Handles print service requests including synchronous execute and async job submission.
/// </summary>
internal static class PrintingToolsRequestHandlers
{
    private const int DefaultDpi = 96;
    private const int MaxDpi = 600;
    // Max pixel dimension per axis.  At MaxDpi (600) with the largest layout
    // template (A3 Landscape) page dimensions resolve to ~9 921 × 7 016 px
    // (~278 MB RGBA surface).  Layout templates are Pro-edition-gated and
    // server-defined, so the memory ceiling is bounded by design.
    private const int MaxOutputDimension = 4096;
    private const int MaxFeaturesPerLayer = 10_000;

    /// <summary>
    /// TTL for progress entries and temporary output files.
    /// </summary>
    internal static readonly TimeSpan ResultTtl = TimeSpan.FromHours(24);

    /// <summary>
    /// Processes a synchronous print execute request.
    /// Returns the composed output as bytes with content type.
    /// </summary>
    internal static async Task<(byte[] OutputBytes, string ContentType, string FileName)?> ExecuteAsync(
        WebMapDefinition webMap,
        string format,
        string templateName,
        int dpi,
        IResourceValidator resourceValidator,
        IMetadataV2GraphProvider metadataGraphProvider,
        IFeatureReader featureReader,
        ILayerStyleCatalog styleCatalog,
        ILogger logger,
        CancellationToken cancellationToken,
        ClaimsPrincipal? callerPrincipal = null,
        IAccessPolicyEvaluator? accessPolicyEvaluator = null)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("print.execute", ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, "PrintingTools");
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "execute");
        activity?.SetTag("print.template", templateName);
        activity?.SetTag("print.format", format);
        activity?.SetTag("print.dpi", dpi);
        var stopwatch = Stopwatch.StartNew();

        // Validate and resolve template
        if (!LayoutTemplateRegistry.TryGetTemplate(templateName, out var template))
        {
            return null;
        }

        // Edition gating is enforced by the endpoint layer (ValidateAndResolveRequestAsync)
        // before this method is called. No redundant check here.

        try
        {
            // Resolve visible layers once — shared by both map-frame rendering and legend
            // building to avoid duplicate service validation and style catalog calls.
            var resolvedLayers = await MaterializeVisibleLayersAsync(
                webMap,
                resourceValidator,
                metadataGraphProvider,
                styleCatalog,
                callerPrincipal,
                accessPolicyEvaluator,
                cancellationToken);

            // Render map frame
            var mapFrameBytes = await RenderMapFrameAsync(
                webMap, template, dpi, resolvedLayers, featureReader, logger, cancellationToken);

            if (mapFrameBytes is null)
            {
                return null;
            }

            // Build legend swatches for layout templates (honor showLegend option)
            List<LegendSwatchEntry>? legendEntries = null;
            if (!template.IsMapOnly && template.Legend is not null
                && webMap.LayoutOptions?.ShowLegend != false)
            {
                legendEntries = BuildLegendEntries(resolvedLayers);
            }

            // Get layout options
            var titleText = webMap.LayoutOptions?.TitleText;
            var attributionText = webMap.LayoutOptions?.CopyrightText ?? "Powered by Honua";

            // Calculate scale denominator from extent (only needed for layout templates with a scale bar)
            var scaleDenominator = template.IsMapOnly ? 0.0 : CalculateScaleDenominator(webMap, template, dpi);

            // Compose the layout — full-page dimensions for layout templates, map-frame for MAP_ONLY
            var (pageWidthPx, pageHeightPx) = template.IsMapOnly
                ? ResolveOutputDimensions(webMap, template, dpi)
                : ((int)(template.PageWidth * dpi / 72f), (int)(template.PageHeight * dpi / 72f));

            // When MAP_ONLY with custom outputSize, construct a runtime template whose
            // page/map-frame dimensions match the resolved pixels (in points) so the
            // LayoutComposer draws into the correct rect instead of the fixed Letter slot.
            var compositionTemplate = template;
            if (template.IsMapOnly)
            {
                var pagePtW = pageWidthPx * 72f / dpi;
                var pagePtH = pageHeightPx * 72f / dpi;
                compositionTemplate = new PrintLayoutTemplate
                {
                    Name = template.Name,
                    Label = template.Label,
                    PageWidth = pagePtW,
                    PageHeight = pagePtH,
                    MapFrame = new LayoutSlot { X = 0, Y = 0, Width = pagePtW, Height = pagePtH }
                };
            }

            byte[] outputBytes;
            string contentType;
            string fileName;

            if (format.Equals(PrintOutputFormat.Pdf, StringComparison.OrdinalIgnoreCase))
            {
                outputBytes = RenderPdf(compositionTemplate, pageWidthPx, pageHeightPx, mapFrameBytes, legendEntries, titleText, attributionText, scaleDenominator, dpi);
                contentType = PrintOutputFormat.GetContentType(format);
                fileName = $"print_output{PrintOutputFormat.GetExtension(format)}";
            }
            else
            {
                // Raster output (PNG, JPG)
                using var surface = SKSurface.Create(new SKImageInfo(pageWidthPx, pageHeightPx, SKColorType.Rgba8888, SKAlphaType.Premul));
                var canvas = surface.Canvas;

                LayoutComposer.Compose(canvas, compositionTemplate, mapFrameBytes, legendEntries,
                    titleText, attributionText, scaleDenominator, dpi);

                outputBytes = EncodeRaster(surface, format);
                contentType = PrintOutputFormat.GetContentType(format);
                fileName = $"print_output{PrintOutputFormat.GetExtension(format)}";
            }

            activity?.SetTag("print.output_bytes", outputBytes.Length);
            stopwatch.Stop();
            HonuaTelemetry.SetSuccess(activity);
            HonuaTelemetry.CategorizeLatency(activity, stopwatch.Elapsed.TotalMilliseconds);
            return (outputBytes, contentType, fileName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HonuaTelemetry.RecordException(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Renders the map frame image from pre-resolved operational layers.
    /// </summary>
    private static async Task<byte[]?> RenderMapFrameAsync(
        WebMapDefinition webMap,
        PrintLayoutTemplate template,
        int dpi,
        IReadOnlyList<ResolvedLayer> resolvedLayers,
        IFeatureReader featureReader,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var extent = webMap.MapOptions?.Extent;
        if (extent is null)
        {
            return null;
        }

        var (imageWidth, imageHeight) = ResolveOutputDimensions(webMap, template, dpi);

        var extentSrid = ResolveExtentSrid(extent.SpatialReference, webMap.MapOptions?.SpatialReference);

        var renderExtent = new SkiaMapRenderer.RenderExtent(
            extent.Xmin, extent.Ymin, extent.Xmax, extent.Ymax);

        // When mapOptions.scale is specified, adjust the extent to render at that
        // scale centered on the original extent (per ExportWebMap spec).
        if (webMap.MapOptions?.Scale is > 0)
        {
            renderExtent = CoordinateTransformer.AdjustExtentForScale(
                renderExtent, webMap.MapOptions.Scale.Value, imageWidth, imageHeight, dpi, extentSrid);
        }

        // Create a composite surface for all layers
        using var surface = SKSurface.Create(new SKImageInfo(imageWidth, imageHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        foreach (var resolved in resolvedLayers)
        {
            if (resolved.GeometryType is MetadataV2GeometryType.None) continue;
            var renderGeometryType = resolved.GeometryType;

            var styleLayers = resolved.StyleLayers;

            // Query features for the (potentially scale-adjusted) render extent
            var serviceSrid = ResolveServiceSrid(resolved.Service, resolved.Resource);
            var spatialFilter = SpatialFilterHelpers.CreateBboxSpatialFilter(
                renderExtent.MinX, renderExtent.MinY, renderExtent.MaxX, renderExtent.MaxY, extentSrid);
            var featureQuery = new FeatureQuery
            {
                SpatialFilter = spatialFilter,
                SpatialReferenceSrid = serviceSrid,
                OutputSrid = extentSrid,
                Limit = MaxFeaturesPerLayer
            };
            var queryResult = await featureReader.QueryAsync(resolved.StorageLayerId, featureQuery, cancellationToken);

            if (queryResult.Items.Length == 0) continue;

            // Render this layer's features directly onto the composite canvas
            using var renderer = new SkiaMapRenderer();
            if (resolved.OperationalLayer.Opacity.HasValue && resolved.OperationalLayer.Opacity.Value < 1.0)
            {
                using var opacityPaint = new SKPaint { Color = SKColors.White.WithAlpha((byte)(resolved.OperationalLayer.Opacity.Value * 255)) };
                var count = canvas.SaveLayer(opacityPaint);
                renderer.RenderFeaturesOnCanvas(canvas, queryResult.Items, styleLayers, renderExtent, imageWidth, imageHeight, renderGeometryType);
                canvas.RestoreToCount(count);
            }
            else
            {
                renderer.RenderFeaturesOnCanvas(canvas, queryResult.Items, styleLayers, renderExtent, imageWidth, imageHeight, renderGeometryType);
            }
        }

        // Encode the composite map frame as PNG
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Builds legend swatch entries from pre-resolved operational layers.
    /// </summary>
    private static List<LegendSwatchEntry> BuildLegendEntries(
        IReadOnlyList<ResolvedLayer> resolvedLayers)
    {
        var entries = new List<LegendSwatchEntry>();

        foreach (var resolved in resolvedLayers)
        {
            var styleLayers = resolved.StyleLayers;
            var renderGeometryType = resolved.GeometryType;
            var label = resolved.OperationalLayer.Title
                ?? resolved.Publication.TitleOverride
                ?? resolved.Resource.Metadata.Title
                ?? resolved.Resource.Metadata.Name;

            if (styleLayers.Length == 0)
            {
                var defaultSwatch = SkiaMapRenderer.RenderLegendSwatch(
                    new MapLibreStyleLayer { Type = "default" }, renderGeometryType);
                entries.Add(new LegendSwatchEntry
                {
                    Label = label,
                    SwatchBytes = defaultSwatch
                });
            }
            else
            {
                foreach (var sl in styleLayers)
                {
                    if (sl.Type is null or "background") continue;
                    var swatch = SkiaMapRenderer.RenderLegendSwatch(sl, renderGeometryType);
                    entries.Add(new LegendSwatchEntry
                    {
                        Label = sl.Id ?? label,
                        SwatchBytes = swatch
                    });
                }
            }
        }

        return entries;
    }

    /// <summary>
    /// Materializes the visible layers into a list so they can be shared between
    /// map-frame rendering and legend building without duplicate service validation
    /// or style catalog lookups.
    /// </summary>
    private static async Task<IReadOnlyList<ResolvedLayer>> MaterializeVisibleLayersAsync(
        WebMapDefinition webMap,
        IResourceValidator resourceValidator,
        IMetadataV2GraphProvider metadataGraphProvider,
        ILayerStyleCatalog styleCatalog,
        ClaimsPrincipal? callerPrincipal,
        IAccessPolicyEvaluator? accessPolicyEvaluator,
        CancellationToken cancellationToken)
    {
        var layers = new List<ResolvedLayer>();
        var snapshot = await metadataGraphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var resolved in ResolveVisibleLayersAsync(
            webMap,
            resourceValidator,
            snapshot,
            callerPrincipal,
            accessPolicyEvaluator,
            cancellationToken))
        {
            // Pre-fetch and parse style so both render and legend paths share the result
            var style = await styleCatalog.GetLayerStyleAsync(resolved.StorageLayerId, cancellationToken);
            var styleLayers = StyleTranslator.ParseStyleLayers(style?.MapLibreStyleJson);
            layers.Add(resolved with { StyleLayers = styleLayers });
        }
        return layers;
    }

    /// <summary>
    /// Resolves visible operational layers to their service publications and resources,
    /// handling URL parsing, service validation, storage-layer resolution, and access policy.
    /// </summary>
    private static async IAsyncEnumerable<ResolvedLayer> ResolveVisibleLayersAsync(
        WebMapDefinition webMap,
        IResourceValidator resourceValidator,
        MetadataV2GraphSnapshot snapshot,
        ClaimsPrincipal? callerPrincipal,
        IAccessPolicyEvaluator? accessPolicyEvaluator,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var operationalLayers = webMap.OperationalLayers ?? [];

        foreach (var opLayer in operationalLayers)
        {
            if (opLayer.Visibility == false) continue;

            ResolveLayerFromUrl(opLayer);
            if (opLayer.ResolvedServiceId is null) continue;

            var serviceResult = await resourceValidator.ValidateServiceV2Async(
                opLayer.ResolvedServiceId, cancellationToken);
            if (!serviceResult.IsValid || serviceResult.Resource is null) continue;

            var service = serviceResult.Resource;
            var targetLayers = opLayer.ResolvedLayerId.HasValue
                ? ResolveSinglePublication(snapshot, service.Metadata.Id, opLayer.ResolvedLayerId.Value)
                : snapshot.Index.PublicationsByService[service.Metadata.Id]
                    .Where(publication => ResolveResource(snapshot, publication)?.Display?.DefaultVisibility ?? true);

            foreach (var publication in targetLayers)
            {
                var resource = ResolveResource(snapshot, publication);
                if (resource is null) continue;

                var storageLayerId = snapshot.ResolveStorageLayerId(publication)
                    ?? snapshot.ResolveStorageLayerId(resource);
                if (!storageLayerId.HasValue) continue;

                if (callerPrincipal is not null && accessPolicyEvaluator is not null &&
                    !accessPolicyEvaluator.Evaluate(
                        callerPrincipal,
                        resource.AccessPolicy,
                        service.AccessPolicy).IsAllowed)
                    continue;

                yield return new ResolvedLayer(
                    opLayer,
                    service,
                    publication,
                    resource,
                    storageLayerId.Value,
                    resource.ReadGeometryType(),
                    []);
            }
        }
    }

    private static IEnumerable<MetadataV2Publication> ResolveSinglePublication(
        MetadataV2GraphSnapshot snapshot,
        string serviceId,
        int layerIndex)
    {
        var publication = snapshot.FindPublicationByLayerIndex(serviceId, layerIndex);
        if (publication is not null)
        {
            yield return publication;
        }
    }

    private static MetadataV2Resource? ResolveResource(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Publication publication)
        => snapshot.ResolveResource(publication);

    private static int ResolveServiceSrid(MetadataV2Service service, MetadataV2Resource resource)
        => service.SpatialReference?.ResolveSrid()
            ?? resource.ReadSrid()
            ?? 4326;

    /// <summary>
    /// A resolved operational layer with its parent service, target publication/resource,
    /// and pre-fetched style layers to avoid duplicate catalog lookups.
    /// </summary>
    private readonly record struct ResolvedLayer(
        WebMapOperationalLayer OperationalLayer,
        MetadataV2Service Service,
        MetadataV2Publication Publication,
        MetadataV2Resource Resource,
        int StorageLayerId,
        MetadataV2GeometryType GeometryType,
        MapLibreStyleLayer[] StyleLayers);

    /// <summary>
    /// Renders the composed layout to PDF using SkiaSharp's SKDocument.
    /// </summary>
    private static byte[] RenderPdf(
        PrintLayoutTemplate template,
        int resolvedWidthPx,
        int resolvedHeightPx,
        byte[] mapFrameBytes,
        IReadOnlyList<LegendSwatchEntry>? legendEntries,
        string? titleText,
        string? attributionText,
        double scaleDenominator,
        int dpi)
    {
        using var stream = new MemoryStream();
        var metadata = new SKDocumentPdfMetadata
        {
            Title = titleText ?? "Map Export",
            Creator = "Honua Print Service",
            Producer = "Honua Server (SkiaSharp)"
        };

        using var document = SKDocument.CreatePdf(stream, metadata);

        // For MAP_ONLY, derive PDF page size from the resolved pixel dimensions.
        // PDF operates at 72 DPI natively, so convert pixels → points.
        float pageWidth, pageHeight;
        if (template.IsMapOnly)
        {
            pageWidth = resolvedWidthPx * 72f / dpi;
            pageHeight = resolvedHeightPx * 72f / dpi;
        }
        else
        {
            pageWidth = template.PageWidth;
            pageHeight = template.PageHeight;
        }

        using var pageCanvas = document.BeginPage(pageWidth, pageHeight);
        LayoutComposer.Compose(pageCanvas, template, mapFrameBytes, legendEntries,
            titleText, attributionText, scaleDenominator, 72); // PDF uses 72 DPI natively

        document.EndPage();
        document.Close();

        return stream.ToArray();
    }

    /// <summary>
    /// Encodes a surface to the requested raster format.
    /// </summary>
    private static byte[] EncodeRaster(SKSurface surface, string format)
    {
        return SkiaMapRenderer.EncodeSurface(surface, format.ToUpperInvariant() switch
        {
            "JPG" => "jpg",
            _ => "png"
        });
    }

    /// <summary>
    /// Extracts service ID and layer ID from an operational layer URL.
    /// </summary>
    internal static void ResolveLayerFromUrl(WebMapOperationalLayer layer)
    {
        if (string.IsNullOrWhiteSpace(layer.Url)) return;

        // Match patterns like:
        // .../rest/services/{serviceId}/MapServer/{layerId}
        // .../rest/services/{serviceId}/FeatureServer/{layerId}
        // Note: folder-based service URLs ({folder}/{serviceId}/MapServer/...) are
        // not yet supported; such layers will be silently skipped.
        var url = layer.Url;
        var restIdx = url.IndexOf("/rest/services/", StringComparison.OrdinalIgnoreCase);
        if (restIdx < 0) return;

        var afterServices = url[(restIdx + "/rest/services/".Length)..];
        var segments = afterServices.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Expect: serviceId/MapServer|FeatureServer/layerId
        if (segments.Length >= 3 &&
            (segments[1].Equals("MapServer", StringComparison.OrdinalIgnoreCase) ||
             segments[1].Equals("FeatureServer", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(segments[2], out var layerId))
        {
            layer.ResolvedServiceId = segments[0];
            layer.ResolvedLayerId = layerId;
        }
        else if (segments.Length >= 2 &&
                 (segments[1].Equals("MapServer", StringComparison.OrdinalIgnoreCase) ||
                  segments[1].Equals("FeatureServer", StringComparison.OrdinalIgnoreCase)))
        {
            // Service-level URL — ResolvedLayerId stays null to indicate
            // "all default-visible layers" per ExportWebMap spec.
            layer.ResolvedServiceId = segments[0];
        }
    }

    /// <summary>
    /// Parses the web map JSON into a strongly typed definition.
    /// </summary>
    internal static WebMapDefinition? ParseWebMapJson(string? webMapJson)
    {
        if (string.IsNullOrWhiteSpace(webMapJson)) return null;

        try
        {
            return JsonSerializer.Deserialize(webMapJson, PrintingToolsJsonContext.Default.WebMapDefinition);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the output format from the request.
    /// </summary>
    internal static string ResolveFormat(string? format) =>
        string.IsNullOrWhiteSpace(format) ? PrintOutputFormat.Png32 : format.Trim();

    /// <summary>
    /// Resolves the DPI from the web map definition.
    /// </summary>
    internal static int ResolveDpi(WebMapDefinition? webMap)
    {
        var dpi = webMap?.ExportOptions?.Dpi ?? DefaultDpi;
        return Math.Clamp(dpi, 72, MaxDpi);
    }

    /// <summary>
    /// Computes effective output pixel dimensions, honoring OutputSize for MAP_ONLY templates.
    /// </summary>
    private static (int Width, int Height) ResolveOutputDimensions(WebMapDefinition webMap, PrintLayoutTemplate template, int dpi)
    {
        var outputSize = webMap.ExportOptions?.OutputSize;
        if (template.IsMapOnly && outputSize is { Length: >= 2 } && outputSize[0] > 0 && outputSize[1] > 0)
        {
            // Spec: outputSize is [width, height] — clamp to MaxOutputDimension per MapServer parity
            return (Math.Clamp(outputSize[0], 1, MaxOutputDimension),
                    Math.Clamp(outputSize[1], 1, MaxOutputDimension));
        }

        if (template.IsMapOnly && outputSize is [> 0])
        {
            var clampedW = Math.Clamp(outputSize[0], 1, MaxOutputDimension);
            // Single-element fallback: derive height from extent aspect ratio
            var extent = webMap.MapOptions?.Extent;
            if (extent is not null)
            {
                var extentWidth = extent.Xmax - extent.Xmin;
                var extentHeight = extent.Ymax - extent.Ymin;
                var aspect = extentWidth > 0 ? extentHeight / extentWidth : 1.0;
                return (clampedW, Math.Clamp(Math.Max(1, (int)(clampedW * aspect)), 1, MaxOutputDimension));
            }

            return (clampedW, clampedW);
        }

        return ((int)(template.MapFrame.Width * dpi / 72f), (int)(template.MapFrame.Height * dpi / 72f));
    }

    private static double CalculateScaleDenominator(WebMapDefinition webMap, PrintLayoutTemplate template, int dpi)
    {
        if (webMap.MapOptions?.Scale is > 0)
        {
            return webMap.MapOptions.Scale.Value;
        }

        var extent = webMap.MapOptions?.Extent;
        if (extent is null) return 0;

        var renderExtent = new SkiaMapRenderer.RenderExtent(
            extent.Xmin, extent.Ymin, extent.Xmax, extent.Ymax);
        var imageWidth = (int)(template.MapFrame.Width * dpi / 72f);
        var srid = ResolveExtentSrid(extent.SpatialReference, webMap.MapOptions?.SpatialReference);

        return CoordinateTransformer.CalculateScaleDenominator(renderExtent, imageWidth, dpi, srid);
    }

    /// <summary>
    /// Resolves the canonical EPSG SRID from an ArcGIS spatial reference,
    /// preferring latestWkid (EPSG) over wkid (which may be an Esri alias).
    /// Falls back to <paramref name="mapLevelSr"/> when the extent-level SR has no WKID,
    /// per the ExportWebMap spec.
    /// </summary>
    internal static int ResolveExtentSrid(WebMapSpatialReference? spatialReference, WebMapSpatialReference? mapLevelSr = null)
    {
        var srid = spatialReference?.LatestWkid ?? spatialReference?.Wkid;
        if (srid.HasValue) return srid.Value;

        var fallback = mapLevelSr?.LatestWkid ?? mapLevelSr?.Wkid;
        return fallback ?? 4326;
    }

    /// <summary>
    /// Validates that the web map definition includes a renderable extent with a
    /// resolvable spatial reference. Returns an error message if invalid, null if valid.
    /// </summary>
    internal static string? ValidateWebMapExtent(WebMapDefinition webMap)
    {
        var extent = webMap.MapOptions?.Extent;
        if (extent is null)
            return "Web map definition must include mapOptions.extent.";

        var sr = extent.SpatialReference;
        // If a spatial reference is provided with only WKT (no WKID), we cannot
        // reliably resolve the SRID without a database lookup. Reject early rather
        // than silently defaulting to EPSG:4326.
        if (sr is not null && sr.Wkid is null && sr.LatestWkid is null && !string.IsNullOrWhiteSpace(sr.Wkt))
            return "Spatial reference with only WKT is not supported for print requests. Provide wkid or latestWkid.";

        // Apply the same check to the map-level spatial reference which is used as
        // fallback when the extent has no SR (see ResolveExtentSrid).  Without this
        // guard a WKT-only map-level SR silently defaults to EPSG:4326.
        var mapSr = webMap.MapOptions?.SpatialReference;
        if (mapSr is not null && mapSr.Wkid is null && mapSr.LatestWkid is null && !string.IsNullOrWhiteSpace(mapSr.Wkt))
            return "Map-level spatial reference with only WKT is not supported for print requests. Provide wkid or latestWkid.";

        return null;
    }

    /// <summary>
    /// Validates that MAP_ONLY requests include the required exportOptions.outputSize.
    /// Per the ExportWebMap spec, outputSize must be defined when layout_template is MAP_ONLY.
    /// Returns an error message if invalid, null if valid.
    /// </summary>
    internal static string? ValidateMapOnlyOutputSize(WebMapDefinition webMap, string templateName)
    {
        if (!LayoutTemplateRegistry.TryGetTemplate(templateName, out var template))
            return null;

        if (!template.IsMapOnly)
            return null;

        var outputSize = webMap.ExportOptions?.OutputSize;
        if (outputSize is { Length: >= 2 } && outputSize[0] > 0 && outputSize[1] > 0)
        {
            if (outputSize[0] > MaxOutputDimension || outputSize[1] > MaxOutputDimension)
                return $"outputSize dimensions must not exceed {MaxOutputDimension}. Requested: [{outputSize[0]}, {outputSize[1]}].";
            return null;
        }

        if (outputSize is [> 0])
        {
            if (outputSize[0] > MaxOutputDimension)
                return $"outputSize width must not exceed {MaxOutputDimension}. Requested: {outputSize[0]}.";
            return null;
        }

        return "exportOptions.outputSize is required when Layout_Template is MAP_ONLY.";
    }

    /// <summary>
    /// Validates that the current edition allows the requested template and format.
    /// Returns the blocked reason if gated, or null if allowed.
    /// The caller must resolve the template first; this method skips the check
    /// if <paramref name="resolvedTemplate"/> is null.
    /// </summary>
    internal static string? ValidateEdition(
        PrintLayoutTemplate? resolvedTemplate,
        string format,
        HonuaEdition edition,
        ILogger logger)
    {
        if (resolvedTemplate is null)
        {
            return null; // Template validation handled separately
        }

        if (edition >= HonuaEdition.Pro) return null;

        if (!resolvedTemplate.IsMapOnly)
        {
            PrintingToolsLog.EditionGateBlocked(logger, "layout_template");
            return "Layout templates require Pro edition or higher.";
        }

        if (format.Equals(PrintOutputFormat.Pdf, StringComparison.OrdinalIgnoreCase))
        {
            PrintingToolsLog.EditionGateBlocked(logger, "pdf_output");
            return "PDF output requires Pro edition or higher.";
        }

        return null;
    }

    /// <summary>
    /// Collects informational warning messages for unsupported web map features
    /// (e.g. baseMap presence) so they can be included in GP task responses.
    /// </summary>
    internal static PrintJobMessage[] CollectWarnings(WebMapDefinition webMap, ILogger logger)
    {
        var warnings = new List<PrintJobMessage>();

        if (webMap.BaseMap is { } bm && bm.ValueKind != System.Text.Json.JsonValueKind.Null)
        {
            PrintingToolsLog.BaseMapNotSupported(logger);
            warnings.Add(new PrintJobMessage
            {
                Type = "esriJobMessageTypeWarning",
                Description = "The baseMap element is not supported and was omitted from the output. Only operationalLayers resolved to local services are rendered."
            });
        }

        return [.. warnings];
    }

}
