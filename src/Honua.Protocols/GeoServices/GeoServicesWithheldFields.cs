// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices;

// Withheld fields are the publisher-hidden fields plus the caller's field masks; metadata must not name them.
internal static partial class GeoServicesWithheldFields
{
    private static readonly HashSet<string> FieldReferenceProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "field",
        "field1",
        "field2",
        "field3",
        "fields",
        "normalizationField",
        "normalizeField",
        "tooltipField",
        "fieldName",
        "rotationField",
    };

    // Label classes and popup field entries can be dropped one by one; other references drop the document.
    private static readonly string[] SeparableEntryArrays = ["labelingInfo", "fieldInfos"];

    // Marks the caller's masked fields hidden, so every listing that omits hidden fields omits them too.
    public static async Task<MetadataV2Object> HideMaskedFieldsAsync(
        HttpContext context, MetadataV2Object resource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resource);
        if (context.RequestServices.GetService<IFieldMaskSource>() is not { } masks)
        {
            return resource;
        }

        var masked = await masks.ResolveAsync(resource, cancellationToken).ConfigureAwait(false);
        return resource.WithSchemaFieldsHidden(masked);
    }

    // Hidden fields plus every field the caller's masks name, including masked names the schema does not declare.
    public static async Task<IReadOnlySet<string>> WithheldFieldNamesAsync(
        HttpContext context, MetadataV2Object resource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resource);
        var withheld = new HashSet<string>(resource.HiddenSchemaFieldNames(), StringComparer.OrdinalIgnoreCase);
        if (context.RequestServices.GetService<IFieldMaskSource>() is { } masks)
        {
            withheld.UnionWith(await masks.ResolveAsync(resource, cancellationToken).ConfigureAwait(false));
        }

        return withheld;
    }

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
            JsonValueKind.String => NamesWithheldField(propertyName, element.GetString() ?? string.Empty, withheld),
            _ => false,
        };

    private static bool NamesWithheldField(string? propertyName, string value, IReadOnlySet<string> withheld)
    {
        if (propertyName is not null
            && FieldReferenceProperties.Contains(propertyName)
            && withheld.Contains(value.Trim()))
        {
            return true;
        }

        // Arcade ($feature.name, $feature["name"], DomainName($feature, "name")), label ([name]), popup ({name}) and SQL where references.
        var pattern = string.Equals(propertyName, "where", StringComparison.OrdinalIgnoreCase)
            ? SqlIdentifier()
            : ExpressionFieldReference();
        return pattern.Matches(value).Any(match => withheld.Contains(match.Groups["name"].Value.Trim()));
    }

    [GeneratedRegex(
        """\$feature\.(?<name>[A-Za-z_]\w*)|\$feature\[\s*["'](?<name>[^"']+)["']\s*\]|\$feature\s*,\s*["'](?<name>[^"']+)["']|\[(?<name>[^\[\]]+)\]|\{(?<name>[^{}]+)\}""",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex ExpressionFieldReference();

    [GeneratedRegex("""(?<name>[A-Za-z_]\w*)""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SqlIdentifier();
}
