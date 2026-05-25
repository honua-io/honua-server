// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.TemporalHistory.Domain;

namespace Honua.Postgres.Features.TemporalHistory;

/// <summary>
/// Reflection-free JSON helpers for reading JSONB attribute bags and computing field-level changes.
/// Uses <see cref="JsonDocument"/> parsing (AOT/trimming safe) rather than reflection-based serialization.
/// </summary>
internal static class TemporalJson
{
    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyAttributes
        = new Dictionary<string, JsonElement>();

    /// <summary>
    /// Parses a JSONB object string into a detached attribute dictionary. Returns an empty dictionary
    /// for null/blank/non-object input.
    /// </summary>
    /// <param name="json">JSONB object text.</param>
    /// <returns>Attribute dictionary.</returns>
    public static IReadOnlyDictionary<string, JsonElement> ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return EmptyAttributes;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return EmptyAttributes;
            }

            var dictionary = new Dictionary<string, JsonElement>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                dictionary[property.Name] = property.Value.Clone();
            }

            return dictionary;
        }
        catch (JsonException)
        {
            return EmptyAttributes;
        }
    }

    /// <summary>
    /// Parses arbitrary JSON text into a detached <see cref="JsonElement"/>, or null for null/blank input.
    /// </summary>
    /// <param name="json">JSON text (for example a GeoJSON geometry).</param>
    /// <returns>The parsed element, or null.</returns>
    public static JsonElement? ParseElement(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Computes field-level changes between two attribute states, ordered by field name.
    /// </summary>
    /// <param name="before">Prior attribute state.</param>
    /// <param name="after">New attribute state.</param>
    /// <returns>The set of changed fields with before/after values.</returns>
    public static IReadOnlyList<TemporalFieldChange> FieldChanges(
        IReadOnlyDictionary<string, JsonElement> before,
        IReadOnlyDictionary<string, JsonElement> after)
    {
        if (before.Count == 0 && after.Count == 0)
        {
            return [];
        }

        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in before.Keys)
        {
            keys.Add(key);
        }

        foreach (var key in after.Keys)
        {
            keys.Add(key);
        }

        var changes = new List<TemporalFieldChange>();
        foreach (var key in keys)
        {
            var hasBefore = before.TryGetValue(key, out var beforeValue);
            var hasAfter = after.TryGetValue(key, out var afterValue);

            if (hasBefore && hasAfter && JsonEquals(beforeValue, afterValue))
            {
                continue;
            }

            changes.Add(new TemporalFieldChange
            {
                Field = key,
                Before = hasBefore ? beforeValue : null,
                After = hasAfter ? afterValue : null
            });
        }

        return changes;
    }

    private static bool JsonEquals(JsonElement left, JsonElement right)
        => string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
}
