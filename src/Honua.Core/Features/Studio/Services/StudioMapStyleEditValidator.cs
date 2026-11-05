// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Shared.Services;

namespace Honua.Core.Features.Studio.Services;

// The Studio client's map-style edit rules: an edit may change how authorized data looks, never which
// sources or render-layer bindings it draws from. Both size limits measure JSON.stringify text as Studio does:
// the map style in UTF-8 bytes, inline overrides in UTF-16 code units (JavaScript string length).
internal static class StudioMapStyleEditValidator
{
    internal const string CompositionLayerIdKey = "honua:compositionLayerId";

    private const int MaxMapStyleBytes = 1_048_576;
    private const int MaxInlineStyleLength = 262_144;
    private const string HonuaSourceTypePrefix = "honua-";

    private static readonly HashSet<string> MutableStyleMembers =
        new(["layers", "name", "metadata", "transition", "light", "lights", "fog"], StringComparer.Ordinal);

    private static readonly HashSet<string> InlineOverrideMembers =
        new(["paint", "layout", "filter", "minzoom", "maxzoom", "metadata"], StringComparer.Ordinal);

    public static string? FindMapStyleViolation(JsonObject anchor, JsonNode? candidate)
    {
        if (candidate is not JsonObject style
            || !IsNumber(style["version"], 8)
            || style["sources"] is not JsonObject sources
            || style["layers"] is not JsonArray layers)
        {
            return "Expected a MapLibre v8 style document.";
        }

        if (Utf8SizeViolation(style, MaxMapStyleBytes, "Map style exceeds the 1 MiB limit.") is { } mapSize)
        {
            return mapSize;
        }

        return FindProtectedMemberChange(anchor, style)
            ?? FindRenderLayerViolation(anchor, sources, layers)
            ?? FindHonuaStyleViolation(sources, layers);
    }

    public static string? FindInlineStyleViolation(string layerId, JsonNode? styleBody)
    {
        if (styleBody is not JsonObject overrides)
        {
            return "Style overrides must be an object.";
        }

        foreach (var (targetLayerId, layerOverride) in overrides)
        {
            if (targetLayerId != layerId && targetLayerId != OutlineLayerId(layerId))
            {
                return "Style overrides must target the selected layer only.";
            }

            if (layerOverride is not JsonObject members || members.Any(member => !InlineOverrideMembers.Contains(member.Key)))
            {
                return "Style overrides cannot change layer identity or sources.";
            }
        }

        return Utf16LengthViolation(overrides, MaxInlineStyleLength, "Style overrides exceed the 256 KiB limit.");
    }

    private static string? Utf8SizeViolation(JsonNode node, int limit, string tooLong) =>
        MeasuredTextViolation(node, text => Encoding.UTF8.GetByteCount(text) <= limit ? null : tooLong);

    private static string? Utf16LengthViolation(JsonNode node, int limit, string tooLong) =>
        MeasuredTextViolation(node, text => text.Length <= limit ? null : tooLong);

    private static string? MeasuredTextViolation(JsonNode node, Func<string, string?> measure)
    {
        try
        {
            return measure(EcmaScriptJsonSerializer.Stringify(node));
        }
        catch (Exception exception) when (exception is UnpairedUtf16SurrogateException
            || (exception is InvalidOperationException && exception.Message.Contains("UTF-16", StringComparison.Ordinal)))
        {
            return UnpairedUtf16SurrogateException.MessageText;
        }
    }

    internal static string OutlineLayerId(string layerId) => layerId + "__outline";

    internal static JsonNode? CompositionLayerIdOf(JsonObject renderLayer) =>
        (renderLayer["metadata"] as JsonObject)?[CompositionLayerIdKey];

    private static string? FindProtectedMemberChange(JsonObject anchor, JsonObject candidate)
    {
        var changed = anchor.Select(member => member.Key)
            .Union(candidate.Select(member => member.Key), StringComparer.Ordinal)
            .FirstOrDefault(key => !MutableStyleMembers.Contains(key) && !JsonNode.DeepEquals(anchor[key], candidate[key]));
        return changed is null ? null : $"The editor cannot change protected style member '{changed}'.";
    }

    private static string? FindRenderLayerViolation(JsonObject anchor, JsonObject sources, JsonArray layers)
    {
        var anchorLayers = AnchorLayersById(anchor);
        foreach (var node in layers)
        {
            if (node is not JsonObject layer || !IsString(layer["id"]))
            {
                return "Every render layer needs an identity.";
            }

            if (anchorLayers.TryGetValue(layer["id"]!.GetValue<string>(), out var existing) && ChangesSourceBinding(existing, layer))
            {
                return "Existing render-layer source bindings cannot change.";
            }

            if (layer.TryGetPropertyValue("source", out var source)
                && !(IsString(source) && sources.ContainsKey(source!.GetValue<string>())))
            {
                return "Render layers must use an existing authorized source.";
            }
        }

        return null;
    }

    private static string? FindHonuaStyleViolation(JsonObject sources, JsonArray layers)
    {
        foreach (var (_, node) in sources)
        {
            if (node is not JsonObject source)
            {
                return "Source must be a non-null object";
            }

            if (!IsString(source["type"]))
            {
                return "Source type must be a string";
            }

            if (source["type"]!.GetValue<string>().StartsWith(HonuaSourceTypePrefix, StringComparison.Ordinal) && !IsString(source["url"]))
            {
                return "Honua source must have a string url";
            }
        }

        return layers.Any(layer => !IsString(layer!["type"])) ? "Layer must have a string type" : null;
    }

    private static Dictionary<string, JsonObject> AnchorLayersById(JsonObject anchor)
    {
        var byId = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var layer in (anchor["layers"] as JsonArray ?? []).OfType<JsonObject>().Where(layer => IsString(layer["id"])))
        {
            byId[layer["id"]!.GetValue<string>()] = layer;
        }

        return byId;
    }

    private static bool ChangesSourceBinding(JsonObject existing, JsonObject candidate) =>
        !JsonNode.DeepEquals(existing["source"], candidate["source"])
        || !JsonNode.DeepEquals(existing["source-layer"], candidate["source-layer"])
        || !JsonNode.DeepEquals(CompositionLayerIdOf(existing), CompositionLayerIdOf(candidate));

    private static bool IsString(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String;

    private static bool IsNumber(JsonNode? node, double expected) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.GetValue<double>() == expected;
}
