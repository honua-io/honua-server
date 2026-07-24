// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Styling.Abstractions;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Honua.Infrastructure.Rendering;

/// <summary>
/// Decorates the provider's raster-coverage <see cref="IRasterMapRenderer"/> (e.g. the
/// PostGIS raster renderer) with server-side vector styling. The provider renderer only
/// rasterizes stored raster coverages (the <c>raster_data</c> table); a bound vector style
/// applied to a feature layer was therefore never reflected in a static render, and styled
/// requests threw <see cref="NotSupportedException"/> (honua-server#2498).
/// </summary>
/// <remarks>
/// <para>Behavior is <b>raster-first, vector-fallback</b>: whenever the wrapped renderer
/// produces raster bytes (a layer backed by a raster coverage), those native bytes are
/// returned unchanged, so raster rendering — including format, GDAL encoding, temporal
/// mosaics, and multi-layer <c>ST_Union</c> merges — is untouched. Only when the raster
/// path yields no data <b>and</b> the requested layer(s) carry geometry does this decorator
/// render the features through the shared Skia pipeline
/// (<see cref="RasterMapRenderingPipeline.RenderBoundStyleVectorLayersAsync"/>), applying
/// each layer's bound MapLibre/drawingInfo style — the same styled draw path WMS GetMap,
/// MapServer export, and OGC API Maps use. This is the render path the MCP
/// <c>honua_render_map</c> tool drives, so an <c>honua_apply_style_preset</c> binding is now
/// visibly reflected in the rendered pixels on the default (Postgres) profile.</para>
/// <para>Styled vector output is encoded by Skia and therefore limited to PNG/JPEG; a TIFF
/// (or otherwise non-Skia) request that would fall back to the vector path returns an empty
/// result rather than mislabeled bytes, preserving the raster path's honest content typing.</para>
/// </remarks>
internal sealed class VectorAwareRasterMapRenderer : IRasterMapRenderer
{
    private readonly IRasterMapRenderer _inner;
    private readonly IServiceProvider _services;
    private readonly ILogger<VectorAwareRasterMapRenderer> _logger;

