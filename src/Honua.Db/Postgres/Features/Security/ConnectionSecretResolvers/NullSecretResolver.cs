// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;

namespace Honua.Db.Postgres.Features.Security.ConnectionSecretResolvers;

/// <summary>
/// Null implementation of secret resolver that doesn't support any external secret providers.
/// </summary>
/// <remarks>
/// This implementation is used as a fallback when no external secret management
/// systems are configured. It will always fail to resolve secrets, encouraging
/// the use of encrypted storage instead.
/// </remarks>
internal sealed class NullSecretResolver : IConnectionSecretResolver
{
    /// <inheritdoc />
    public string ProviderName => "null";

    /// <inheritdoc />
    public Task<string?> ResolveSecretAsync(string secretKey, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            $"External secret resolution is not configured. Secret reference '{secretKey}' cannot be resolved. " +
            "Either configure a secret management provider or use encrypted credential storage.");
    }

    /// <inheritdoc />
    public bool CanResolve(string secretKey)
    {
        // Always return false since this resolver doesn't support any providers
        return false;
    }

    /// <inheritdoc />
    public Task<string> ResolveConnectionStringAsync(string connectionStringTemplate, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            $"External secret resolution is not configured. Connection string template '{connectionStringTemplate}' cannot be resolved. " +
            "Either configure a secret management provider or use encrypted credential storage.");
    }
}
