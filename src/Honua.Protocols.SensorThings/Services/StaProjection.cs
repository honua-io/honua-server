// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Nodes;

namespace Honua.Protocols.SensorThings.Services;

/// <summary>
/// Applies a validated <c>$select</c> projection to an already-serialized STA entity or
/// entity set. The projection runs over the JSON the source-generated context produced, so
/// the wire shape of an unprojected response is unchanged and no reflection-based
/// serialization is introduced (#4201).
/// </summary>
internal static class StaProjection
{
    /// <summary>
    /// Keeps only <paramref name="members"/> on each entity inside an entity-set envelope,
    /// leaving the envelope annotations (<c>@iot.count</c>, <c>@iot.nextLink</c>) intact.
    /// </summary>
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

    /// <summary>
    /// Keeps only <paramref name="members"/> on a single entity. A selected navigation
    /// property keeps both its <c>@iot.navigationLink</c> and, when the same request
    /// expanded it, the inline entity.
    /// </summary>
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

            entity.Remove(property.Key);
            projected[property.Key] = property.Value;
        }

        return projected;
    }
}
