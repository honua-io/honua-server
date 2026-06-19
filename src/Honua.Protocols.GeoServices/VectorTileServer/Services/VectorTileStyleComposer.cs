// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Composes the Mapbox GL v8 style document served by the GeoServices VectorTileServer
// resources/styles/root.json endpoint (honua-server#1779, epic #1776). Given the canonical
// MapLibre/Mapbox style stored for the service's primary layer (or none), it produces a
// GL v8 document whose vector source's tile template resolves to this service's
// tile/{z}/{y}/{x}.pbf route.
//
// Sprite/glyphs references are scoped-minimal (honua-server#1780, epic decision): Honua serves
// only a stub sprite sheet and a stub glyph stack, so emitting sprite/glyphs is useful ONLY for
// styles that actually consume them — i.e. styles with at least one `symbol` layer. When the
// composed style has a symbol layer, the composer points sprite/glyphs at THIS service's
// resources routes (absolute); otherwise it omits them. Any stale sprite/glyphs already present
// in a stored style are always replaced (when symbol layers exist) or stripped (when they don't)
// so the served document stays self-consistent.

using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.GeoServices.VectorTileServer.Services;

/// <summary>
/// Rewrites a stored MapLibre/Mapbox style (or synthesizes a deterministic default) into the
/// Mapbox GL v8 style document served by the VectorTileServer <c>resources/styles/root.json</c>
/// endpoint. The sole vector source is pointed at this service's tile template; sprite/glyphs
/// references are emitted (pointed at this service's scoped-minimal sprite/glyph routes) only
/// when the composed style has at least one <c>symbol</c> layer (honua-server#1780).
/// </summary>
internal static class VectorTileStyleComposer
{
    /// <summary>
    /// Source layer name emitted in the default style and used to bind layers to the vector
    /// source. Matches the canonical Honua tile source-layer name.
    /// </summary>
    internal const string DefaultSourceLayerName = "layer";

    private const string DefaultPointColor = "#2D69A5";
    private const string DefaultLineColor = "#2D69A5";
    private const string DefaultFillColor = "#2D69A5";
    private const string DefaultOutlineColor = "#1A4D80";
    private const string DefaultStrokeColor = "#FFFFFF";

    /// <summary>
    /// Composes the GL v8 style JSON for a service.
    /// </summary>
    /// <param name="storedMapLibreJson">
    /// The canonical MapLibre/Mapbox style stored for the service's primary layer, or
    /// <see langword="null"/>/whitespace when the layer has no stored style.
    /// </param>
    /// <param name="serviceName">The GeoServices service name (used for the style <c>name</c>).</param>
    /// <param name="sourceId">The vector source identifier to emit (for example <c>esri</c>).</param>
    /// <param name="tileUrl">
    /// The absolute tile template, for example
    /// <c>https://host/rest/services/{id}/VectorTileServer/tile/{z}/{y}/{x}.pbf</c>.
    /// </param>
    /// <param name="geometryType">
    /// The primary layer's geometry type, used to pick deterministic default paint when no
    /// style is stored.
    /// </param>
    /// <param name="spriteUrl">
    /// The absolute sprite base reference for this service (for example
    /// <c>https://host/rest/services/{id}/VectorTileServer/resources/sprites/sprite</c>), emitted
    /// only when the composed style has a symbol layer. Pass <see langword="null"/> to always omit.
    /// </param>
    /// <param name="glyphsUrl">
    /// The absolute glyphs template for this service (for example
    /// <c>https://host/rest/services/{id}/VectorTileServer/resources/fonts/{fontstack}/{range}.pbf</c>),
    /// emitted only when the composed style has a symbol layer. Pass <see langword="null"/> to
    /// always omit.
    /// </param>
    /// <returns>A serialized Mapbox GL v8 style document.</returns>
    public static string Compose(
        string? storedMapLibreJson,
        string serviceName,
        string sourceId,
        string tileUrl,
        MetadataV2GeometryType geometryType,
        string? spriteUrl = null,
        string? glyphsUrl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tileUrl);

        var root = TryParseStoredStyle(storedMapLibreJson)
            ?? BuildDefaultStyle(serviceName, sourceId, tileUrl, geometryType);

        RewriteSources(root, sourceId, tileUrl);
        ApplySpriteAndGlyphs(root, spriteUrl, glyphsUrl);
        EnsureVersionAndName(root, serviceName);

