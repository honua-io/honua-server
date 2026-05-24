// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Shared query-string parsers used by the Console Operate observability
/// endpoints (#1168). Keeps the per-endpoint handlers focused on assembling
/// filters rather than on reading strings.
/// </summary>
internal static class QueryFilterParsers
{
    public static string? GetString(IQueryCollection query, string key)
    {
        if (!query.TryGetValue(key, out var values))
        {
            return null;
        }

        var value = values.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static bool TryParseInt(IQueryCollection query, string key, out int? value, out string error)
    {
        value = null;
        error = string.Empty;
        if (!query.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"'{key}' must be an integer.";
            return false;
        }

        value = parsed;
        return true;
    }

    public static bool TryParseLong(IQueryCollection query, string key, out long? value, out string error)
    {
        value = null;
        error = string.Empty;
        if (!query.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!long.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"'{key}' must be a 64-bit integer.";
            return false;
        }

        value = parsed;
        return true;
    }

    public static bool TryParseDateTimeOffset(IQueryCollection query, string key, out DateTimeOffset? value, out string error)
    {
        value = null;
        error = string.Empty;
        if (!query.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(raw.ToString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            error = $"'{key}' must be an ISO-8601 timestamp.";
            return false;
        }

        value = parsed;
        return true;
    }

    public static bool TryParseEnumList<TEnum>(
        IQueryCollection query,
        string key,
        out IReadOnlyList<TEnum>? values,
        out string error)
        where TEnum : struct, Enum
    {
        values = null;
        error = string.Empty;

        if (!query.TryGetValue(key, out var raw))
        {
            return true;
        }

        var collected = new List<TEnum>();
        foreach (var entry in raw)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            foreach (var part in entry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!TryParseDefinedEnum<TEnum>(part, out var parsed))
                {
                    error = $"'{key}' contains unsupported value '{part}'.";
                    return false;
                }

                collected.Add(parsed);
            }
        }

        values = collected.Count > 0 ? collected : null;
        return true;
    }

    /// <summary>
    /// Parses an enum value while rejecting numeric strings that resolve to
    /// undefined members. This avoids leaking malformed admin input into the
    /// stores, where unsupported numeric values would surface as 500s.
    /// </summary>
    public static bool TryParseDefinedEnum<TEnum>(string value, out TEnum parsed)
        where TEnum : struct, Enum
    {
        if (!Enum.TryParse<TEnum>(value, ignoreCase: true, out parsed))
        {
            return false;
        }

        // Enum.TryParse accepts arbitrary numeric strings (e.g. "99"); guard against
        // names that are purely digits/whitespace and confirm the resulting member exists.
        if (!Enum.IsDefined(parsed))
        {
            return false;
        }

        return true;
    }
}
