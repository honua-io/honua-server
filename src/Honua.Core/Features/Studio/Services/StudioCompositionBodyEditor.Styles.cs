// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Services;

public static partial class StudioCompositionBodyEditor
{
    /// <summary>
    /// Reconciles a composition transition with a freshly authorized server projection.
    /// Existing appearances are retained; only genuinely added composition layers get
    /// generated defaults. Intentionally removed render layers are never resurrected.
    /// </summary>
    public static StudioCompositionBody ReconcileMapStyle(StudioCompositionBody before,
        StudioCompositionBody after, JsonElement authorizedProjection)
    {
        if (before.MapSpec is not { ValueKind: JsonValueKind.Object } original)
            throw new StudioCompositionBodyException("Initialize the map before reconciling its style.");
        var template = JsonNode.Parse(authorizedProjection.GetRawText())!.AsObject();
        var sources = template["sources"]?.AsObject()
            ?? throw new StudioCompositionBodyException("The source projection is incomplete.");
        var candidate = JsonNode.Parse((after.MapSpec ?? original).GetRawText())!.AsObject();
        var candidateSources = candidate["sources"]?.AsObject()
            ?? throw new StudioCompositionBodyException("The map style must contain its source bindings.");
        var previousIds = before.Layers.Select(layer => layer.Id).ToHashSet(StringComparer.Ordinal);
        var nextIds = after.Layers.Select(layer => layer.Id).ToHashSet(StringComparer.Ordinal);
        var removedIds = previousIds.Except(nextIds).ToHashSet(StringComparer.Ordinal);
        var removedSources = before.Layers.Select(layer => layer.SourceId)
            .Except(after.Layers.Select(layer => layer.SourceId), StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        foreach (var source in candidateSources)
        {
            if (removedSources.Contains(source.Key)) continue;
            if (!sources.TryGetPropertyValue(source.Key, out var expected) || !JsonNode.DeepEquals(source.Value, expected))
                throw new StudioCompositionBodyException("A composed layer must use its server-resolved source binding.");
        }
        candidate["sources"] = sources.DeepClone();
        var renders = candidate["layers"]?.AsArray()
            ?? throw new StudioCompositionBodyException("The map style must contain a layer array.");
        for (var index = renders.Count - 1; index >= 0; index--)
        {
            var owner = RenderOwner(renders[index], before);
            if (owner is not null && removedIds.Contains(owner)) renders.RemoveAt(index);
        }
        foreach (var layer in after.Layers.Where(layer => !previousIds.Contains(layer.Id)))
        {
            if (renders.Any(render => RenderOwner(render, after) == layer.Id)) continue;
            var following = after.Layers.SkipWhile(candidateLayer => candidateLayer.Id != layer.Id).Skip(1)
                .Select(candidateLayer => candidateLayer.Id).ToHashSet(StringComparer.Ordinal);
            var insertAt = Enumerable.Range(0, renders.Count).FirstOrDefault(
                index => RenderOwner(renders[index], after) is { } owner && following.Contains(owner), renders.Count);
            foreach (var generated in template["layers"]!.AsArray().Where(render => RenderOwner(render, after) == layer.Id))
                renders.Insert(insertAt++, generated!.DeepClone());
        }
        var previousOrder = before.Layers.Where(layer => nextIds.Contains(layer.Id)).Select(layer => layer.Id);
        var nextOrder = after.Layers.Where(layer => previousIds.Contains(layer.Id)).Select(layer => layer.Id);
        if (!previousOrder.SequenceEqual(nextOrder, StringComparer.Ordinal))
        {
            // Only an explicit composition reorder changes established render order.
            // Keep basemap slots and each owner's internal fill/outline/label order.
            var ranks = after.Layers.Select((layer, index) => (layer.Id, index))
                .ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.Ordinal);
            var slots = Enumerable.Range(0, renders.Count)
                .Where(index => RenderOwner(renders[index], after) is { } owner && ranks.ContainsKey(owner)).ToArray();
            var reordered = slots.Select(index => renders[index]!)
                .OrderBy(render => ranks[RenderOwner(render, after)!]).Select(render => render.DeepClone()).ToArray();
            for (var index = 0; index < slots.Length; index++)
                renders[slots[index]] = reordered[index];
        }
        foreach (var layer in after.Layers)
        {
            var previous = before.Layers.FirstOrDefault(candidateLayer => candidateLayer.Id == layer.Id);
            if (previous is null || previous.Visible == layer.Visible) continue;
            foreach (var render in renders.OfType<JsonObject>().Where(render => RenderOwner(render, after) == layer.Id))
            {
                if (render["layout"] is null) render["layout"] = new JsonObject();
                render["layout"]!["visibility"] = layer.Visible ? "visible" : "none";
            }
        }
        // Source changes are authorized above; external asset bindings still use
        // the original style as their anchor, exactly as the typed style command.
        var anchor = JsonNode.Parse(original.GetRawText())!.AsObject();
        anchor["sources"] = sources.DeepClone();
        using var anchorDocument = JsonDocument.Parse(anchor.ToJsonString());
        using var candidateDocument = JsonDocument.Parse(candidate.ToJsonString());
        _ = SetMapStyle(after with { MapSpec = anchorDocument.RootElement.Clone() }, candidateDocument.RootElement);
        var removedInlineStyles = removedIds.Select(id => $"honua-inline:{id}").ToHashSet(StringComparer.Ordinal);
        return after with
        {
            MapSpec = candidateDocument.RootElement.Clone(),
            StyleRefs = after.StyleRefs?.Where(reference =>
                !(reference.ValueKind == JsonValueKind.Object
                    && reference.TryGetProperty("styleId", out var id) && id.ValueKind == JsonValueKind.String
                    && removedInlineStyles.Contains(id.GetString()!))).ToArray()
        };
    }