        return root.ToJsonString(SerializerOptions);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };

    private static JsonObject? TryParseStoredStyle(string? storedMapLibreJson)
    {
        if (string.IsNullOrWhiteSpace(storedMapLibreJson))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(storedMapLibreJson) as JsonObject;
        }
        catch (JsonException)
        {
            // A stored style that no longer parses should not fail the endpoint; fall back
            // to the deterministic default instead of surfacing a 500.
            return null;
        }
    }

    /// <summary>
    /// Rewrites every <c>sources.&lt;id&gt;</c> entry so the served document only advertises
    /// the local vector tile route. The configured <paramref name="sourceId"/> is guaranteed to
    /// exist as a vector source whose <c>tiles[]</c> is the absolute service tile template; the
    /// legacy <c>url</c> (TileJSON pointer) is removed so clients fetch tiles directly.
    /// </summary>
    private static void RewriteSources(JsonObject root, string sourceId, string tileUrl)
    {
        if (root["sources"] is not JsonObject sources)
        {
            sources = new JsonObject();
            root["sources"] = sources;
        }

        // Rewrite any existing vector source in place so layer source-bindings stay valid.
        foreach (var entry in sources.ToArray())
        {
            if (entry.Value is JsonObject source)
            {
                RewriteVectorSource(source, tileUrl);
            }
        }

        if (sources[sourceId] is not JsonObject configured)
        {
            configured = new JsonObject();
            sources[sourceId] = configured;
        }

        RewriteVectorSource(configured, tileUrl);
    }

    private static void RewriteVectorSource(JsonObject source, string tileUrl)
    {
        source["type"] = "vector";
        // Direct tile template wins; drop the TileJSON pointer so we never emit a URL the
        // VectorTileServer surface does not serve.
        source.Remove("url");
        source["tiles"] = new JsonArray(tileUrl);
    }

    /// <summary>
    /// Applies the scoped-minimal sprite/glyphs rule: when the composed style has at least one
    /// <c>symbol</c> layer and absolute references are supplied, point <c>sprite</c>/<c>glyphs</c>
    /// at this service's resources routes; otherwise strip them so the served document never
    /// advertises a sprite/glyph reference the client would fail to use.
    /// </summary>
    private static void ApplySpriteAndGlyphs(JsonObject root, string? spriteUrl, string? glyphsUrl)
    {
        if (HasSymbolLayer(root)
            && !string.IsNullOrWhiteSpace(spriteUrl)
            && !string.IsNullOrWhiteSpace(glyphsUrl))
        {
            root["sprite"] = spriteUrl;
            root["glyphs"] = glyphsUrl;
            return;
        }

        root.Remove("sprite");
        root.Remove("glyphs");
    }

    /// <summary>
    /// Returns <see langword="true"/> when any layer in the style has <c>type == "symbol"</c>,
    /// the only Mapbox GL layer type that consumes a sprite sheet (icons) or glyph stack (text).
    /// </summary>
    private static bool HasSymbolLayer(JsonObject root)
    {
        if (root["layers"] is not JsonArray layers)
        {
            return false;
        }

        foreach (var layer in layers)
        {
            if (layer is JsonObject layerObject
                && layerObject["type"] is JsonValue typeValue
                && typeValue.TryGetValue(out string? type)
                && string.Equals(type, "symbol", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureVersionAndName(JsonObject root, string serviceName)
    {
        root["version"] = 8;
        if (root["name"] is null)
        {
            root["name"] = serviceName;
        }
    }

    /// <summary>
    /// Builds a deterministic, geometry-aware default GL v8 style for a layer with no stored
    /// style. The paint defaults mirror the canonical Honua per-layer defaults so the
    /// VectorTileServer surface renders identically to the rest of the server.
    /// </summary>
    private static JsonObject BuildDefaultStyle(
        string serviceName,
        string sourceId,
        string tileUrl,
        MetadataV2GeometryType geometryType)
    {
        var layers = BuildDefaultLayers(sourceId, geometryType);

        return new JsonObject
        {
            ["version"] = 8,
            ["name"] = serviceName,
            ["sources"] = new JsonObject
            {
                [sourceId] = new JsonObject
                {
                    ["type"] = "vector",
                    ["tiles"] = new JsonArray(tileUrl)
                }
            },
            ["layers"] = layers
        };
    }

    private static JsonArray BuildDefaultLayers(string sourceId, MetadataV2GeometryType geometryType)
    {
        var layers = new JsonArray();

        switch (geometryType)
        {
            case MetadataV2GeometryType.Point:
            case MetadataV2GeometryType.MultiPoint:
                layers.Add(CircleLayer(sourceId));
                break;

            case MetadataV2GeometryType.LineString:
            case MetadataV2GeometryType.MultiLineString:
                layers.Add(LineLayer(sourceId));
                break;

            case MetadataV2GeometryType.Polygon:
            case MetadataV2GeometryType.MultiPolygon:
                layers.Add(FillLayer(sourceId));
                layers.Add(FillOutlineLayer(sourceId));
                break;

            default:
                // Unknown / mixed / non-geometric: emit a superset so any geometry renders.
                layers.Add(FillLayer(sourceId));
                layers.Add(LineLayer(sourceId));
                layers.Add(CircleLayer(sourceId));
                break;
        }

        return layers;
    }

    private static JsonObject CircleLayer(string sourceId) => new()
    {
        ["id"] = $"{sourceId}-circle",
        ["type"] = "circle",
        ["source"] = sourceId,
        ["source-layer"] = DefaultSourceLayerName,
        ["paint"] = new JsonObject
        {
            ["circle-color"] = DefaultPointColor,
            ["circle-radius"] = 4,
            ["circle-opacity"] = 0.85,
            ["circle-stroke-color"] = DefaultStrokeColor,
            ["circle-stroke-width"] = 1
        }
    };

    private static JsonObject LineLayer(string sourceId) => new()
    {
        ["id"] = $"{sourceId}-line",
        ["type"] = "line",
        ["source"] = sourceId,
        ["source-layer"] = DefaultSourceLayerName,
        ["paint"] = new JsonObject
        {
            ["line-color"] = DefaultLineColor,
            ["line-width"] = 2,
            ["line-opacity"] = 0.9
        }
    };

    private static JsonObject FillLayer(string sourceId) => new()
    {
        ["id"] = $"{sourceId}-fill",
        ["type"] = "fill",
        ["source"] = sourceId,
        ["source-layer"] = DefaultSourceLayerName,
        ["paint"] = new JsonObject
        {
            ["fill-color"] = DefaultFillColor,
            ["fill-opacity"] = 0.4
        }
    };

    private static JsonObject FillOutlineLayer(string sourceId) => new()
    {
        ["id"] = $"{sourceId}-fill-outline",
        ["type"] = "line",
        ["source"] = sourceId,
        ["source-layer"] = DefaultSourceLayerName,
        ["paint"] = new JsonObject
        {
            ["line-color"] = DefaultOutlineColor,
            ["line-width"] = 0.75,
            ["line-opacity"] = 0.8
        }
    };
}
