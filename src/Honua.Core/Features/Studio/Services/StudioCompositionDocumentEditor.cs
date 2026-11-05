// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Shared.Services;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Services;

// Members a document does not declare (sourceBindings, styleRefs, mapSpec) stay undeclared, as in the Studio reducer.
public static class StudioCompositionDocumentEditor
{
    private const string SourceBindingsMember = "sourceBindings";
    private const string StyleRefsMember = "styleRefs";
    private const string MapSpecMember = "mapSpec";
    private const string CompositionLayerIdsKey = "honua:compositionLayerIds";
    private const string InlineStyleIdPrefix = "honua-inline:";

    public static StudioPackageEnvelope RemoveLayer(StudioPackageEnvelope envelope, string layerId)
    {
        var body = StudioCompositionBodyEditor.ReadBody(envelope);
        var updated = StudioCompositionBodyEditor.RemoveLayer(body, layerId);
        var removedLayer = body.Layers.First(layer => string.Equals(layer.Id, layerId, StringComparison.Ordinal));
        var orphanedSourceId = StudioCompositionBodyEditor.SourceOrphanedBy(removedLayer, updated.Layers);
        var boundSourceIds = updated.Layers
            .Select(layer => layer.SourceId)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        return EditDocument(StudioCompositionBodyEditor.WriteBody(envelope, updated), document =>
        {
            RemoveArrayItems(document[SourceBindingsMember], binding => !IsBoundToAny(binding, boundSourceIds));
            RemoveArrayItems(document[StyleRefsMember], styleRef => HasMember(styleRef, "styleId", InlineStyleId(layerId)));
            PruneMapSpec(document[MapSpecMember] as JsonObject, layerId, orphanedSourceId);
        });
    }

    public static StudioPackageEnvelope SetLayerInlineStyle(StudioPackageEnvelope envelope, string layerId, JsonElement styleBody) =>
        RefuseUnpairedSurrogate(() => BindInlineStyle(envelope, layerId, styleBody));

    // An edited map style owns every layer's styling, so style definitions and layer style bindings are cleared.
    public static StudioPackageEnvelope SetMapStyle(StudioPackageEnvelope envelope, JsonElement mapSpec) =>
        RefuseUnpairedSurrogate(() => ReplaceMapStyle(envelope, mapSpec));

    private static StudioPackageEnvelope RefuseUnpairedSurrogate(Func<StudioPackageEnvelope> edit)
    {
        try
        {
            return edit();
        }
        catch (UnpairedUtf16SurrogateException exception)
        {
            throw new StudioCompositionConflictException(exception.Message);
        }
        catch (InvalidOperationException exception) when (exception is not StudioCompositionConflictException
            && exception.Message.Contains("UTF-16", StringComparison.Ordinal))
        {
            // The JSON reader fails a property name or a string value that holds an unpaired surrogate.
            throw new StudioCompositionConflictException(UnpairedUtf16SurrogateException.MessageText);
        }
    }

    private static StudioPackageEnvelope BindInlineStyle(StudioPackageEnvelope envelope, string layerId, JsonElement styleBody)
    {
        var styleNode = JsonNode.Parse(styleBody.GetRawText());
        if (StudioMapStyleEditValidator.FindInlineStyleViolation(layerId, styleNode) is { } violation)
        {
            throw new StudioCompositionConflictException(violation);
        }

        var styleId = InlineStyleId(layerId);
        var bound = StudioCompositionBodyEditor.EditBody(
            envelope, body => StudioCompositionBodyEditor.SetLayerStyleRef(body, layerId, styleId));

        return EditDocument(bound, document =>
        {
            if (document[StyleRefsMember] is not JsonArray styleRefs)
            {
                styleRefs = [];
                document[StyleRefsMember] = styleRefs;
            }

            RemoveArrayItems(styleRefs, styleRef => HasMember(styleRef, "styleId", styleId));
            styleRefs.Add(new JsonObject { ["styleId"] = styleId, ["body"] = styleNode });
        });
    }

    private static StudioPackageEnvelope ReplaceMapStyle(StudioPackageEnvelope envelope, JsonElement mapSpec)
    {
        var candidate = JsonNode.Parse(mapSpec.GetRawText());
        var unbound = StudioCompositionBodyEditor.EditBody(envelope, body => body with
        {
            Layers = body.Layers.Select(layer => layer with { StyleRef = null }).ToList(),
        });

        return EditDocument(unbound, document =>
        {
            if (document[MapSpecMember] is not JsonObject anchor)
            {
                throw new StudioCompositionConflictException("Initialize the authorized map style before editing it.");
            }

            if (StudioMapStyleEditValidator.FindMapStyleViolation(anchor, candidate) is { } violation)
            {
                throw new StudioCompositionConflictException(violation);
            }

            document[MapSpecMember] = candidate;
            document[StyleRefsMember] = new JsonArray();
        });
    }

    private static string InlineStyleId(string layerId) => InlineStyleIdPrefix + layerId;

    private static void PruneMapSpec(JsonObject? mapSpec, string layerId, string? orphanedSourceId)
    {
        if (mapSpec?["layers"] is not JsonArray renderLayers)
        {
            return;
        }

        RemoveArrayItems((mapSpec["metadata"] as JsonObject)?[CompositionLayerIdsKey], owner => IsString(owner, layerId));
        if (orphanedSourceId is not null && mapSpec["sources"] is JsonObject sources)
        {
            sources.Remove(orphanedSourceId);
        }

        RemoveArrayItems(renderLayers, node => node is JsonObject renderLayer && IsDrawnFor(renderLayer, layerId));
    }

    private static bool IsDrawnFor(JsonObject renderLayer, string layerId) =>
        IsString(renderLayer["id"], layerId)
        || IsString(renderLayer["id"], StudioMapStyleEditValidator.OutlineLayerId(layerId))
        || IsString(StudioMapStyleEditValidator.CompositionLayerIdOf(renderLayer), layerId);

    private static void RemoveArrayItems(JsonNode? node, Func<JsonNode?, bool> shouldRemove)
    {
        if (node is not JsonArray array)
        {
            return;
        }

        for (var index = array.Count - 1; index >= 0; index--)
        {
            if (shouldRemove(array[index]))
            {
                array.RemoveAt(index);
            }
        }
    }

    private static bool IsBoundToAny(JsonNode? binding, HashSet<string> sourceIds) =>
        binding is JsonObject item
        && item["sourceId"] is JsonValue value
        && value.GetValueKind() == JsonValueKind.String
        && sourceIds.Contains(value.GetValue<string>());

    private static bool HasMember(JsonNode? node, string member, string expected) =>
        node is JsonObject item && IsString(item[member], expected);

    private static bool IsString(JsonNode? node, string expected) =>
        node is JsonValue value
        && value.GetValueKind() == JsonValueKind.String
        && string.Equals(value.GetValue<string>(), expected, StringComparison.Ordinal);

    private static StudioPackageEnvelope EditDocument(StudioPackageEnvelope envelope, Action<JsonObject> edit)
    {
        var document = JsonNode.Parse(envelope.Body!.Value.GetRawText())!.AsObject();
        edit(document);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            document.WriteTo(writer);
        }

        using var parsed = JsonDocument.Parse(buffer.WrittenMemory);
        return envelope with { Body = parsed.RootElement.Clone() };
    }
}