    public VectorAwareRasterMapRenderer(
        IRasterMapRenderer inner,
        IServiceProvider services,
        ILogger<VectorAwareRasterMapRenderer> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<RasterResult> RenderCollectionMapAsync(
        int layerId,
        MapRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        var rasterResult = await _inner.RenderCollectionMapAsync(layerId, request, cancellationToken).ConfigureAwait(false);
        if (rasterResult.Data.Length > 0)
        {
            return rasterResult;
        }

        return await TryRenderStyledVectorAsync(
            new[] { layerId },
            request,
            explicitStyleId: null,
            fallback: rasterResult,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RasterResult> RenderDatasetMapAsync(
        int[] layerIds,
        MapRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        var rasterResult = await _inner.RenderDatasetMapAsync(layerIds, request, cancellationToken).ConfigureAwait(false);
        if (rasterResult.Data.Length > 0)
        {
            return rasterResult;
        }

        return await TryRenderStyledVectorAsync(
            layerIds,
            request,
            explicitStyleId: null,
            fallback: rasterResult,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RasterResult> RenderStyledMapAsync(
        int layerId,
        string styleId,
        MapRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var resource = snapshot is null ? null : ResolveResourceForLayer(snapshot, layerId, request);
        if (resource is not null &&
            HasGeometry(resource))
        {
            var styleJson = await ResolveExplicitStyleJsonAsync(styleId, cancellationToken).ConfigureAwait(false);
            var rendered = await RenderStyledVectorLayersAsync(
                new[] { (layerId, resource) },
                request,
                styleJson,
                cancellationToken).ConfigureAwait(false);
            if (rendered.HasValue)
            {
                return rendered.Value;
            }
        }

        // Raster coverage (or a format the Skia path cannot encode): defer to the raster
        // renderer, which reports an honest NotSupported for styled raster output.
        return await _inner.RenderStyledMapAsync(layerId, styleId, request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RasterResult> TryRenderStyledVectorAsync(
        int[] layerIds,
        MapRenderRequest request,
        string? explicitStyleId,
        RasterResult fallback,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return fallback;
        }

        var geometryLayers = new List<(int LayerId, MetadataV2Resource Resource)>(layerIds.Length);
        foreach (var layerId in layerIds)
        {
            var resource = ResolveResourceForLayer(snapshot, layerId, request);
            if (resource is not null && HasGeometry(resource))
            {
                geometryLayers.Add((layerId, resource));
            }
        }

        if (geometryLayers.Count == 0)
        {
            return fallback;
        }

        var styleJson = explicitStyleId is null
            ? null
            : await ResolveExplicitStyleJsonAsync(explicitStyleId, cancellationToken).ConfigureAwait(false);

        var rendered = await RenderStyledVectorLayersAsync(geometryLayers, request, styleJson, cancellationToken)
            .ConfigureAwait(false);
        return rendered ?? fallback;
    }

    private async Task<RasterResult?> RenderStyledVectorLayersAsync(
        IReadOnlyList<(int LayerId, MetadataV2Resource Resource)> geometryLayers,
        MapRenderRequest request,
        string? explicitStyleJson,
        CancellationToken cancellationToken)
    {
        if (!TryResolveSkiaFormat(request.Format, out var format))
        {
            // TIFF / non-Skia formats stay on the raster path so returned bytes always
            // match the requested content type (#2365). Signal no vector render.
            return null;
        }

        if (request.BoundingBox is not { Length: 4 })
        {
            return null;
        }

        var featureReader = _services.GetService<IFeatureReader>();
        var styleCatalog = _services.GetService<ILayerStyleCatalog>();
        if (featureReader is null || styleCatalog is null)
        {
            return null;
        }

        var requestSrid = request.Crs ?? request.BoundingBoxCrs ?? DefaultSrid;
        var extent = new SkiaMapRenderer.RenderExtent(
            request.BoundingBox[0],
            request.BoundingBox[1],
            request.BoundingBox[2],
            request.BoundingBox[3]);

        var layers = new List<RasterMapRenderingPipeline.BoundStyleVectorLayer>(geometryLayers.Count);
        foreach (var (layerId, resource) in geometryLayers)
        {
            var geometryType = ResolveGeometryType(resource);
            var storageSrid = resource.ReadSrid() ?? requestSrid;
            layers.Add(new RasterMapRenderingPipeline.BoundStyleVectorLayer(
                layerId,
                geometryType,
                storageSrid,
                explicitStyleJson));
        }

        byte[] imageBytes;
        try
        {
            imageBytes = await RasterMapRenderingPipeline.RenderBoundStyleVectorLayersAsync(
                featureReader,
                styleCatalog,
                layers,
                extent,
                requestSrid,
                request.Width,
                request.Height,
                RasterMapRenderingPipeline.MaxFeaturesPerLayer,
                format,
                request.Transparent,
                ResolveBackgroundColor(request.BackgroundColor),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException &&
                                   RasterRenderingUnavailableException.IsNativeLoadFailure(ex))
        {
            // The native SkiaSharp rasterizer could not be initialized on this runtime
            // image (missing libSkiaSharp / fontconfig / freetype, or a wrong-arch native
            // asset). Log the real cause for operators and translate it into a typed
            // capability failure so callers (e.g. the MCP honua_render_map tool) surface an
            // actionable failed_precondition instead of a generic internal error
            // (honua-server#2770).
            RasterMapRenderingPipelineLog.RenderingRuntimeUnavailable(
                _logger, layers.Count, request.Width, request.Height, ex);
            throw new RasterRenderingUnavailableException(ex);
        }

        return new RasterResult
        {
            Data = imageBytes,
            ContentType = request.Format.ToContentType(),
            Width = request.Width,
            Height = request.Height,
            Srid = requestSrid
        };
    }

    private async Task<string?> ResolveExplicitStyleJsonAsync(string styleId, CancellationToken cancellationToken)
    {
        var styleProjection = _services.GetService<IOgcStyleProjection>();
        if (styleProjection is null || string.IsNullOrWhiteSpace(styleId))
        {
            // No projection registered (or no style requested): render with the layer's
            // bound style resolved from the catalog.
            return null;
        }

        try
        {
            var stylesheet = await styleProjection
                .GetStylesheetAsync(styleId, OgcStyleEncoding.MapboxStyle, cancellationToken)
                .ConfigureAwait(false);
            return stylesheet?.Content;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RasterMapRenderingPipelineLog.StyleResolutionFailed(_logger, styleId, ex);
            return null;
        }
    }

    private async Task<MetadataV2GraphSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var graphProvider = _services.GetService<IMetadataV2GraphProvider>();
        if (graphProvider is null)
        {
            return null;
        }

        return await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
    }

    private const int DefaultSrid = 4326;

    /// <summary>
    /// Resolves the resource for <paramref name="layerId"/>, preferring the handler-resolved
    /// identity carried on <see cref="MapRenderRequest.ResolvedLayers"/> (looked up by
    /// canonical resource id) so a colliding StorageLayerId does not re-resolve first-wins to
    /// the wrong (e.g. raster) resource. Falls back to the first-wins storage-layer index when
    /// no resolved identity is supplied (legacy callers) (#2799).
    /// </summary>
    private static MetadataV2Resource? ResolveResourceForLayer(
        MetadataV2GraphSnapshot snapshot,
        int layerId,
        MapRenderRequest request)
    {
        if (request.ResolvedLayers is { } resolved)
        {
            var resolvedLayerIndex = 0;
            while (resolvedLayerIndex < resolved.Count)
            {
                var entry = resolved[resolvedLayerIndex];
                if (entry.LayerId == layerId &&
                    snapshot.Index.ResourcesById.TryGetValue(entry.ResourceId, out var byResolvedLayer))
                {
                    return byResolvedLayer;
                }

                resolvedLayerIndex++;
            }
        }

        return snapshot.Index.ResourcesByStorageLayerId.TryGetValue(layerId, out var byLayer)
            ? byLayer
            : null;
    }

    private static bool HasGeometry(MetadataV2Resource resource)
        => resource.ReadGeometryType() != MetadataV2GeometryType.None ||
           resource.FindPrimaryGeometryField() is not null;

    private static MetadataV2GeometryType ResolveGeometryType(MetadataV2Resource resource)
    {
        var geometryType = resource.ReadGeometryType();
        // A layer that surfaces geometry only through its schema (no typed Spatial slot)
        // still renders; default to Polygon so the shared default-paint path draws a
        // filled+stroked footprint rather than the point fast-path.
        return geometryType == MetadataV2GeometryType.None
            ? MetadataV2GeometryType.Polygon
            : geometryType;
    }

    private static bool TryResolveSkiaFormat(RasterFormat format, out string skiaFormat)
    {
        switch (format)
        {
            case RasterFormat.PNG:
                skiaFormat = "png";
                return true;
            case RasterFormat.JPEG:
                skiaFormat = "jpeg";
                return true;
            default:
                skiaFormat = string.Empty;
                return false;
        }
    }

    private static SKColor? ResolveBackgroundColor(string? backgroundColor)
    {
        if (string.IsNullOrWhiteSpace(backgroundColor))
        {
            return null;
        }

        // OGC background colors arrive as 0xRRGGBB; ignore anything else and fall back to
        // the default white background used by the shared pipeline.
        var value = backgroundColor.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            value.Length == 8 &&
            uint.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var rgb))
        {
            return new SKColor(
                (byte)((rgb >> 16) & 0xFF),
                (byte)((rgb >> 8) & 0xFF),
                (byte)(rgb & 0xFF));
        }

        return null;
    }
}

/// <summary>
/// Registration for the vector-aware raster map renderer decorator.
/// </summary>
public static class VectorAwareRasterMapRendererServiceCollectionExtensions
{
    /// <summary>
    /// Wraps the currently registered <see cref="IRasterMapRenderer"/> (the provider's
    /// raster-coverage renderer) with <see cref="VectorAwareRasterMapRenderer"/> so a
    /// layer's bound vector style is applied at pixel level on the shared render path
    /// (honua-server#2498). No-op when no inner renderer is registered.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddVectorAwareRasterMapRendering(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var innerDescriptor = services.LastOrDefault(d => d.ServiceType == typeof(IRasterMapRenderer));
        if (innerDescriptor is null)
        {
            return services;
        }

        services.Remove(innerDescriptor);

        services.Add(ServiceDescriptor.Describe(
            typeof(IRasterMapRenderer),
            sp =>
            {
                var inner = (IRasterMapRenderer)InstantiateInner(innerDescriptor, sp);
                return new VectorAwareRasterMapRenderer(
                    inner,
                    sp,
                    sp.GetRequiredService<ILogger<VectorAwareRasterMapRenderer>>());
            },
            innerDescriptor.Lifetime));

        return services;
    }

    private static object InstantiateInner(ServiceDescriptor descriptor, IServiceProvider sp)
    {
        if (descriptor.ImplementationInstance is not null)
        {
            return descriptor.ImplementationInstance;
        }

        if (descriptor.ImplementationFactory is not null)
        {
            return descriptor.ImplementationFactory(sp);
        }

        if (descriptor.ImplementationType is not null)
        {
            return ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType);
        }

        throw new InvalidOperationException("Unable to resolve the inner IRasterMapRenderer implementation.");
    }
}
