// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;

namespace Honua.Protocols.SensorThings.Services;

internal static class StaProjection
{
    public static JsonObject ProjectEntitySet(JsonObject entitySet, IReadOnlyList<string> members)
    {
        ArgumentNullException.ThrowIfNull(entitySet);

        if (entitySet["value"] is JsonArray value)
        {
            for (var i = 0; i < value.Count; i++)
            {
                if (value[i] is JsonObject entity)
                {
                    value[i] = ProjectEntity(entity, members);
                }
            }
        }

        return entitySet;
    }

    public static JsonObject ProjectEntity(JsonObject entity, IReadOnlyList<string> members)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(members);

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members)
        {
            keep.Add(member);
            keep.Add($"{member}@iot.navigationLink");
        }

        var projected = new JsonObject();
        foreach (var property in entity.ToArray())
        {
            if (!keep.Contains(property.Key))
            {
                continue;
            }

            projected[property.Key] = property.Value?.DeepClone();
        }

        foreach (var member in members.Where(member => member.Contains('/', StringComparison.Ordinal)))
        {
            var parts = member.Split('/');
            JsonNode? source = entity;
            var target = projected;
            for (var index = 0; index < parts.Length; index++)
            {
                if (source is not JsonObject complex || !complex.TryGetPropertyValue(parts[index], out source)) break;
                if (index == parts.Length - 1 || source is null)
                {
                    target[parts[index]] = source?.DeepClone();
                    break;
                }
                if (target[parts[index]] is not JsonObject nested)
                {
                    nested = new JsonObject();
                    target[parts[index]] = nested;
                }
                target = nested;
            }
        }

        return projected;
    }
}
