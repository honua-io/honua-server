// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;

namespace Honua.Db.Postgres.Features.Security.ConnectionSecretResolvers;

internal sealed class NullSecretResolver : IConnectionSecretResolver
{
    public string ProviderName => "null";

    public Task<string?> ResolveSecretAsync(string secretKey, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            $"External secret resolution is not configured. Secret reference '{secretKey}' cannot be resolved. " +
            "Either configure a secret management provider or use encrypted credential storage.");
    }

    public Task<string?> ResolveSecretFreshAsync(string secretKey, CancellationToken cancellationToken = default)
        => ResolveSecretAsync(secretKey, cancellationToken);

    public bool CanResolve(string secretKey)
    {
        // Always return false since this resolver doesn't support any providers
        return false;
    }

    public Task<string> ResolveConnectionStringAsync(string connectionStringTemplate, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            $"External secret resolution is not configured. Connection string template '{connectionStringTemplate}' cannot be resolved. " +
            "Either configure a secret management provider or use encrypted credential storage.");
    }
}