    private static string? RenderOwner(JsonNode? render, StudioCompositionBody body)
    {
        if (render is not JsonObject layer) return null;
        if (layer["metadata"] is JsonObject metadata && metadata["honua:compositionLayerId"] is JsonValue value
            && value.TryGetValue<string>(out var owner)) return owner;
        var id = layer["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var text) ? text : null;
        return body.Layers.FirstOrDefault(candidate => candidate.Id == id)?.Id
            ?? body.Layers.FirstOrDefault(candidate => $"{candidate.Id}__outline" == id)?.Id;
    }

    /// <summary>
    /// Stores a full resolved MapLibre appearance as a map-local snapshot. Shared
    /// catalog styles remain unchanged; their draft bindings are explicitly detached.
    /// Source and external-asset bindings must match the existing authorized document.
    /// Full style-spec validation remains the lifecycle validator's responsibility.
    /// </summary>
    public static StudioCompositionBody SetMapStyle(StudioCompositionBody body, JsonElement mapSpec)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.MapSpec is not { ValueKind: JsonValueKind.Object } previous)
        {
            throw new StudioCompositionBodyException("Initialize this draft's authorized map document before editing its full style.");
        }

        if (mapSpec.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(mapSpec.GetRawText()) > 1048576
            || !mapSpec.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var number) || number != 8
            || !mapSpec.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Object
            || !mapSpec.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
        {
            throw new StudioCompositionBodyException("A map style must be a MapLibre version 8 document of at most 1 MiB with sources and layers.");
        }

        var previousMembers = UniqueMembers(previous);
        var nextMembers = UniqueMembers(mapSpec);
        foreach (var key in previousMembers.Keys.Union(nextMembers.Keys, StringComparer.Ordinal))
        {
            if (key is "layers" or "name" or "metadata" or "transition" or "light" or "lights" or "fog")
            {
                continue;
            }

            if (!previousMembers.TryGetValue(key, out var before) || !nextMembers.TryGetValue(key, out var after)
                || !JsonElement.DeepEquals(before, after))
            {
                throw new StudioCompositionBodyException(
                    $"Full style editing cannot change '{key}'. Change data sources or approved assets through their own workflow first.");
            }
        }

        var sourceNames = UniqueMembers(sources).Keys.ToHashSet(StringComparer.Ordinal);
        var sourceLayers = new HashSet<(string Source, string Layer)>();
        var previousLayers = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (previous.TryGetProperty("layers", out var originalLayers) && originalLayers.ValueKind == JsonValueKind.Array)
        {
            foreach (var layer in originalLayers.EnumerateArray())
            {
                if (layer.ValueKind == JsonValueKind.Object
                    && layer.TryGetProperty("id", out var previousId) && previousId.ValueKind == JsonValueKind.String)
                    previousLayers.TryAdd(previousId.GetString()!, layer);
                if (layer.ValueKind == JsonValueKind.Object
                    && layer.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String
                    && layer.TryGetProperty("source-layer", out var sourceLayer) && sourceLayer.ValueKind == JsonValueKind.String)
                {
                    sourceLayers.Add((source.GetString()!, sourceLayer.GetString()!));
                }
            }
        }

