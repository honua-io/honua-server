// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Helpers;

/// <summary>
/// Normalizes feature attribute values for protocol wire formats that represent booleans as small integers.
/// </summary>
internal static class FeatureAttributeValueNormalizer
{
    /// <summary>
    /// Converts booleans to small-integer values while leaving other types unchanged.
    /// </summary>
    public static object? Normalize(object? value)
        => value switch
        {
            bool b => b ? 1 : 0,
            _ => value
        };

    /// <summary>
    /// Creates a normalized copy of an attribute dictionary.
    /// </summary>
    public static Dictionary<string, object?> NormalizeAttributes(IReadOnlyDictionary<string, object?> attributes)
    {
        var normalized = new Dictionary<string, object?>(attributes.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in attributes)
        {
            normalized[kvp.Key] = Normalize(kvp.Value);
        }

        return normalized;
    }
}
