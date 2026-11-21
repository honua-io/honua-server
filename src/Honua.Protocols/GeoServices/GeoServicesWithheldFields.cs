// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Rendering;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices;

// Withheld fields are the publisher-hidden fields plus the caller's field masks; metadata must not name them.
internal static class GeoServicesWithheldFields
{
    // Label classes and popup field entries can be dropped one by one; other references drop the document.
    private static readonly string[] SeparableEntryArrays = ["labelingInfo", "fieldInfos"];

    // Marks the caller's masked fields hidden, so every listing that omits hidden fields omits them too.
    public static Task<MetadataV2Object> HideMaskedFieldsAsync(
        HttpContext context, MetadataV2Object resource, CancellationToken cancellationToken)
        => CallerVisibleSchema.HideMaskedFieldsAsync(context, resource, cancellationToken);

    // Hidden fields plus every field the caller's masks name, including masked names the schema does not declare.
    public static Task<IReadOnlySet<string>> WithheldFieldNamesAsync(
        HttpContext context, MetadataV2Object resource, CancellationToken cancellationToken)
        => StyleDocumentWithheldFields.ResolveAsync(context, resource, cancellationToken);

    // A renderer, label class or popup that names a withheld field would disclose it, and a class renderer its values.
    public static JsonElement? WithoutWithheldReferences(JsonElement? document, MetadataV2Object schemaResource)
    {
        ArgumentNullException.ThrowIfNull(schemaResource);
        var withheld = schemaResource.HiddenSchemaFieldNames();
        if (document is not { } value || !References(value, null, withheld))
        {
            return document;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var trimmed = WithoutWithheldEntries(value, withheld);
        return References(trimmed, null, withheld) ? null : trimmed;
    }

    // True when a renderer, label class or popup document names one of the withheld fields.
    public static bool NamesWithheldField(JsonElement document, IReadOnlySet<string> withheld)
    {
        ArgumentNullException.ThrowIfNull(withheld);
        return References(document, null, withheld);
    }

    // A time extent over a withheld field would disclose that field's values.
    public static FeatureServerTimeInfo? WithoutWithheldTimeFields(MetadataV2Object schemaResource, FeatureServerTimeInfo? timeInfo)
    {
        ArgumentNullException.ThrowIfNull(schemaResource);
        return timeInfo is not null
               && (schemaResource.IsHiddenSchemaField(timeInfo.StartTimeField)
                   || schemaResource.IsHiddenSchemaField(timeInfo.EndTimeField)
                   || schemaResource.IsHiddenSchemaField(timeInfo.TrackIdField))
            ? null
            : timeInfo;
    }

    private static JsonElement WithoutWithheldEntries(JsonElement document, IReadOnlySet<string> withheld)
    {
        var node = JsonNode.Parse(document.GetRawText())!.AsObject();
        foreach (var name in SeparableEntryArrays)
        {
            if (document.TryGetProperty(name, out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                node[name] = new JsonArray([.. entries.EnumerateArray()
                    .Where(entry => !References(entry, null, withheld))
                    .Select(entry => JsonNode.Parse(entry.GetRawText()))]);
            }
        }

        return JsonElement.Parse(node.ToJsonString());
    }

    private static bool References(JsonElement element, string? propertyName, IReadOnlySet<string> withheld)
        => withheld.Count > 0 && element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().Any(property => References(property.Value, property.Name, withheld)),
            JsonValueKind.Array => element.EnumerateArray().Any(item => References(item, propertyName, withheld)),
            JsonValueKind.String => StyleDocumentWithheldFields.NamesWithheldField(propertyName, element.GetString() ?? string.Empty, withheld),
            _ => false,
        };
}
