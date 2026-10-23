// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Security.Abstractions;

/// <summary>
/// Provider-owned inspection of a resolved secure connection string before any network access.
/// Implementations parse with their driver's builder and reject unsupported or unsafe settings.
/// </summary>
public interface ISecureConnectionStringPolicy
{
    /// <summary>Validates settings and returns every destination and the effective TLS requirement.</summary>
    ConnectionStringSecurity InspectConnectionString(string connectionString);
}

/// <summary>Applies shared connection policy to resolved strings before a connection or draft probe opens.</summary>
public interface IResolvedConnectionStringValidator
{
    /// <summary>Validates provider settings, transport security and every resolved destination.</summary>
    Task ValidateConnectionStringAsync(DataConnection connection, string connectionString, CancellationToken cancellationToken = default);
}

/// <summary>Validated destinations and transport security for a provider connection string.</summary>
/// <param name="Hosts">Every host the provider may connect to.</param>
/// <param name="RequiresTls">Whether the string prevents plaintext fallback.</param>
public sealed record ConnectionStringSecurity(IReadOnlyList<string> Hosts, bool RequiresTls);

/// <summary>Shared keyword enforcement after provider-specific parsing and canonicalization.</summary>
public static class ConnectionStringKeywordPolicy
{
    /// <summary>Rejects explicitly supplied settings outside the provider's allowlist.</summary>
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

    /// <summary>Splits a comma-separated host list, removing brackets around IPv6 literals.</summary>
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
