// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Infrastructure.Rendering;
using Honua.Server.Features.Styling.Sld;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Features.Styling;

/// <summary>
/// <see cref="IOgcStyleProjection"/> implementation that projects Honua's per-layer
/// style storage as OGC API - Styles resources (ADR-0048, Phase 1). It composes the
/// internal <see cref="ILayerStyleService"/>, the canonical MapLibre store
/// (<see cref="ILayerStyleCatalog"/>), the SLD exporter, and the metadata-v2 graph so
/// the protocol slice in <c>Honua.Protocols.OgcApi</c> never needs to reach those
/// internal types directly.
/// </summary>
internal sealed class OgcStyleProjection : IOgcStyleProjection
{
    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly ILayerStyleService _styleService;
    private readonly ILayerStyleCatalog _styleCatalog;
    private readonly IGeoServicesStyleConverter _geoServicesConverter;
    private readonly IStyleCatalog? _independentStyleCatalog;
    private readonly IMetadataV2StyleGraphSync? _styleGraphSync;
    private readonly ILogger _logger;

    public OgcStyleProjection(
        IMetadataV2GraphProvider graphProvider,
        ILayerStyleService styleService,
        ILayerStyleCatalog styleCatalog,
        IGeoServicesStyleConverter geoServicesConverter,
        IStyleCatalog? independentStyleCatalog = null,
        IMetadataV2StyleGraphSync? styleGraphSync = null,
        ILogger<OgcStyleProjection>? logger = null)
    {
        _logger = logger ?? NullLogger<OgcStyleProjection>.Instance;
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        _styleService = styleService ?? throw new ArgumentNullException(nameof(styleService));
        _styleCatalog = styleCatalog ?? throw new ArgumentNullException(nameof(styleCatalog));
        _geoServicesConverter = geoServicesConverter ?? throw new ArgumentNullException(nameof(geoServicesConverter));
        _independentStyleCatalog = independentStyleCatalog;
        _styleGraphSync = styleGraphSync;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OgcStyleSummary>> ListStylesAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        var summaries = new List<OgcStyleSummary>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<(string StyleId, MetadataV2Resource Resource, int StorageLayerId)>();
        foreach (var resource in snapshot.Graph.Resources)
        {
            var styleId = resource.Metadata.Name;
            if (string.IsNullOrWhiteSpace(styleId) || !seen.Add(styleId))
            {
                continue;
            }

            var storageLayerId = snapshot.ResolveStorageLayerId(resource);
            if (!storageLayerId.HasValue)
            {
                continue;
            }

            candidates.Add((styleId, resource, storageLayerId.Value));
        }

        // Phase 1: a collection projects to an OGC style only when it has a
        // genuinely stored MapLibre style. Read the canonical store directly so the
        // in-memory default style synthesized for unstyled layers does not appear.
        // Fetch with bounded fan-out rather than one store round trip per resource
        // in sequence, so listing latency does not grow linearly with catalog size.
        // (A batch lookup on ILayerStyleCatalog would collapse this to one query.)
        const int lookupFanOut = 16;
        for (var offset = 0; offset < candidates.Count; offset += lookupFanOut)
        {
            var batch = candidates.GetRange(offset, Math.Min(lookupFanOut, candidates.Count - offset));
            var stored = await Task.WhenAll(
                batch.Select(candidate => _styleCatalog.GetLayerStyleAsync(candidate.StorageLayerId, cancellationToken)))
                .ConfigureAwait(false);

            for (var i = 0; i < batch.Count; i++)
            {
                if (stored[i] is { } style && !string.IsNullOrWhiteSpace(style.MapLibreStyleJson))
                {
                    summaries.Add(new OgcStyleSummary(batch[i].StyleId, ResolveTitle(batch[i].Resource)));
                }
            }
        }

        // Phase 2: also surface standalone catalog styles that are not already
        // represented as a Phase 1 collection-keyed style.
        if (_independentStyleCatalog is not null)
        {
            var catalogStyles = await _independentStyleCatalog.ListStylesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var style in catalogStyles)
            {
                if (!seen.Add(style.StyleId))
                {
                    continue;
                }

                summaries.Add(new OgcStyleSummary(
                    style.StyleId,
                    string.IsNullOrWhiteSpace(style.Title) ? style.StyleId : style.Title!));
            }
        }

