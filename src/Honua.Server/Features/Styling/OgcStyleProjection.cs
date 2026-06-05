// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Infrastructure.Rendering;
using Honua.Server.Features.Styling.Sld;

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

    public OgcStyleProjection(
        IMetadataV2GraphProvider graphProvider,
        ILayerStyleService styleService,
        ILayerStyleCatalog styleCatalog,
        IGeoServicesStyleConverter geoServicesConverter,
        IStyleCatalog? independentStyleCatalog = null)
    {
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        _styleService = styleService ?? throw new ArgumentNullException(nameof(styleService));
        _styleCatalog = styleCatalog ?? throw new ArgumentNullException(nameof(styleCatalog));
        _geoServicesConverter = geoServicesConverter ?? throw new ArgumentNullException(nameof(geoServicesConverter));
        _independentStyleCatalog = independentStyleCatalog;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OgcStyleSummary>> ListStylesAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        var summaries = new List<OgcStyleSummary>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
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

            // Phase 1: a collection projects to an OGC style only when it has a
            // genuinely stored MapLibre style. Read the canonical store directly so the
            // in-memory default style synthesized for unstyled layers does not appear.
            var stored = await _styleCatalog.GetLayerStyleAsync(storageLayerId.Value, cancellationToken).ConfigureAwait(false);
            if (stored is null || string.IsNullOrWhiteSpace(stored.MapLibreStyleJson))
            {
                continue;
            }

            summaries.Add(new OgcStyleSummary(styleId, ResolveTitle(resource)));
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

        var (resource, storageLayerId, snapshot) = await ResolveStyledResourceAsync(styleId, cancellationToken).ConfigureAwait(false);
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
            return new OgcStyleUpdateResult(OgcStyleUpdateStatus.NotFound, $"Style '{styleId}' not found.");
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

        var (resource, storageLayerId, _) = await ResolveResourceAsync(styleId, cancellationToken).ConfigureAwait(false);
        if (resource is null || !storageLayerId.HasValue)
        {
            return new OgcStyleUpdateResult(OgcStyleUpdateStatus.NotFound, $"Style '{styleId}' not found.");
        }

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
                    || version.GetInt32() != 8)
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
            if (!document.RootElement.TryGetProperty("layers", out var layersElement)
                || layersElement.ValueKind != JsonValueKind.Array)
            {
                layers = Array.Empty<MapLibreStyleLayer>();
            }
            else
            {
                layers = JsonSerializer.Deserialize(
                    layersElement.GetRawText(),
                    MapLibreStyleJsonContext.Default.MapLibreStyleLayerArray) ?? Array.Empty<MapLibreStyleLayer>();
            }
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
            return document.Declaration + Environment.NewLine + document.ToString();
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
