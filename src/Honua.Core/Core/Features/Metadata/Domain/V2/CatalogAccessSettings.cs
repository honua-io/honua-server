// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Text.Json;

namespace Honua.Core.Features.Metadata.Domain.V2;

// Compares the access-related settings of two catalog documents. An automatic rollback restores a
// whole earlier document, so it is allowed only when that document grants exactly the access the
// current one does; any change to who may read, write or see a resource needs an operator. The key
// list errs broad: a false difference only means the rollback is left to an operator.
public static class CatalogAccessSettings
{
    private static readonly FrozenSet<string> AccessKeys = new[]
    {
        "accessPolicy", "permanentFilter", "permissions", "policies", "policyIds", "ownerEditPolicy",
        "publication", "publications", "publicationIds", "roles", "role", "tenant", "hidden",
        "defaultVisibility", "restrictive", "status", "lifecycle", "editing", "editable", "canModify",
        "capabilities", "declaredOperations", "protocols", "enabledProtocols", "enabled", "isEnabled",
        "queryable", "deleted", "retired",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool Match(string firstDocument, string secondDocument)
    {
        ArgumentNullException.ThrowIfNull(firstDocument);
        ArgumentNullException.ThrowIfNull(secondDocument);
        return Project(firstDocument).SequenceEqual(Project(secondDocument), StringComparer.Ordinal);
    }

    private static List<string> Project(string document)
    {
        using var json = JsonDocument.Parse(document);
        var settings = new List<string>();
        Collect(json.RootElement, "$", settings);
        settings.Sort(StringComparer.Ordinal);
        return settings;
    }

    private static void Collect(JsonElement element, string path, List<string> settings)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = path + "." + property.Name;
                if (AccessKeys.Contains(property.Name))
                {
                    settings.Add(propertyPath + "=" + Canonical(property.Value));
                }
                else
                {
                    Collect(property.Value, propertyPath, settings);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                // Identified elements are matched by id, so reordering a list is not an access change.
                Collect(item, path + "[" + (Identity(item) ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "]", settings);
                index++;
            }
        }
    }

    private static string? Identity(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
        {
            return "id=" + id.GetString();
        }

        return item.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("id", out var nested) && nested.ValueKind == JsonValueKind.String
            ? "id=" + nested.GetString()
            : null;
    }

    // Property order inside a setting is not meaningful, so objects are written with sorted keys.
    private static string Canonical(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => "\"" + JsonEncodedText.Encode(property.Name) + "\":" + Canonical(property.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Canonical)) + "]",
        _ => element.GetRawText(),
    };
}