        summaries.Sort(static (a, b) => string.CompareOrdinal(a.StyleId, b.StyleId));
        return summaries;
    }

    /// <inheritdoc />
    public async Task<OgcStylesheet?> GetStylesheetAsync(
        string styleId,
        OgcStyleEncoding encoding,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(styleId);

        var (resource, storageLayerId, _) = await ResolveStyledResourceAsync(styleId, cancellationToken).ConfigureAwait(false);
        if (resource is null || !storageLayerId.HasValue)
        {
            // Phase 2: the styleId may identify a standalone catalog style rather than a
            // Phase 1 collection-keyed style. Serve it from the independent catalog.
            return await GetCatalogStylesheetAsync(styleId, encoding, cancellationToken).ConfigureAwait(false);
        }

        var stored = await _styleCatalog.GetLayerStyleAsync(storageLayerId.Value, cancellationToken).ConfigureAwait(false);
        if (stored is null || string.IsNullOrWhiteSpace(stored.MapLibreStyleJson))
        {
            return await GetCatalogStylesheetAsync(styleId, encoding, cancellationToken).ConfigureAwait(false);
        }

        var mapLibreJson = stored.MapLibreStyleJson!;
        if (encoding == OgcStyleEncoding.MapboxStyle)
        {
            return new OgcStylesheet(mapLibreJson, OgcStyleMediaTypes.MapboxStyle, OgcStyleEncoding.MapboxStyle);
        }

        if (encoding == OgcStyleEncoding.EsriDrawingInfo)
        {
            // Esri renderer is a server-side projection of the canonical MapLibre style (ADR-0002): the style
            // service back-generates drawingInfo from the stored MapLibre. The console never converts.
            var drawingInfo = await _styleService
                .GetDrawingInfoAsync(resource, storageLayerId.Value, cancellationToken)
                .ConfigureAwait(false);
            if (drawingInfo is not { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) })
            {
                return null;
            }

            return new OgcStylesheet(
                drawingInfo.Value.GetRawText(),
                OgcStyleMediaTypes.EsriDrawingInfo,
                OgcStyleEncoding.EsriDrawingInfo);
        }

        var sld = DeriveSld(mapLibreJson, resource, storageLayerId.Value, encoding);
        return sld;
    }

    private async Task<OgcStylesheet?> GetCatalogStylesheetAsync(
        string styleId,
        OgcStyleEncoding encoding,
        CancellationToken cancellationToken)
    {
        if (_independentStyleCatalog is null)
        {
            return null;
        }

        var style = await _independentStyleCatalog.GetStyleAsync(styleId, cancellationToken).ConfigureAwait(false);
        if (style is null || string.IsNullOrWhiteSpace(style.MapLibreStyleJson))
        {
            return null;
        }

        if (encoding == OgcStyleEncoding.MapboxStyle)
        {
            return new OgcStylesheet(style.MapLibreStyleJson, OgcStyleMediaTypes.MapboxStyle, OgcStyleEncoding.MapboxStyle);
        }

        if (encoding == OgcStyleEncoding.EsriDrawingInfo)
        {
            // ADR-0002: the canonical document is the stored MapLibre style, so the Esri
            // renderer is always back-generated from it here rather than served from the
            // catalog's cached drawingInfo column — a cache written by an earlier edit must
            // never shadow the canonical style a later MapLibre PUT replaced it with.
            var descriptor = StandaloneStyleDescriptor.FromMapLibre(style.StyleId, style.MapLibreStyleJson!);
            var drawingInfoJson = MapLibreToGeoServicesConverter.Convert(style.MapLibreStyleJson!, descriptor);
            return new OgcStylesheet(
                drawingInfoJson,
                OgcStyleMediaTypes.EsriDrawingInfo,
                OgcStyleEncoding.EsriDrawingInfo);
        }

        return DeriveSldFromMapLibre(style.MapLibreStyleJson, style.StyleId, encoding);
    }

    /// <inheritdoc />
    public async Task<OgcStyleMetadata?> GetStyleMetadataAsync(
        string styleId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(styleId);

        var (resource, storageLayerId, _) = await ResolveStyledResourceAsync(styleId, cancellationToken).ConfigureAwait(false);
        if (resource is null || !storageLayerId.HasValue)
        {
            return await GetCatalogStyleMetadataAsync(styleId, cancellationToken).ConfigureAwait(false);
        }

        var stored = await _styleCatalog.GetLayerStyleAsync(storageLayerId.Value, cancellationToken).ConfigureAwait(false);
        if (stored is null || string.IsNullOrWhiteSpace(stored.MapLibreStyleJson))
        {
            return await GetCatalogStyleMetadataAsync(styleId, cancellationToken).ConfigureAwait(false);
        }

        var version = stored.StyleVersion > 0
            ? stored.StyleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;

        return new OgcStyleMetadata(
            styleId,
            ResolveTitle(resource),
            resource.Metadata.Description,
            resource.Metadata.Keywords,
            resource.Metadata.License,
            version);
    }

    private async Task<OgcStyleMetadata?> GetCatalogStyleMetadataAsync(string styleId, CancellationToken cancellationToken)
    {
        if (_independentStyleCatalog is null)
        {
            return null;
        }

        var style = await _independentStyleCatalog.GetStyleAsync(styleId, cancellationToken).ConfigureAwait(false);
        if (style is null)
        {
            return null;
        }

        var version = style.StyleVersion > 0
            ? style.StyleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;

        return new OgcStyleMetadata(
            style.StyleId,
            string.IsNullOrWhiteSpace(style.Title) ? style.StyleId : style.Title!,
            style.Description,
            Array.Empty<string>(),
            License: null,
            version);
    }

    /// <inheritdoc />
    public async Task<OgcStyleUpdateResult> UpdateStyleAsync(
        string styleId,
        string mapLibreStyleJson,
        bool strict,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(styleId);
        ArgumentNullException.ThrowIfNull(mapLibreStyleJson);

        var (resource, storageLayerId, _) = await ResolveResourceAsync(styleId, cancellationToken).ConfigureAwait(false);
        if (resource is null || !storageLayerId.HasValue)
        {
            // Phase 2: the styleId may identify a catalog style rather than a collection-keyed
            // one. Keep PUT symmetric with POST/DELETE, which already reach the catalog.
            var bound = await ResolveMirroredLayerStyleAsync(styleId, cancellationToken).ConfigureAwait(false);
            if (bound is null)
            {
                return await UpdateCatalogStyleAsync(styleId, mapLibreStyleJson, strict, cancellationToken).ConfigureAwait(false);
            }

            (resource, storageLayerId) = (bound.Value.Resource, bound.Value.StorageLayerId);
        }

        JsonElement parsed;
        try
        {
            using var document = JsonDocument.Parse(mapLibreStyleJson);
            parsed = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return new OgcStyleUpdateResult(OgcStyleUpdateStatus.Invalid, $"MapLibre style is not valid JSON: {ex.Message}");
        }

        var result = await _styleService
            .UpdateStyleAsync(resource, storageLayerId.Value, parsed, drawingInfo: null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            LayerStyleUpdateStatus.Updated => new OgcStyleUpdateResult(OgcStyleUpdateStatus.Updated, null),
            LayerStyleUpdateStatus.NotFound => new OgcStyleUpdateResult(OgcStyleUpdateStatus.NotFound, $"Style '{styleId}' not found."),
            _ => new OgcStyleUpdateResult(OgcStyleUpdateStatus.Invalid, result.ErrorMessage ?? "MapLibre style is invalid.")
        };
    }

    /// <inheritdoc />
    public async Task<OgcStyleUpdateResult> UpdateStyleFromDrawingInfoAsync(
        string styleId,
        string drawingInfoJson,
        bool strict,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(styleId);
        ArgumentNullException.ThrowIfNull(drawingInfoJson);

        JsonElement drawingInfo;
        try
        {
            using var document = JsonDocument.Parse(drawingInfoJson);
            drawingInfo = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return new OgcStyleUpdateResult(OgcStyleUpdateStatus.Invalid, $"drawingInfo is not valid JSON: {ex.Message}");
        }

        if (!TryValidateDrawingInfoShape(drawingInfo, out var shapeError))
        {
            return new OgcStyleUpdateResult(
                OgcStyleUpdateStatus.Invalid,
                shapeError);
        }

        var (resource, storageLayerId, _) = await ResolveResourceAsync(styleId, cancellationToken).ConfigureAwait(false);
        if (resource is null || !storageLayerId.HasValue)
        {
            // Phase 2: catalog styles accept the negotiated Esri encoding on write too, so
            // the console's drawingInfo authoring mode reaches them.
            var bound = await ResolveMirroredLayerStyleAsync(styleId, cancellationToken).ConfigureAwait(false);
            if (bound is null)
            {
                return await UpdateCatalogStyleFromDrawingInfoAsync(styleId, drawingInfo, strict, cancellationToken).ConfigureAwait(false);
            }

            (resource, storageLayerId) = (bound.Value.Resource, bound.Value.StorageLayerId);
        }

        // Convert Esri drawingInfo -> canonical MapLibre server-side (ADR-0002), capturing lossy symbolizers.
        var geometryType = resource.Spatial?.GeometryType ?? MetadataV2GeometryType.None;
        var conversion = _geoServicesConverter.Convert(
            drawingInfo,
            storageLayerId.Value,
            string.IsNullOrWhiteSpace(resource.Metadata.Name) ? styleId : resource.Metadata.Name,
            geometryType);

        var warnings = conversion.Unsupported.Count == 0
            ? null
            : conversion.Unsupported.Select(u => $"{u.Code} ({u.SymbolizerType}): {u.Guidance}").ToArray();

        // Strict: never persist a lossy conversion — reject so the operator can adjust the renderer.
        if (strict && warnings is { Length: > 0 })
        {
            return new OgcStyleUpdateResult(
                OgcStyleUpdateStatus.Invalid,
                "The renderer uses features the canonical MapLibre style cannot represent. Resubmit without strict handling to accept the lossy conversion.",
                warnings);
        }

        JsonElement mapLibre;
        try
        {
            using var document = JsonDocument.Parse(conversion.MapLibreStyleJson);
            mapLibre = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return new OgcStyleUpdateResult(OgcStyleUpdateStatus.Invalid, $"Converted MapLibre style is not valid JSON: {ex.Message}", warnings);
        }

        var result = await _styleService
            .UpdateStyleAsync(resource, storageLayerId.Value, mapLibre, drawingInfo: null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            LayerStyleUpdateStatus.Updated => new OgcStyleUpdateResult(OgcStyleUpdateStatus.Updated, null, warnings),
            LayerStyleUpdateStatus.NotFound => new OgcStyleUpdateResult(OgcStyleUpdateStatus.NotFound, $"Style '{styleId}' not found."),
            _ => new OgcStyleUpdateResult(OgcStyleUpdateStatus.Invalid, result.ErrorMessage ?? "drawingInfo is invalid.", warnings)
        };
    }

    // A catalog style whose id is not a collection name may be a mirrored layer default. These
    // per-layer defaults are stored as "style-layer-{id}" at ordinal zero and listed
    // through this surface. Only that reserved mirror writes through to the canonical per-layer
    // store; ordinary associated styles remain independently editable catalog records.
    private async Task<(MetadataV2Resource Resource, int StorageLayerId)?> ResolveMirroredLayerStyleAsync(
        string styleId,
        CancellationToken cancellationToken)
    {
        const string mirroredStylePrefix = "style-layer-";
        if (_independentStyleCatalog is null
            || !styleId.StartsWith(mirroredStylePrefix, StringComparison.Ordinal)
            || !int.TryParse(
                styleId.AsSpan(mirroredStylePrefix.Length),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var storageLayerId))
        {
            return null;
        }

        var associations = await _independentStyleCatalog.ListAssociationsAsync(cancellationToken).ConfigureAwait(false);
        if (!associations.Any(candidate =>
                candidate.LayerId == storageLayerId
                && candidate.Ordinal == 0
                && string.Equals(candidate.StyleId, styleId, StringComparison.Ordinal)))
        {
            return null;
        }

        var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Index.ResourcesByStorageLayerId.TryGetValue(storageLayerId, out var resource) && resource is not null
            ? (resource, storageLayerId)
            : null;
    }

    // Phase 2 write path: update a standalone catalog style from a MapLibre document.
    // Applies the same validation POST does — a standalone style has no layer binding, so
    // the per-layer normalizer (which requires a Honua tile source) cannot be used.
    private async Task<OgcStyleUpdateResult> UpdateCatalogStyleAsync(
        string styleId,
        string mapLibreStyleJson,
        bool strict,
        CancellationToken cancellationToken)
    {
        if (_independentStyleCatalog is null)
        {
            return new OgcStyleUpdateResult(OgcStyleUpdateStatus.NotFound, $"Style '{styleId}' not found.");
        }

        var existing = await _independentStyleCatalog.GetStyleAsync(styleId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return new OgcStyleUpdateResult(OgcStyleUpdateStatus.NotFound, $"Style '{styleId}' not found.");
        }

        if (!TryValidateStandaloneMapLibre(mapLibreStyleJson, strict, out var error))
        {
            return new OgcStyleUpdateResult(OgcStyleUpdateStatus.Invalid, error);
        }

        var updated = await PersistCatalogStyleAsync(existing, mapLibreStyleJson, cancellationToken).ConfigureAwait(false);
        return updated
            ? new OgcStyleUpdateResult(OgcStyleUpdateStatus.Updated, null)
            : new OgcStyleUpdateResult(OgcStyleUpdateStatus.NotFound, $"Style '{styleId}' not found.");
    }

    // Phase 2 write path: update a standalone catalog style from an Esri drawingInfo
    // renderer. The renderer is converted server-side (ADR-0002) and only the resulting
    // canonical MapLibre style is stored, so MapLibre stays the single source of truth and
    // the Esri encoding is re-derived on read.
    private async Task<OgcStyleUpdateResult> UpdateCatalogStyleFromDrawingInfoAsync(
        string styleId,
        JsonElement drawingInfo,
        bool strict,
        CancellationToken cancellationToken)
    {
        if (_independentStyleCatalog is null)
        {
            return new OgcStyleUpdateResult(OgcStyleUpdateStatus.NotFound, $"Style '{styleId}' not found.");
        }

        var existing = await _independentStyleCatalog.GetStyleAsync(styleId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return new OgcStyleUpdateResult(OgcStyleUpdateStatus.NotFound, $"Style '{styleId}' not found.");
        }

        // Both converters are geometry-driven. Resolve geometry from the bound resource;
        // trusting the submitted renderer could replace a point layer's canonical style
        // with a polygon fill (or another incompatible symbolizer family).
        var descriptor = StandaloneStyleDescriptor.FromMapLibre(styleId, existing.MapLibreStyleJson);
        if (!descriptor.IsBoundToStorageLayer)
        {
            return new OgcStyleUpdateResult(
                OgcStyleUpdateStatus.Invalid,
                "The stored style is not bound to a Honua layer, so an Esri drawingInfo update cannot preserve its source. Submit a MapLibre style document instead.");
        }

        var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Index.StorageBindingsByStorageLayerId.ContainsKey(descriptor.Id)
            || !snapshot.Index.ResourcesByStorageLayerId.TryGetValue(descriptor.Id, out var resource))
        {
            return new OgcStyleUpdateResult(
                OgcStyleUpdateStatus.Invalid,
                "The stored style references a source that is not an existing Honua layer, so an Esri drawingInfo update cannot preserve its source. Submit a MapLibre style document instead.");
        }

        var geometryType = resource.ReadGeometryType();
        if (geometryType == MetadataV2GeometryType.None)
        {
            return new OgcStyleUpdateResult(
                OgcStyleUpdateStatus.Invalid,
                "The bound layer's geometry type could not be determined, so an Esri drawingInfo update cannot be converted safely.");
        }

        var rendererGeometryType = StandaloneStyleDescriptor.InferGeometryType(drawingInfo);
        if (rendererGeometryType != MetadataV2GeometryType.None
            && !UsesSameGeometryFamily(geometryType, rendererGeometryType))
        {
            return new OgcStyleUpdateResult(
                OgcStyleUpdateStatus.Invalid,
                $"The renderer symbolizes {rendererGeometryType}, but the bound layer uses {geometryType}. Submit a renderer for the bound layer's geometry type.");
        }

        var conversion = _geoServicesConverter.Convert(drawingInfo, descriptor.Id, styleId, geometryType);

        var warnings = conversion.Unsupported.Count == 0
            ? null
            : conversion.Unsupported.Select(u => $"{u.Code} ({u.SymbolizerType}): {u.Guidance}").ToArray();

        // Strict: never persist a lossy conversion — reject so the operator can adjust the renderer.
        if (strict && warnings is { Length: > 0 })
        {
            return new OgcStyleUpdateResult(
                OgcStyleUpdateStatus.Invalid,
                "The renderer uses features the canonical MapLibre style cannot represent. Resubmit without strict handling to accept the lossy conversion.",
                warnings);
        }

        var updated = await PersistCatalogStyleAsync(existing, conversion.MapLibreStyleJson, cancellationToken).ConfigureAwait(false);
        return updated
            ? new OgcStyleUpdateResult(OgcStyleUpdateStatus.Updated, null, warnings)
            : new OgcStyleUpdateResult(OgcStyleUpdateStatus.NotFound, $"Style '{styleId}' not found.");
    }

    private static bool UsesSameGeometryFamily(
        MetadataV2GeometryType resourceGeometryType,
        MetadataV2GeometryType rendererGeometryType)
        => resourceGeometryType switch
        {
            MetadataV2GeometryType.Point or MetadataV2GeometryType.MultiPoint =>
                rendererGeometryType is MetadataV2GeometryType.Point or MetadataV2GeometryType.MultiPoint,
            MetadataV2GeometryType.LineString or MetadataV2GeometryType.MultiLineString =>
                rendererGeometryType is MetadataV2GeometryType.LineString or MetadataV2GeometryType.MultiLineString,
            MetadataV2GeometryType.Polygon or MetadataV2GeometryType.MultiPolygon =>
                rendererGeometryType is MetadataV2GeometryType.Polygon or MetadataV2GeometryType.MultiPolygon,
            _ => resourceGeometryType == rendererGeometryType
        };

    // Replaces the canonical MapLibre document of an existing catalog style, keeping its
    // descriptive metadata and letting the store increment style_version. The cached
    // drawingInfo column is deliberately cleared: the Esri encoding is derived from the
    // canonical style on read, so a stale cache must never outlive the style it mirrored.
    private async Task<bool> PersistCatalogStyleAsync(
        StyleCatalogRecord existing,
        string mapLibreStyleJson,
        CancellationToken cancellationToken)
    {
        var descriptor = StandaloneStyleDescriptor.FromMapLibre(existing.StyleId, mapLibreStyleJson);
        var drawingInfoJson = MapLibreToGeoServicesConverter.Convert(mapLibreStyleJson, descriptor);
        var updated = await _independentStyleCatalog!
            .UpdateStyleAsync(
                existing.StyleId,
                mapLibreStyleJson,
                existing.Title,
                existing.Description,
                drawingInfoJson,
                revisedBy: null,
                changeSummary: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (updated is null)
        {
            return false;
        }

        if (_styleGraphSync is not null)
        {
            // The catalog write above has already committed and incremented style_version, so
            // this mirror into the metadata-v2 graph is strictly post-commit. Reporting a
            // failure here would surface a successful edit as a 500: the endpoint would skip
            // its output-cache eviction (serving the stale list for the whole TTL) and a
            // client retry would apply a second revision of an edit that already landed.
            // Best-effort, mirroring LayerStyleService's own catalog/graph sync — log it and
            // let StyleResourceIds lag until the next publish.
            try
            {
                var associations = await _independentStyleCatalog
                    .ListAssociationsAsync(cancellationToken)
                    .ConfigureAwait(false);
                foreach (var layerId in associations
                             .Where(association => string.Equals(
                                 association.StyleId,
                                 existing.StyleId,
                                 StringComparison.Ordinal))
                             .Select(association => association.LayerId)
                             .Distinct())
                {
                    await _styleGraphSync.SyncLayerStylesAsync(layerId, cancellationToken).ConfigureAwait(false);
                }
            }
            // Intentional broad catch: a post-commit synchronization failure must not be
            // reported as a failed edit. Cancellation still propagates so a shutdown or an
            // aborted request is not silently swallowed.
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                LayerStyleLog.StandaloneStyleGraphSyncFailed(_logger, existing.StyleId, ex);
            }
        }

        return true;
    }

    private static bool TryValidateDrawingInfoShape(JsonElement drawingInfo, out string error)
    {
        if (drawingInfo.ValueKind != JsonValueKind.Object)
        {
            error = "drawingInfo must be a JSON object.";
            return false;
        }

        if (!drawingInfo.TryGetProperty("renderer", out var renderer)
            || renderer.ValueKind != JsonValueKind.Object)
        {
            error = "drawingInfo.renderer must be a JSON object.";
            return false;
        }

        if (!renderer.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(type.GetString()))
        {
            error = "drawingInfo.renderer.type must be a non-empty string.";
            return false;
        }

        foreach (var propertyName in new[] { "uniqueValueInfos", "classBreakInfos" })
        {
            if (!renderer.TryGetProperty(propertyName, out var infos)
                || infos.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            if (infos.EnumerateArray().Any(info => info.ValueKind != JsonValueKind.Object))
            {
                error = $"drawingInfo.renderer.{propertyName} entries must be JSON objects.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    /// <inheritdoc />
    public async Task<OgcStyleCreateResult> CreateStyleAsync(
        string? styleId,
        string mapLibreStyleJson,
        bool strict,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mapLibreStyleJson);

        if (_independentStyleCatalog is null)
        {
            return new OgcStyleCreateResult(
                OgcStyleCreateStatus.Invalid,
                null,
                "Standalone style creation requires the independent style catalog, which is not configured.");
        }

        if (!TryValidateStandaloneMapLibre(mapLibreStyleJson, strict, out var error))
        {
            return new OgcStyleCreateResult(OgcStyleCreateStatus.Invalid, null, error);
        }

        var resolvedStyleId = string.IsNullOrWhiteSpace(styleId)
            ? $"style-{Guid.NewGuid():N}"
            : styleId.Trim();

        var (collidingResource, _, _) = await ResolveResourceAsync(resolvedStyleId, cancellationToken).ConfigureAwait(false);
        if (collidingResource is not null)
        {
            return new OgcStyleCreateResult(
                OgcStyleCreateStatus.Conflict,
                null,
                $"Style identifier '{resolvedStyleId}' is already owned by a collection.");
        }

        var created = await _independentStyleCatalog
            .CreateStyleAsync(resolvedStyleId, mapLibreStyleJson, title: resolvedStyleId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (created is null)
        {
            return new OgcStyleCreateResult(
                OgcStyleCreateStatus.Conflict,
                null,
                $"A style with identifier '{resolvedStyleId}' already exists.");
        }

        return new OgcStyleCreateResult(OgcStyleCreateStatus.Created, created.StyleId, null);
    }

    /// <inheritdoc />
    public async Task<OgcStyleDeleteResult> DeleteStyleAsync(
        string styleId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(styleId);

        if (_independentStyleCatalog is null)
        {
            return new OgcStyleDeleteResult(
                OgcStyleDeleteStatus.NotFound,
                "Standalone style deletion requires the independent style catalog, which is not configured.");
        }

        var deleted = await _independentStyleCatalog.DeleteStyleAsync(styleId, cancellationToken).ConfigureAwait(false);
        return deleted
            ? new OgcStyleDeleteResult(OgcStyleDeleteStatus.Deleted, null)
            : new OgcStyleDeleteResult(OgcStyleDeleteStatus.NotFound, $"Style '{styleId}' not found.");
    }

    // Lightweight validation for a standalone (not-yet-layer-bound) MapLibre style.
    // Unlike the per-layer normalizer, it does not require a Honua tile source binding,
    // since a standalone catalog style is decoupled from any layer; binding (and the
    // stricter normalization) happens when the style is associated with a layer.
    private static bool TryValidateStandaloneMapLibre(string mapLibreStyleJson, bool strict, out string? error)
    {
        error = null;
        try
        {
            using var document = JsonDocument.Parse(mapLibreStyleJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "MapLibre style must be a JSON object.";
                return false;
            }

            if (strict)
            {
                if (!root.TryGetProperty("version", out var version)
                    || version.ValueKind != JsonValueKind.Number
                    || !version.TryGetInt32(out var versionNumber)
                    || versionNumber != 8)
                {
                    error = "MapLibre style must include version 8.";
                    return false;
                }

                if (!root.TryGetProperty("layers", out var layers)
                    || layers.ValueKind != JsonValueKind.Array
                    || layers.GetArrayLength() == 0)
                {
                    error = "MapLibre style must include at least one layer.";
                    return false;
                }
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = $"MapLibre style is not valid JSON: {ex.Message}";
            return false;
        }
    }

    private static OgcStylesheet DeriveSld(
        string mapLibreJson,
        MetadataV2Resource resource,
        int storageLayerId,
        OgcStyleEncoding encoding)
    {
        var layerName = string.IsNullOrWhiteSpace(resource.Metadata.Name)
            ? $"layer-{storageLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : resource.Metadata.Name;

        return DeriveSldFromMapLibre(mapLibreJson, layerName, encoding);
    }

    private static OgcStylesheet DeriveSldFromMapLibre(
        string mapLibreJson,
        string layerName,
        OgcStyleEncoding encoding)
    {
        if (string.IsNullOrWhiteSpace(layerName))
        {
            layerName = "style";
        }

        MapLibreStyleLayer[] layers;
        using (var document = JsonDocument.Parse(mapLibreJson))
        {
            layers = !document.RootElement.TryGetProperty("layers", out var layersElement)
                || layersElement.ValueKind != JsonValueKind.Array
                ? Array.Empty<MapLibreStyleLayer>()
                : JsonSerializer.Deserialize(
                    layersElement.GetRawText(),
                    MapLibreStyleJsonContext.Default.MapLibreStyleLayerArray) ?? Array.Empty<MapLibreStyleLayer>();
        }

        var export = MapLibreToSldConverter.Export(layers, layerName);
        var sldXml = export.SldXml;

        if (encoding == OgcStyleEncoding.Sld11)
        {
            sldXml = RewriteSldVersion(sldXml, "1.1.0");
            return new OgcStylesheet(sldXml, OgcStyleMediaTypes.Sld11, OgcStyleEncoding.Sld11);
        }

        return new OgcStylesheet(sldXml, OgcStyleMediaTypes.Sld10, OgcStyleEncoding.Sld10);
    }

    private static string RewriteSldVersion(string sldXml, string version)
    {
        try
        {
            var document = XDocument.Parse(sldXml);
            document.Root?.SetAttributeValue("version", version);
            return document.Declaration + Environment.NewLine + document;
        }
        catch (System.Xml.XmlException)
        {
            // The exporter always produces well-formed XML; fall back to the original
            // document rather than failing the request if parsing ever regresses.
            return sldXml;
        }
    }

    private async Task<(MetadataV2Resource? Resource, int? StorageLayerId, MetadataV2GraphSnapshot Snapshot)> ResolveResourceAsync(
        string styleId,
        CancellationToken cancellationToken)
    {
        var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        foreach (var resource in snapshot.Graph.Resources)
        {
            if (!string.Equals(resource.Metadata.Name, styleId, StringComparison.Ordinal))
            {
                continue;
            }

            return (resource, snapshot.ResolveStorageLayerId(resource), snapshot);
        }

        return (null, null, snapshot);
    }

    private async Task<(MetadataV2Resource? Resource, int? StorageLayerId, MetadataV2GraphSnapshot Snapshot)> ResolveStyledResourceAsync(
        string styleId,
        CancellationToken cancellationToken)
        => await ResolveResourceAsync(styleId, cancellationToken).ConfigureAwait(false);

    private static string ResolveTitle(MetadataV2Resource resource)
        => resource.Metadata.Title
            ?? (string.IsNullOrWhiteSpace(resource.Metadata.Name) ? "Style" : resource.Metadata.Name);
}

/// <summary>
/// Media type constants for OGC API - Styles stylesheet encodings, kept in
/// <c>Honua.Server</c> alongside the projection that produces them.
/// </summary>
internal static class OgcStyleMediaTypes
{
    public const string MapboxStyle = "application/vnd.mapbox.style+json";
    public const string Sld10 = "application/vnd.ogc.sld+xml;version=1.0";
    public const string Sld11 = "application/vnd.ogc.sld+xml;version=1.1";
    public const string EsriDrawingInfo = "application/vnd.esri.drawinginfo+json";
}
