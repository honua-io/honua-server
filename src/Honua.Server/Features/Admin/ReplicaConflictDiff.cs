// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Computes the field-level and geometry-change comparison between the base, client, and server
/// states of a durable disconnected-sync conflict (#1287). The states are opaque feature objects of
/// the shape <c>{"attributes": { ... }, "geometry": ...}</c>; this derives the per-attribute deltas
/// and a geometry-changed signal that the Console conflict reviewer renders, without the detail
/// endpoint needing to understand the protocol wire shape beyond that envelope.
/// </summary>
internal static class ReplicaConflictDiff
{
    private const string AttributesProperty = "attributes";
    private const string GeometryProperty = "geometry";

    /// <summary>
    /// Computes the geometry-changed flag and the set of diverging attribute fields across the three
    /// states. Only fields that differ between at least two states are emitted; when no client or
    /// server state is available the field list is empty and geometry-changed is null.
    /// </summary>
    public static (bool? GeometryChanged, ReplicaConflictFieldChange[] FieldChanges) Compute(
        JsonElement? baseState,
        JsonElement? clientState,
        JsonElement? serverState)
    {
        var fieldChanges = ComputeFieldChanges(baseState, clientState, serverState);
        var geometryChanged = ComputeGeometryChanged(clientState, serverState);
        return (geometryChanged, fieldChanges);
    }

    private static ReplicaConflictFieldChange[] ComputeFieldChanges(
        JsonElement? baseState,
        JsonElement? clientState,
        JsonElement? serverState)
    {
        var baseAttributes = TryGetObject(baseState, AttributesProperty);
        var clientAttributes = TryGetObject(clientState, AttributesProperty);
        var serverAttributes = TryGetObject(serverState, AttributesProperty);

        // A field comparison is only meaningful when at least the two conflicting sides are present.
        if (clientAttributes is null || serverAttributes is null)
        {
            return [];
        }

        var fieldNames = new SortedSet<string>(StringComparer.Ordinal);
        CollectKeys(baseAttributes, fieldNames);
        CollectKeys(clientAttributes, fieldNames);
        CollectKeys(serverAttributes, fieldNames);

        var changes = new List<ReplicaConflictFieldChange>();
        foreach (var field in fieldNames)
        {
            var baseValue = TryGetProperty(baseAttributes, field);
            var clientValue = TryGetProperty(clientAttributes, field);
            var serverValue = TryGetProperty(serverAttributes, field);

            // Skip fields where all available states agree — the operator only needs the deltas.
            if (ValuesEqual(clientValue, serverValue) &&
                (baseAttributes is null || ValuesEqual(baseValue, clientValue)))
            {
                continue;
            }

            // With a base present, "changed on X" means X diverged from the common ancestor. Without a
            // base, the only knowable fact is that client and server disagree, so both sides are flagged.
            bool changedOnClient;
            bool changedOnServer;
            if (baseAttributes is not null)
            {
                changedOnClient = !ValuesEqual(baseValue, clientValue);
                changedOnServer = !ValuesEqual(baseValue, serverValue);
            }
            else
            {
                changedOnClient = changedOnServer = !ValuesEqual(clientValue, serverValue);
            }

            changes.Add(new ReplicaConflictFieldChange
            {
                Field = field,
                BaseValue = baseValue,
                ClientValue = clientValue,
                ServerValue = serverValue,
                ChangedOnClient = changedOnClient,
                ChangedOnServer = changedOnServer,
            });
        }

        return changes.ToArray();
    }

    private static bool? ComputeGeometryChanged(JsonElement? clientState, JsonElement? serverState)
    {
        var clientGeometry = TryGetGeometry(clientState);
        var serverGeometry = TryGetGeometry(serverState);
        if (clientGeometry is null || serverGeometry is null)
        {
            return null;
        }

        return !string.Equals(clientGeometry, serverGeometry, StringComparison.Ordinal);
    }

    private static string? TryGetGeometry(JsonElement? state)
    {
        if (state is { ValueKind: JsonValueKind.Object } element &&
            element.TryGetProperty(GeometryProperty, out var geometry) &&
            geometry.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            return geometry.GetRawText();
        }

        return null;
    }

    private static JsonElement? TryGetObject(JsonElement? state, string property)
    {
        if (state is { ValueKind: JsonValueKind.Object } element &&
            element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }

        return null;
    }

    private static JsonElement? TryGetProperty(JsonElement? container, string name)
    {
        if (container is { ValueKind: JsonValueKind.Object } element &&
            element.TryGetProperty(name, out var value))
        {
            return value;
        }

        return null;
    }

    private static void CollectKeys(JsonElement? attributes, SortedSet<string> fieldNames)
    {
        if (attributes is { ValueKind: JsonValueKind.Object } element)
        {
            foreach (var property in element.EnumerateObject())
            {
                fieldNames.Add(property.Name);
            }
        }
    }

    private static bool ValuesEqual(JsonElement? left, JsonElement? right)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        // Raw-text comparison is sufficient for the scalar/array/object attribute values stored in the
        // state envelope; it treats absent and JSON null as distinct (handled by the null checks above).
        return string.Equals(left.Value.GetRawText(), right.Value.GetRawText(), StringComparison.Ordinal);
    }
}
