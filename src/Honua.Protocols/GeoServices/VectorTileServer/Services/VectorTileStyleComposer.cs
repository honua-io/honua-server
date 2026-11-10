// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
//
// Composes the Mapbox GL v8 style document served by the GeoServices VectorTileServer
// resources/styles/root.json endpoint (honua-server#1779, epic #1776). Given the canonical
// MapLibre/Mapbox style stored for the service's primary layer (or none), it produces a
// GL v8 document whose vector source's url names this service's descriptor (#5534) and whose
// tile template resolves to this service's tile/{z}/{y}/{x}.pbf route.
//
// Sprite/glyphs references are scoped (honua-server#1780, epic decision): Honua serves a stub
// sprite sheet and one bundled glyph face (#5535), so emitting sprite/glyphs is useful ONLY for
// styles that actually consume them — i.e. styles with at least one `symbol` layer. When the
// composed style has a symbol layer, the composer points sprite/glyphs at THIS service's
// resources routes (absolute); otherwise it omits them. Any stale sprite/glyphs already present
// in a stored style are always replaced (when symbol layers exist) or stripped (when they don't)
// so the served document stays self-consistent.

using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.GeoServices.VectorTileServer.Services;

internal static class VectorTileStyleComposer
{
    internal const string DefaultSourceLayerName = "layer";

    private const string DefaultPointColor = "#2D69A5";
    private const string DefaultLineColor = "#2D69A5";
    private const string DefaultFillColor = "#2D69A5";
    private const string DefaultOutlineColor = "#1A4D80";
    private const string DefaultStrokeColor = "#FFFFFF";

    public static string Compose(
        string? storedMapLibreJson,
        string serviceName,
        string sourceId,
        string tileUrl,
        MetadataV2GeometryType geometryType,
        string? spriteUrl = null,
        string? glyphsUrl = null,
        string? serviceUrl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tileUrl);

        var root = TryParseStoredStyle(storedMapLibreJson)
            ?? BuildDefaultStyle(serviceName, sourceId, tileUrl, geometryType);

        RewriteSources(root, sourceId, tileUrl, serviceUrl);
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

    // Every style source is rewritten to this service, and a stored URL that names another service is replaced or dropped.
    private static void RewriteSources(JsonObject root, string sourceId, string tileUrl, string? serviceUrl)
    {
        if (root["sources"] is not JsonObject sources)
        {
            sources = new JsonObject();
            root["sources"] = sources;
        }

        // Rewrite any existing vector source in place so layer source-bindings stay valid.
        foreach (var source in sources.ToArray().Select(entry => entry.Value).OfType<JsonObject>())
        {
            RewriteVectorSource(source, tileUrl, serviceUrl);
        }

        if (sources[sourceId] is not JsonObject configured)
        {
            configured = new JsonObject();
            sources[sourceId] = configured;
        }

        RewriteVectorSource(configured, tileUrl, serviceUrl);
    }

    private static void RewriteVectorSource(JsonObject source, string tileUrl, string? serviceUrl)
    {
        source["type"] = "vector";
        // The url names this service's descriptor, from which clients read the fullExtent and
        // tile map (#5534); the direct tile template stays for clients that fetch tiles from
        // the style alone. A stored url never survives: it may name a service we do not serve.
        if (string.IsNullOrWhiteSpace(serviceUrl))
        {
            source.Remove("url");
        }
        else
        {
            source["url"] = serviceUrl;
        }

        source["tiles"] = new JsonArray(tileUrl);
    }

    // Sprite and glyph references are added only when the style has a symbol layer and absolute references exist; otherwise they are stripped so the document does not advertise a resource the client would fail to fetch.
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

    private static bool HasSymbolLayer(JsonObject root)
    {
        if (root["layers"] is not JsonArray layers)
        {
            return false;
        }

        return layers.Any(layer => layer is JsonObject layerObject
            && layerObject["type"] is JsonValue typeValue
            && typeValue.TryGetValue(out string? type)
            && string.Equals(type, "symbol", StringComparison.Ordinal));
    }

    private static void EnsureVersionAndName(JsonObject root, string serviceName)
    {
        root["version"] = 8;
        if (root["name"] is null)
        {
            root["name"] = serviceName;
        }
    }

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
