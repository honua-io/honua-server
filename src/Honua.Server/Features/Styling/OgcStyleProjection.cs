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

    public OgcStyleProjection(
        IMetadataV2GraphProvider graphProvider,
        ILayerStyleService styleService,
        ILayerStyleCatalog styleCatalog)
    {
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        _styleService = styleService ?? throw new ArgumentNullException(nameof(styleService));
        _styleCatalog = styleCatalog ?? throw new ArgumentNullException(nameof(styleCatalog));
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
            return null;
        }

        var stored = await _styleCatalog.GetLayerStyleAsync(storageLayerId.Value, cancellationToken).ConfigureAwait(false);
        if (stored is null || string.IsNullOrWhiteSpace(stored.MapLibreStyleJson))
        {
            return null;
        }

        var mapLibreJson = stored.MapLibreStyleJson!;
        if (encoding == OgcStyleEncoding.MapboxStyle)
        {
            return new OgcStylesheet(mapLibreJson, OgcStyleMediaTypes.MapboxStyle, OgcStyleEncoding.MapboxStyle);
        }

        var sld = DeriveSld(mapLibreJson, resource, storageLayerId.Value, encoding);
        return sld;
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
            return null;
        }

        var stored = await _styleCatalog.GetLayerStyleAsync(storageLayerId.Value, cancellationToken).ConfigureAwait(false);
        if (stored is null || string.IsNullOrWhiteSpace(stored.MapLibreStyleJson))
        {
            return null;
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

    private static OgcStylesheet DeriveSld(
        string mapLibreJson,
        MetadataV2Resource resource,
        int storageLayerId,
        OgcStyleEncoding encoding)
    {
        var layerName = string.IsNullOrWhiteSpace(resource.Metadata.Name)
            ? $"layer-{storageLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : resource.Metadata.Name;

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
}
