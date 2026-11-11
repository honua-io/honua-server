// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Core.Features.Security;

public static class CatalogConnectionDiscovery
{
    public static async Task<CatalogConnectionMatch> FindUniqueAsync(
        IServiceProvider services,
        string? connectionString,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolver = services.GetService<ISecureConnectionResolver>();
            var registry = services.GetService<ISecureConnectionRegistry>();
            var matcher = services.GetService<ICatalogConnectionIdentityMatcher>();
            if (resolver is null || registry is null || matcher is null || string.IsNullOrWhiteSpace(connectionString))
            {
                return default;
            }

            return await new RegistrationScanner(resolver, registry, matcher)
                .FindUniqueAsync(connectionString, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // Discovery is optional; callers retain their import receipt without a connection hint.
            return default;
        }
    }

    private sealed class RegistrationScanner(
        ISecureConnectionResolver resolver,
        ISecureConnectionRegistry registry,
        ICatalogConnectionIdentityMatcher matcher)
    {
        public async Task<CatalogConnectionMatch> FindUniqueAsync(string connectionString, CancellationToken cancellationToken)
        {
            DataConnection? match = null;
            var connections = await registry.GetActiveConnectionsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var connection in connections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!connection.IsActive || string.IsNullOrWhiteSpace(connection.Name) ||
                    !await MatchesAsync(connection, connectionString, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                if (match is not null)
                {
                    return new CatalogConnectionMatch(null, true);
                }

                match = connection;
            }

            return new CatalogConnectionMatch(match, false);
        }

        private async Task<bool> MatchesAsync(DataConnection connection, string connectionString, CancellationToken token)
        {
            try
            {
                var registered = await resolver.ResolveConnectionStringAsync(connection.Name, token).ConfigureAwait(false);
                return matcher.IsMatch(connection.NormalizedProvider, connectionString, registered);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                // An unavailable secret must not hide another matching registration.
                return false;
            }
        }
    }
}

public readonly record struct CatalogConnectionMatch(DataConnection? Connection, bool Ambiguous);
