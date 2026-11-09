// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Security.Abstractions;

public interface ISecureConnectionStringPolicy
{
    ConnectionStringSecurity InspectConnectionString(string connectionString);
}

public interface IResolvedConnectionStringValidator
{
    Task ValidateConnectionStringAsync(DataConnection connection, string connectionString, CancellationToken cancellationToken = default);
}

public sealed record ConnectionStringSecurity(IReadOnlyList<string> Hosts, bool RequiresTls);

public static class ConnectionStringKeywordPolicy
{
    public static void EnsureAllowed(DbConnectionStringBuilder typedBuilder, params string[] allowedKeywords)
    {
        ArgumentNullException.ThrowIfNull(typedBuilder);
        var allowed = allowedKeywords.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        // Some typed builders enumerate all supported keys, including unset defaults.
        // Reparse their serialized string to inspect only explicitly supplied settings.
        var supplied = new DbConnectionStringBuilder { ConnectionString = typedBuilder.ConnectionString };
        foreach (string keyword in supplied.Keys)
        {
            if (!allowed.Contains(Normalize(keyword)))
            {
                throw new ArgumentException($"Connection string keyword '{keyword}' is not permitted.");
            }
        }
    }

    public static IReadOnlyList<string> SplitHosts(string hosts)
    {
        var values = hosts.Split(',', StringSplitOptions.TrimEntries)
            .Select(host => host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host)
            .ToArray();
        if (values.Length == 0 || values.Any(host => Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Contains('/') || host.Contains('\\')))
        {
            throw new ArgumentException("Connection string must name a network host.");
        }
        return values;
    }

    private static string Normalize(string keyword)
        => keyword.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}
