// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;

namespace Honua.Infrastructure.Helpers;

internal static class ConnectionStringResolutionHelper
{
    public static Task<string?> ResolveDefaultConnectionStringAsync(
        IConfiguration configuration,
        IConnectionSecretResolver? secretResolver,
        CancellationToken cancellationToken = default)
        => ResolveConnectionStringValueAsync(
            configuration.GetConnectionString("DefaultConnection"),
            "ConnectionStrings:DefaultConnection",
            secretResolver,
            cancellationToken);

    // An env: reference is substituted before the secret resolver, and a plain value passes through when no resolver claims it.
    public static async Task<string?> ResolveConnectionStringValueAsync(
        string? connectionString,
        string settingName,
        IConnectionSecretResolver? secretResolver,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        connectionString = SecretReferenceResolver.ResolveEnvironmentReference(connectionString, settingName);

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
