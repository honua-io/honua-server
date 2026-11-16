// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Services;

// URL handling shared by the migration scanners, importers and evidence packs: nothing that
// leaves through a log or an evidence pack keeps credentials, a query or a fragment.
internal static class MigrationSourceUrls
{
    internal static MigrationSourceIdentity RedactSource(MigrationSourceIdentity source)
    {
        return new MigrationSourceIdentity
        {
            DisplayName = source.DisplayName,
            BaseUrl = RedactUrl(source.BaseUrl),
            Product = source.Product,
            Version = source.Version,
            Build = source.Build,
            ServiceType = source.ServiceType
        };
    }

    internal static string RedactUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return baseUrl;
        }

        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? ToSafeSourceUrl(uri) : baseUrl;
    }

    internal static string ToSafeSourceUrl(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.AbsoluteUri;
    }

    internal static Uri EnsureDirectoryBase(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };

        if (builder.Path.Length == 0 || builder.Path[^1] != '/')
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    internal static string BuildQuery(string existingQuery, IReadOnlyDictionary<string, string?> values)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        var trimmed = existingQuery.TrimStart('?');
        if (trimmed.Length > 0)
        {
            foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = part.IndexOf('=', StringComparison.Ordinal);
                var key = separator < 0 ? Uri.UnescapeDataString(part) : Uri.UnescapeDataString(part[..separator]);
                if (values.Keys.Any(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var value = separator < 0 ? string.Empty : Uri.UnescapeDataString(part[(separator + 1)..]);
                pairs.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        foreach (var (key, value) in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                pairs.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        return string.Join("&", pairs.Select(static pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }
}
