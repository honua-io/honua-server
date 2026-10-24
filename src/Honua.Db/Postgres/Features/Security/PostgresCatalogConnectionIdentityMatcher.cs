// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security.Abstractions;
using Npgsql;

namespace Honua.Db.Postgres.Features.Security;

/// <summary>
/// Uses Npgsql's connection-string parser for aliases, quoting and effective
/// defaults, then compares only database identity. Session and transport
/// settings such as Application Name and SSL Mode are not database identity.
/// </summary>
internal sealed class PostgresCatalogConnectionIdentityMatcher : ICatalogConnectionIdentityMatcher
{
    public bool IsMatch(string provider, string catalogConnectionString, string registeredConnectionString)
    {
        if (!string.Equals(DataProviderNames.Normalize(provider), DataProviderNames.Postgis, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(catalogConnectionString)
            || string.IsNullOrWhiteSpace(registeredConnectionString))
        {
            return false;
        }

        try
        {
            var catalog = new NpgsqlConnectionStringBuilder(catalogConnectionString);
            var registered = new NpgsqlConnectionStringBuilder(registeredConnectionString);

            // A host list has failover semantics; a string comparison cannot
            // establish which target either connection actually used. Unix
            // socket paths are case-sensitive and also need a separate rule.
            if (!IsSingleNetworkHost(catalog.Host) || !IsSingleNetworkHost(registered.Host)
                || string.IsNullOrWhiteSpace(catalog.Database)
                || string.IsNullOrWhiteSpace(registered.Database)
                || string.IsNullOrWhiteSpace(catalog.Username)
                || string.IsNullOrWhiteSpace(registered.Username))
            {
                return false;
            }

            return string.Equals(catalog.Host, registered.Host, StringComparison.OrdinalIgnoreCase)
                && catalog.Port == registered.Port
                && string.Equals(catalog.Database, registered.Database, StringComparison.Ordinal)
                && string.Equals(catalog.Username, registered.Username, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Malformed/unsupported registration data cannot justify a hint.
            return false;
        }
    }

    private static bool IsSingleNetworkHost(string? host)
        => !string.IsNullOrWhiteSpace(host) && !host.Contains(',') && !host.Contains('/');
}
