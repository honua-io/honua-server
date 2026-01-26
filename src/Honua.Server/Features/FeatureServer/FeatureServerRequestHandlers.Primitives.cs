// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private static bool TryParseRequiredIntValue(
        IReadOnlyDictionary<string, StringValues> values,
        string key,
        out int result,
        out string? error)
    {
        error = null;
        result = default;

        if (!TryGetValue(values, key, out var raw) || StringValues.IsNullOrEmpty(raw))
        {
            error = $"{key} parameter is required";
            return false;
        }

        var value = raw.ToString();
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
        {
            error = $"{key} must be an integer";
            return false;
        }

        return true;
    }

    private static bool TryParseIntValue(
        IReadOnlyDictionary<string, StringValues> values,
        string key,
        out int? result,
        out string? error)
    {
        error = null;
        result = null;

        if (!TryGetValue(values, key, out var raw) || StringValues.IsNullOrEmpty(raw))
        {
            return true;
        }

        var value = raw.ToString();
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"{key} must be an integer";
            return false;
        }

        result = parsed;
        return true;
    }

    private static bool TryParseDoubleValue(
        IReadOnlyDictionary<string, StringValues> values,
        string key,
        out double? result,
        out string? error)
    {
        error = null;
        result = null;

        if (!TryGetValue(values, key, out var raw) || StringValues.IsNullOrEmpty(raw))
        {
            return true;
        }

        var value = raw.ToString();
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"{key} must be a number";
            return false;
        }

        result = parsed;
        return true;
    }

    private static bool TryParseBoolValue(
        IReadOnlyDictionary<string, StringValues> values,
        string key,
        bool defaultValue,
        out bool result,
        out string? error)
    {
        error = null;
        result = defaultValue;

        if (!TryGetValue(values, key, out var raw) || StringValues.IsNullOrEmpty(raw))
        {
            return true;
        }

        var value = raw.ToString();
        if (bool.TryParse(value, out var parsed))
        {
            result = parsed;
            return true;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            result = numeric != 0;
            return true;
        }

        error = $"{key} must be a boolean value";
        return false;
    }

    private static bool TryParseLongArray(
        IReadOnlyDictionary<string, StringValues> values,
        string key,
        out long[]? result,
        out string? error)
    {
        result = null;
        error = null;

        if (!TryGetValue(values, key, out var raw) || StringValues.IsNullOrEmpty(raw))
        {
            return true;
        }

        if (!TryParseLongArray(raw, key, out result, out error))
        {
            return false;
        }

        return true;
    }

    private static bool TryParseRequiredLongArray(
        IReadOnlyDictionary<string, StringValues> values,
        string key,
        out long[] result,
        out string? error)
    {
        error = null;
        result = [];

        if (!TryGetValue(values, key, out var raw) || StringValues.IsNullOrEmpty(raw))
        {
            error = $"{key} parameter is required";
            return false;
        }

        if (!TryParseLongArray(raw, key, out var parsed, out error))
        {
            error ??= $"Invalid {key} values";
            return false;
        }

        result = parsed ?? [];
        if (result.Length == 0)
        {
            error = $"{key} parameter is required";
            return false;
        }

        return true;
    }

    private static bool TryParseLongArray(
        StringValues raw,
        string key,
        out long[]? result,
        out string? error)
    {
        error = null;
        result = null;

        var tokens = new List<string>();
        foreach (var value in raw)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            tokens.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        if (tokens.Count == 0)
        {
            return true;
        }

        var ids = new List<long>(tokens.Count);
        foreach (var token in tokens)
        {
            if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                error = string.Equals(key, "objectIds", StringComparison.OrdinalIgnoreCase)
                    ? "Invalid objectId value. objectIds parameter must contain only numeric values."
                    : $"{key} parameter must contain only numeric values";
                return false;
            }

            ids.Add(id);
        }

        result = ids.ToArray();
        return true;
    }
}
