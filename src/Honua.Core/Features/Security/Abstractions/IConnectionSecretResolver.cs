// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Abstractions;

/// <summary>
/// Service for resolving connection secrets from external sources.
/// </summary>
public interface IConnectionSecretResolver
{
    /// <summary>
    /// Gets the provider name for this resolver.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Resolves a secret by key.
    /// </summary>
    /// <param name="secretKey">The key to resolve</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The resolved secret value</returns>
    Task<string?> ResolveSecretAsync(string secretKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if this resolver can handle the given secret key.
    /// </summary>
    /// <param name="secretKey">The secret key to check</param>
    /// <returns>True if this resolver can handle the key</returns>
    bool CanResolve(string secretKey);

    /// <summary>
    /// Checks asynchronously whether a secret reference can be resolved.
    /// </summary>
    /// <param name="secretKey">The secret reference to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the reference can be resolved.</returns>
    Task<bool> CanResolveSecretAsync(string secretKey, CancellationToken cancellationToken = default)
        => Task.FromResult(CanResolve(secretKey));

    /// <summary>
    /// Resolves a connection string with secret substitution.
    /// </summary>
    /// <param name="connectionStringTemplate">Connection string template with secret placeholders</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The resolved connection string</returns>
    Task<string> ResolveConnectionStringAsync(string connectionStringTemplate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the provider identifiers supported by this resolver.
    /// </summary>
    /// <returns>The supported provider identifiers.</returns>
    string[] GetSupportedProviders() => [ProviderName];
}
