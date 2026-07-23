// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Routing.Features.Routing.Domain;

/// <summary>
/// Centralised, provider-neutral validation for batched topology content edits (#2716).
/// Runs entirely in memory (no database access) so it can reject a malformed or unsafe
/// batch before a transaction is opened. Cross-generation checks that require a database
/// round trip (does an id already exist, does a referenced edge exist) are deliberately
/// left to the storage layer, which enforces them transactionally.
/// </summary>
public static class NetworkTopologyEditValidation
{
    /// <summary>Maximum number of add+update+delete items accepted per edge list family.</summary>
    public const int MaxEdgeItemsPerBatch = 500;

    /// <summary>Maximum number of add+update+delete items accepted per restriction list family.</summary>
    public const int MaxRestrictionItemsPerBatch = 500;

    private const int MaxIdLength = 256;
    private const int MaxAttributeKeyLength = 128;
    private const int MaxAttributeValueLength = 256;
    private const int MaxAttributeCount = 64;

    /// <summary>
    /// Validates a full edit batch: sizes, duplicate ids, edge geometry/SRID, and the
    /// allowlisted, dataset-backed edge attribute keys (#2655 travel-profile cost columns).
    /// </summary>
    /// <param name="batch">The batch to validate.</param>
    /// <param name="generationSrid">The SRID edge geometry must be expressed in.</param>
    /// <param name="allowedAttributeKeys">
    /// The exact set of edge attribute keys the dataset's travel profiles back (forward/reverse
    /// cost columns). Any attribute key outside this set is rejected.
    /// </param>
    /// <param name="error">Set to a sanitized, client-safe validation message on failure.</param>
    /// <returns><see langword="true"/> when the batch is well-formed.</returns>
    public static bool TryValidateBatch(
        NetworkTopologyEditBatch batch,
        int generationSrid,
        IReadOnlySet<string> allowedAttributeKeys,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(allowedAttributeKeys);

        if (batch.IsEmpty)
        {
            error = "Edit batch must contain at least one edge or turn-restriction mutation.";
            return false;
        }

        if (batch.EdgeItemCount > MaxEdgeItemsPerBatch)
        {
            error = $"Edit batch contains {batch.EdgeItemCount} edge mutations, exceeding the maximum of {MaxEdgeItemsPerBatch}.";
            return false;
        }

        if (batch.RestrictionItemCount > MaxRestrictionItemsPerBatch)
        {
            error = $"Edit batch contains {batch.RestrictionItemCount} turn-restriction mutations, exceeding the maximum of {MaxRestrictionItemsPerBatch}.";
            return false;
        }

        if (!TryValidateNoDuplicateIds(
                batch.AddEdges.Select(e => e.EdgeId),
                batch.UpdateEdges.Select(e => e.EdgeId),
                batch.DeleteEdgeIds,
                "edge",
                out error))
        {
            return false;
        }

        if (!TryValidateNoDuplicateIds(
                batch.AddRestrictions.Select(r => r.RestrictionId),
                batch.UpdateRestrictions.Select(r => r.RestrictionId),
                batch.DeleteRestrictionIds,
                "turn restriction",
                out error))
        {
            return false;
        }

        // codeql[cs/linq/missed-where] -- predicate assigns the caller-visible out parameter.
        foreach (var edge in batch.AddEdges.Concat(batch.UpdateEdges))
        {
            if (!TryValidateEdge(edge, generationSrid, allowedAttributeKeys, out error))
            {
                return false;
            }
        }

        // codeql[cs/linq/missed-where] -- predicate assigns the caller-visible out parameter.
        foreach (var edgeId in batch.DeleteEdgeIds)
        {
            if (!TryValidateStableId(edgeId, "edge id", out error))
            {
                return false;
            }
        }

        // codeql[cs/linq/missed-where] -- predicate assigns the caller-visible out parameter.
        foreach (var restriction in batch.AddRestrictions.Concat(batch.UpdateRestrictions))
        {
            if (!TryValidateRestriction(restriction, out error))
            {
                return false;
            }
        }

        // codeql[cs/linq/missed-where] -- predicate assigns the caller-visible out parameter.
        foreach (var restrictionId in batch.DeleteRestrictionIds)
        {
            if (!TryValidateStableId(restrictionId, "turn restriction id", out error))
            {
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Validates that a stable id (edge, vertex, or turn-restriction id) is a safe,
    /// bounded, printable value. Ids are always bound as SQL parameters (never
    /// interpolated), so this check is deliberately permissive about character set.
    /// </summary>
    public static bool TryValidateStableId(string? id, string role, out string error)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            error = $"{role} is required.";
            return false;
        }

        if (id.Length > MaxIdLength)
        {
            error = $"{role} must be {MaxIdLength} characters or fewer.";
            return false;
        }

        if (id.Any(char.IsControl))
        {
            error = $"{role} must not contain control characters.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateEdge(
        NetworkEdgeEdit edge,
        int generationSrid,
        IReadOnlySet<string> allowedAttributeKeys,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(edge);

        if (!TryValidateStableId(edge.EdgeId, "edge id", out error))
        {
            return false;
        }

        if (!TryValidateStableId(edge.SourceVertexId, "edge source vertex id", out error))
        {
            return false;
        }

        if (!TryValidateStableId(edge.TargetVertexId, "edge target vertex id", out error))
        {
            return false;
        }

        if (edge.Srid != generationSrid)
        {
            error = $"Edge '{edge.EdgeId}' srid {edge.Srid} does not match the generation srid {generationSrid}.";
            return false;
        }

        if (!TryValidateLinearGeoJson(edge.GeometryGeoJson, out error))
        {
            error = $"Edge '{edge.EdgeId}' geometry is invalid: {error}";
            return false;
        }

        if (!TryValidateAttributes(edge.Attributes, allowedAttributeKeys, out error))
        {
            error = $"Edge '{edge.EdgeId}' attributes are invalid: {error}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateRestriction(NetworkTurnRestrictionEdit restriction, out string error)
    {
        ArgumentNullException.ThrowIfNull(restriction);

        if (!TryValidateStableId(restriction.RestrictionId, "turn restriction id", out error))
        {
            return false;
        }

        if (!TryValidateStableId(restriction.FromEdgeId, "turn restriction from-edge id", out error))
        {
            return false;
        }

        if (!TryValidateStableId(restriction.ViaVertexId, "turn restriction via-vertex id", out error))
        {
            return false;
        }

        if (!TryValidateStableId(restriction.ToEdgeId, "turn restriction to-edge id", out error))
        {
            return false;
        }

        if (!Enum.IsDefined(restriction.Kind))
        {
            error = $"Turn restriction '{restriction.RestrictionId}' has an unrecognised kind.";
            return false;
        }

        if (restriction.Kind == NetworkTurnRestrictionKind.Penalty)
        {
            if (restriction.Penalty is not { } penalty || !double.IsFinite(penalty) || penalty < 0)
            {
                error = $"Turn restriction '{restriction.RestrictionId}' is a penalty restriction and requires a finite, non-negative penalty.";
                return false;
            }
        }
        else if (restriction.Penalty is not null)
        {
            error = $"Turn restriction '{restriction.RestrictionId}' must not set a penalty unless its kind is 'penalty'.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateAttributes(
        IReadOnlyDictionary<string, string?> attributes,
        IReadOnlySet<string> allowedAttributeKeys,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        if (attributes.Count > MaxAttributeCount)
        {
            error = $"attribute count {attributes.Count} exceeds the maximum of {MaxAttributeCount}.";
            return false;
        }

        foreach (var (key, value) in attributes)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > MaxAttributeKeyLength || key.Any(char.IsControl))
            {
                error = "attribute key is missing, too long, or contains control characters.";
                return false;
            }

            if (!allowedAttributeKeys.Contains(key))
            {
                error = $"attribute '{key}' is not an allowlisted travel-profile cost column for this dataset.";
                return false;
            }

            if (value is null || value.Length > MaxAttributeValueLength)
            {
                error = $"attribute '{key}' requires a non-null value of {MaxAttributeValueLength} characters or fewer.";
                return false;
            }

            if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var numeric)
                || !double.IsFinite(numeric))
            {
                error = $"attribute '{key}' must be a finite numeric value (cost columns are numeric).";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Structurally validates that a GeoJSON payload is a well-formed <c>LineString</c> or
    /// <c>MultiLineString</c> with finite coordinates. This is a lightweight, dependency-free
    /// shape/finiteness check (JSON numbers can never encode NaN/Infinity, so this mainly
    /// guards structure and coordinate arity); the deeper geometry-validity and SRID checks
    /// happen in PostGIS via <c>ST_GeomFromGeoJSON</c> plus the table's <c>CHECK</c>
    /// constraints when the batch is persisted.
    /// </summary>
    internal static bool TryValidateLinearGeoJson(string? geometryGeoJson, out string error)
    {
        if (string.IsNullOrWhiteSpace(geometryGeoJson))
        {
            error = "geometry is required.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(geometryGeoJson);
        }
        catch (JsonException)
        {
            error = "geometry is not valid JSON.";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                error = "geometry must be a GeoJSON object with a 'type' property.";
                return false;
            }

            var type = typeElement.GetString();
            if (!root.TryGetProperty("coordinates", out var coordinates) || coordinates.ValueKind != JsonValueKind.Array)
            {
                error = "geometry must have a 'coordinates' array.";
                return false;
            }

            switch (type)
            {
                case "LineString":
                    return TryValidateLineStringCoordinates(coordinates, out error);
                case "MultiLineString":
                    if (coordinates.GetArrayLength() == 0)
                    {
                        error = "MultiLineString geometry must contain at least one line.";
                        return false;
                    }

                    foreach (var line in coordinates.EnumerateArray())
                    {
                        if (line.ValueKind != JsonValueKind.Array)
                        {
                            error = "MultiLineString contains an invalid line.";
                            return false;
                        }

                        if (!TryValidateLineStringCoordinates(line, out error))
                        {
                            return false;
                        }
                    }

                    error = string.Empty;
                    return true;
                default:
                    error = $"geometry type '{type}' is not supported; only LineString and MultiLineString are allowed for edges.";
                    return false;
            }
        }
    }

    private static bool TryValidateLineStringCoordinates(JsonElement coordinates, out string error)
    {
        if (coordinates.ValueKind != JsonValueKind.Array || coordinates.GetArrayLength() < 2)
        {
            error = "a line must have at least two positions.";
            return false;
        }

        foreach (var position in coordinates.EnumerateArray())
        {
            if (position.ValueKind != JsonValueKind.Array || position.GetArrayLength() < 2)
            {
                error = "each position must be an array of at least two numbers.";
                return false;
            }

            // codeql[cs/linq/missed-where] -- predicate binds state or awaits; retain imperative control flow.
            foreach (var ordinate in position.EnumerateArray())
            {
                if (ordinate.ValueKind != JsonValueKind.Number || !ordinate.TryGetDouble(out var value) || !double.IsFinite(value))
                {
                    error = "each coordinate ordinate must be a finite number.";
                    return false;
                }
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateNoDuplicateIds(
        IEnumerable<string> adds,
        IEnumerable<string> updates,
        IEnumerable<string> deletes,
        string role,
        out string error)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in (adds.Concat(updates).Concat(deletes)).Where(id => !string.IsNullOrEmpty(id) && !seen.Add(id)))
        {
            error = $"Duplicate {role} id '{id}' within the same batch.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
