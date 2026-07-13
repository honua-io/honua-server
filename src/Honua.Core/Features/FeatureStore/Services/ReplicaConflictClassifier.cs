// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Services;

/// <summary>
/// Refines the coarse, detection-time classification of a disconnected-sync conflict once the client
/// (uploaded) and server (pre-apply) feature states have been captured (#1287). The canonical
/// <see cref="ReplicaSyncService"/> classifies conflicts from operation kinds alone (it cannot see
/// geometry or attribute values), so an <c>update vs concurrent server update</c> is recorded as the
/// generic <see cref="ReplicaConflictType.Attribute"/>. When the captured states show the client
/// changed no attribute but the geometry differs, the protocol adapter re-classifies the conflict as
/// <see cref="ReplicaConflictType.Geometry"/> before persisting the durable record.
/// </summary>
/// <remarks>
/// The states are the conflict-review envelope <c>{"attributes": { ... }, "geometry": ...}</c> produced
/// by the protocol adapter, with geometry serialized identically on both sides so it is directly
/// comparable. This classifier answers the single yes/no question "is this geometry-only?"; the richer
/// field-level detail surfaced by the review API is computed separately by <c>ReplicaConflictDiff</c> on
/// the server. "The client changed no attribute" is evaluated against the fields the client actually
/// supplied — server-only business/system fields the client did not touch are ignored, and field-name
/// matching is case-insensitive (Esri field-name convention). Structural conflict types
/// (<see cref="ReplicaConflictType.DeleteUpdate"/>/<see cref="ReplicaConflictType.UpdateDelete"/>) and
/// the not-yet-detectable types (<see cref="ReplicaConflictType.DuplicateInsert"/>,
/// <see cref="ReplicaConflictType.Attachment"/>, <see cref="ReplicaConflictType.Relationship"/>, which
/// the replica upload model does not carry the inputs to detect) are never reclassified here.
/// </remarks>
public static class ReplicaConflictClassifier
{
    private const string AttributesProperty = "attributes";
    private const string GeometryProperty = "geometry";

    /// <summary>
    /// Returns a refined conflict type for the captured states, or <paramref name="current"/> unchanged
    /// when no refinement applies (non-attribute conflict, missing/unparseable states, the client
    /// changed an attribute, or the geometry is unknown/unchanged).
    /// </summary>
    /// <param name="current">The detection-time classification recorded by the sync service.</param>
    /// <param name="clientStateJson">The client (uploaded) state envelope, when captured.</param>
    /// <param name="serverStateJson">The pre-apply server state envelope, when captured.</param>
    public static ReplicaConflictType Refine(
        ReplicaConflictType current,
        string? clientStateJson,
        string? serverStateJson)
    {
        // Only the generic attribute bucket is a refinement candidate; delete/update and the structural
        // types are already specific. Both sides must be present to compare.
        if (current != ReplicaConflictType.Attribute ||
            string.IsNullOrWhiteSpace(clientStateJson) ||
            string.IsNullOrWhiteSpace(serverStateJson))
        {
            return current;
        }

        try
        {
            using var clientDoc = JsonDocument.Parse(clientStateJson);
            using var serverDoc = JsonDocument.Parse(serverStateJson);
            var client = clientDoc.RootElement;
            var server = serverDoc.RootElement;

            // Geometry-only divergence: the client changed no attribute but the geometry differs.
            return !ClientChangedAttributes(client, server) && GeometryDiffers(client, server)
                ? ReplicaConflictType.Geometry
                : current;
        }
        catch (JsonException)
        {
            // Malformed envelopes are never reclassified; the coarse type stands.
            return current;
        }
    }

    private static bool ClientChangedAttributes(JsonElement client, JsonElement server)
    {
        var clientAttributes = TryGetObject(client, AttributesProperty);
        var serverAttributes = TryGetObject(server, AttributesProperty);

        // If either attribute set is absent we cannot prove the client made no attribute change.
        if (clientAttributes is null || serverAttributes is null)
        {
            return true;
        }

        // Compare only the fields the client supplied; server-only fields the client did not touch are
        // not a client-side change. A client field missing on the server (or with a different value) is.
        foreach (var clientField in clientAttributes.Value.EnumerateObject())
        {
            var serverValue = FindProperty(serverAttributes.Value, clientField.Name);
            if (serverValue is null || !ValuesEqual(clientField.Value, serverValue.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool GeometryDiffers(JsonElement client, JsonElement server)
    {
        var clientGeometry = TryGetGeometryRaw(client);
        var serverGeometry = TryGetGeometryRaw(server);

        // Unknown on either side → not classifiable as a geometry conflict.
        if (clientGeometry is null || serverGeometry is null)
        {
            return false;
        }

        return !string.Equals(clientGeometry, serverGeometry, StringComparison.Ordinal);
    }

    private static string? TryGetGeometryRaw(JsonElement state)
    {
        if (state.ValueKind == JsonValueKind.Object &&
            state.TryGetProperty(GeometryProperty, out var geometry) &&
            geometry.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            return geometry.GetRawText();
        }

        return null;
    }

    private static JsonElement? TryGetObject(JsonElement state, string property)
    {
        if (state.ValueKind == JsonValueKind.Object &&
            state.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }

        return null;
    }

    private static JsonElement? FindProperty(JsonElement container, string name)
    {
        if (container.TryGetProperty(name, out var exact))
        {
            return exact;
        }

        // Esri attribute names are conventionally case-insensitive; fall back to a case-insensitive scan.
        foreach (var property in container.EnumerateObject().Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return property.Value;
        }

        return null;
    }

    private static bool ValuesEqual(JsonElement left, JsonElement right)
        // Raw-text comparison is sufficient for the scalar/array/object attribute values stored in the
        // state envelope.
        => string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
}