        var layerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in layers.EnumerateArray())
        {
            if (layer.ValueKind != JsonValueKind.Object || !layer.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())
                || !layerIds.Add(id.GetString()!))
            {
                throw new StudioCompositionBodyException("Map style layers must have distinct nonempty identifiers.");
            }

            _ = UniqueMembers(layer);
            if (previousLayers.TryGetValue(id.GetString()!, out var previousLayer))
            {
                foreach (var property in new[] { "source", "source-layer" })
                {
                    var hadValue = previousLayer.TryGetProperty(property, out var oldValue);
                    var hasValue = layer.TryGetProperty(property, out var newValue);
                    if (hadValue != hasValue || hadValue && !JsonElement.DeepEquals(oldValue, newValue))
                        throw new StudioCompositionBodyException("An existing render layer must retain its source binding.");
                }
                if (EffectiveStyleLayerOwner(body, previousLayer, id.GetString()!)
                    != EffectiveStyleLayerOwner(body, layer, id.GetString()!))
                    throw new StudioCompositionBodyException("An existing render layer must retain its composed layer owner.");
            }
            var sourceId = layer.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String
                ? source.GetString() : null;
            if (layer.TryGetProperty("source", out _) && (sourceId is null || !sourceNames.Contains(sourceId)))
            {
                throw new StudioCompositionBodyException("A style layer must use an existing authorized source.");
            }

            if (layer.TryGetProperty("source-layer", out var sourceLayer)
                && (sourceId is null || sourceLayer.ValueKind != JsonValueKind.String
                    || !sourceLayers.Contains((sourceId, sourceLayer.GetString()!))))
            {
                throw new StudioCompositionBodyException("A style edit cannot introduce an unbound vector source layer.");
            }

            ValidateStyleLayerOwner(body, layer, id.GetString()!, sourceId);
        }

        return body with
        {
            MapSpec = mapSpec.Clone(),
            StyleRefs = [],
            Layers = body.Layers.Select(layer => layer with { StyleRef = null }).ToArray()
        };
    }

    private static string? EffectiveStyleLayerOwner(StudioCompositionBody body, JsonElement layer, string id)
    {
        if (layer.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("honua:compositionLayerId", out var owner) && owner.ValueKind == JsonValueKind.String)
            return owner.GetString();
        return body.Layers.FirstOrDefault(candidate => candidate.Id == id)?.Id
            ?? body.Layers.FirstOrDefault(candidate => $"{candidate.Id}__outline" == id)?.Id;
    }

    private static void ValidateStyleLayerOwner(StudioCompositionBody body, JsonElement layer, string id, string? sourceId)
    {
        string? owner = null;
        if (layer.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("honua:compositionLayerId", out var declared))
        {
            if (declared.ValueKind != JsonValueKind.String)
                throw new StudioCompositionBodyException("A style layer owner must name a composed layer.");
            owner = declared.GetString();
        }

        var identityOwner = body.Layers.FirstOrDefault(candidate => candidate.Id == id)
            ?? body.Layers.FirstOrDefault(candidate => $"{candidate.Id}__outline" == id);
        if (identityOwner is not null && owner is not null && owner != identityOwner.Id)
            throw new StudioCompositionBodyException("A style layer cannot claim another composed layer's identifier.");
        var composed = owner is null ? identityOwner : body.Layers.FirstOrDefault(candidate => candidate.Id == owner);
        if (owner is not null && composed is null
            || composed is not null && composed.SourceId != sourceId)
            throw new StudioCompositionBodyException("A style layer must retain its composed layer's authorized source.");
        if (composed is null && sourceId is not null && body.Layers.Any(candidate => candidate.SourceId == sourceId))
            throw new StudioCompositionBodyException("A new render layer must declare metadata.honua:compositionLayerId so removal and selection retain its owner.");
    }

    private static StudioCompositionBody RemoveComposedLayerAppearance(StudioCompositionBody body, string layerId, string? sourceId)
    {
        var styleRefs = body.StyleRefs?.Where(reference =>
            !(reference.ValueKind == JsonValueKind.Object
                && reference.TryGetProperty("styleId", out var id) && id.ValueKind == JsonValueKind.String
                && id.GetString() == $"honua-inline:{layerId}")).ToArray();
        if (body.MapSpec is not { ValueKind: JsonValueKind.Object } mapSpec)
            return body with { StyleRefs = styleRefs };

        var style = JsonNode.Parse(mapSpec.GetRawText())!.AsObject();
        if (style["layers"] is not JsonArray layers)
            throw new StudioCompositionBodyException("The stored map style has no layer array.");
        for (var index = layers.Count - 1; index >= 0; index--)
        {
            if (layers[index] is not JsonObject layer) continue;
            var id = layer["id"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            var owner = layer["metadata"] is JsonObject metadata
                && metadata["honua:compositionLayerId"] is JsonValue ownerValue
                && ownerValue.TryGetValue<string>(out var ownerText) ? ownerText : null;
            if (id == layerId || owner == layerId
                || id == $"{layerId}__outline" && !body.Layers.Any(candidate => candidate.Id == id))
                layers.RemoveAt(index);
        }
        if (sourceId is not null && !body.Layers.Any(layer => layer.SourceId == sourceId)
            && !layers.OfType<JsonObject>().Any(layer => layer["source"] is JsonValue value
                && value.TryGetValue<string>(out var source) && source == sourceId)
            && style["sources"] is JsonObject sources)
            sources.Remove(sourceId);
        using var document = JsonDocument.Parse(style.ToJsonString());
        return body with { StyleRefs = styleRefs, MapSpec = document.RootElement.Clone() };
    }

    private static Dictionary<string, JsonElement> UniqueMembers(JsonElement value)
    {
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!members.TryAdd(property.Name, property.Value))
            {
                throw new StudioCompositionBodyException("Style documents must not contain duplicate JSON members.");
            }
        }

        return members;
    }

    /// <summary>
    /// Replaces one composed layer's map-local overrides using the portable
    /// <c>styleRefs[].body</c> contract. The lifecycle caller owns generation CAS;
    /// this operation never changes a reusable catalog style or a data source.
    /// </summary>
    public static StudioCompositionBody SetInlineLayerStyle(
        StudioCompositionBody body, string layerId, JsonElement styleBody)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(layerId);
        var styleId = $"honua-inline:{layerId}";
        var updated = SetLayerStyleRef(body, layerId, styleId);
        if (styleId.Length > 256 || body.Layers.Any(layer =>
                !string.Equals(layer.Id, layerId, StringComparison.Ordinal)
                && string.Equals(layer.StyleRef, styleId, StringComparison.Ordinal)))
        {
            throw new StudioCompositionBodyException("A local style must belong only to its selected layer.");
        }

        ValidateInlineLayerOverrides(body, layerId, styleBody);
        var references = (body.StyleRefs ?? []).ToList();
        var found = -1;
        for (var index = 0; index < references.Count; index++)
        {
            var entry = references[index];
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("styleId", out var id) || id.ValueKind != JsonValueKind.String)
            {
                throw new StudioCompositionBodyException("Existing style references must have a string styleId.");
            }

            if (!string.Equals(id.GetString(), styleId, StringComparison.Ordinal))
            {
                continue;
            }

            if (found >= 0)
            {
                throw new StudioCompositionBodyException("Duplicate local style references must be resolved before editing.");
            }

            found = index;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("styleId", styleId);
            if (found >= 0)
            {
                foreach (var member in references[found].EnumerateObject())
                {
                    if (member.Name is not ("styleId" or "body"))
                    {
                        member.WriteTo(writer);
                    }
                }
            }

            writer.WritePropertyName("body");
            styleBody.WriteTo(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        if (found >= 0)
        {
            references[found] = document.RootElement.Clone();
        }
        else
        {
            references.Add(document.RootElement.Clone());
        }

        return updated with { StyleRefs = references };
    }

    private static void ValidateInlineLayerOverrides(StudioCompositionBody body, string layerId, JsonElement overrides)
    {
        if (overrides.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(overrides.GetRawText()) > 65536)
        {
            throw new StudioCompositionBodyException("styleBody must be a layer-override object of at most 64 KiB.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in overrides.EnumerateObject())
        {
            var isOwnOutline = string.Equals(entry.Name, $"{layerId}__outline", StringComparison.Ordinal)
                && !body.Layers.Any(layer => string.Equals(layer.Id, entry.Name, StringComparison.Ordinal));
            if (!seen.Add(entry.Name)
                || (!string.Equals(entry.Name, layerId, StringComparison.Ordinal) && !isOwnOutline)
                || entry.Value.ValueKind != JsonValueKind.Object)
            {
                throw new StudioCompositionBodyException("A local style may change only the selected layer and its outline.");
            }

            var properties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in entry.Value.EnumerateObject())
            {
                var valid = property.Name switch
                {
                    "paint" or "layout" or "metadata" => property.Value.ValueKind == JsonValueKind.Object,
                    "filter" => property.Value.ValueKind == JsonValueKind.Array,
                    "minzoom" or "maxzoom" => property.Value.ValueKind == JsonValueKind.Number
                        && property.Value.TryGetDouble(out var zoom)
                        && double.IsFinite(zoom) && zoom >= 0 && zoom <= 24,
                    _ => false
                };
                if (!properties.Add(property.Name) || !valid)
                {
                    throw new StudioCompositionBodyException(
                        $"Unsupported local style property '{property.Name}'; source and layer identity cannot be changed here.");
                }
            }

            if (entry.Value.TryGetProperty("minzoom", out var minimum)
                && entry.Value.TryGetProperty("maxzoom", out var maximum)
                && minimum.GetDouble() > maximum.GetDouble())
            {
                throw new StudioCompositionBodyException("Local style minzoom must not exceed maxzoom.");
            }
        }

        if (seen.Count == 0)
        {
            throw new StudioCompositionBodyException("styleBody must name the selected layer or its outline.");
        }
    }
}
