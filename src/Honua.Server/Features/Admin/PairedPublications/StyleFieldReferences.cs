// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;

namespace Honua.Server.Features.Admin.PairedPublications;

// The feature property names a MapLibre layer reads: ["get"/"has", name] expressions, legacy
// filters (["==", name, value], ["in", name, ...], ["has", name]) and legacy function "property" keys.
// $type and $id are geometry and id accessors, not properties. A computed name (["get", [...]])
// cannot be checked, so it is reported as Computed, which no field list contains.
internal static class StyleFieldReferences
{
    internal const string Computed = "\0computed";

    private static readonly HashSet<string> PropertyOperators =
        ["get", "has", "!has", "==", "!=", "<", "<=", ">", ">=", "in", "!in"];

    private static readonly HashSet<string> Accessors = ["$type", "$id"];

    public static IReadOnlySet<string> Of(JsonObject layer)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in new[] { layer["filter"], layer["paint"], layer["layout"] })
        {
            Collect(part, names);
        }

        names.ExceptWith(Accessors);
        names.Remove(string.Empty);
        return names;
    }

    private static void Collect(JsonNode? node, HashSet<string> names)
    {
        switch (node)
        {
            case JsonArray array:
                if (array.Count >= 2 && Text(array[0]) is { } op && PropertyOperators.Contains(op))
                {
                    names.Add(Text(array[1]) ?? (op is "get" or "has" ? Computed : string.Empty));
                }

                foreach (var item in array)
                {
                    Collect(item, names);
                }

                break;
            case JsonObject value:
                if (Text(value["property"]) is { } property)
                {
                    names.Add(property);
                }

                foreach (var (_, child) in value)
                {
                    Collect(child, names);
                }

                break;
        }
    }

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
