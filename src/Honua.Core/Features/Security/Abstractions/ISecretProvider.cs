// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Abstractions;

/// <summary>
/// Provides secure access to secrets from various secret stores.
/// </summary>
public interface ISecretProvider
{
    /// <summary>
    /// Retrieves a secret value by key.
    /// </summary>
    /// <param name="secretKey">The key identifying the secret</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The secret value, or null if not found</returns>
    Task<string?> GetSecretAsync(string secretKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the provider can resolve the given secret key.
    /// </summary>
    /// <param name="secretKey">The secret key to check</param>
    /// <returns>True if this provider can handle the key</returns>
    bool CanProvideSecret(string secretKey);

    /// <summary>
    /// Gets the provider name.
    /// </summary>
    string ProviderName { get; }
}