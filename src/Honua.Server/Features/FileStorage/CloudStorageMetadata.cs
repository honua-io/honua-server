// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;

namespace Honua.Server.Features.FileStorage;

internal static class CloudStorageMetadata
{
    internal const string FileNameKey = "honua-file-name";
    internal const string ExpiresAtKey = "honua-expires-at";

    internal static Dictionary<string, string> BuildMetadata(
        ImmutableDictionary<string, string> metadata,
        string fileName,
        DateTimeOffset? expiresAt)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in metadata)
        {
            result[pair.Key] = pair.Value;
        }

        result[FileNameKey] = fileName;
        if (expiresAt.HasValue)
        {
            result[ExpiresAtKey] = expiresAt.Value.ToString("O", CultureInfo.InvariantCulture);
        }

        return result;
    }

    internal static ImmutableDictionary<string, string> ExtractUserMetadata(IDictionary<string, string>? metadata)
    {
        if (metadata == null || metadata.Count == 0)
        {
            return ImmutableDictionary<string, string>.Empty;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, string>();
        foreach (var pair in metadata)
        {
            if (IsReservedKey(pair.Key))
            {
                continue;
            }

            builder[pair.Key] = pair.Value;
        }

        return builder.ToImmutable();
    }

    internal static string? GetFileName(IDictionary<string, string>? metadata)
    {
        return TryGetValueIgnoreCase(metadata, FileNameKey, out var value)
            ? value
            : null;
    }

    internal static DateTimeOffset? GetExpiresAt(IDictionary<string, string>? metadata)
    {
        if (!TryGetValueIgnoreCase(metadata, ExpiresAtKey, out var value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    private static bool IsReservedKey(string key)
    {
        return string.Equals(key, FileNameKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, ExpiresAtKey, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetValueIgnoreCase(IDictionary<string, string>? metadata, string key, out string? value)
    {
        if (metadata == null)
        {
            value = null;
            return false;
        }

        if (metadata.TryGetValue(key, out var directValue))
        {
            value = directValue;
            return true;
        }

        foreach (var pair in metadata)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }
}
