// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;

namespace Honua.Server.Features.Infrastructure.Helpers;

internal static class ConnectionStringResolutionHelper
{
    public static async Task<string?> ResolveDefaultConnectionStringAsync(
        IConfiguration configuration,
        IConnectionSecretResolver? secretResolver,
        CancellationToken cancellationToken = default)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        connectionString = SecretReferenceResolver.ResolveEnvironmentReference(
            connectionString,
            "ConnectionStrings:DefaultConnection");

        if (string.IsNullOrWhiteSpace(connectionString) || secretResolver is null)
        {
            return connectionString;
        }

        if (!await secretResolver.CanResolveSecretAsync(connectionString, cancellationToken).ConfigureAwait(false))
        {
            return connectionString;
        }

        return await secretResolver.ResolveConnectionStringAsync(connectionString, cancellationToken).ConfigureAwait(false);
    }
}
