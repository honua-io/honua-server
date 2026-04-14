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
    /// Retrieves a secret value or returns the supplied default when the secret cannot be resolved.
    /// </summary>
    /// <param name="secretKey">The key identifying the secret.</param>
    /// <param name="defaultValue">The fallback value to use when the secret cannot be resolved.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved secret value, or the supplied default.</returns>
    Task<string?> GetSecretOrDefaultAsync(string secretKey, string? defaultValue = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if this provider can resolve the given secret key.
    /// </summary>
    /// <param name="secretKey">The secret key to check</param>
    /// <returns>True if this provider can handle the key</returns>
    bool CanProvideSecret(string secretKey);

    /// <summary>
    /// Determines whether the given secret key can be resolved successfully.
    /// </summary>
    /// <param name="secretKey">The secret key to test.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the secret can be resolved.</returns>
    Task<bool> CanResolveSecretAsync(string secretKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the provider name.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Gets the set of supported secret provider prefixes.
    /// </summary>
    /// <returns>The supported provider prefixes.</returns>
    string[] GetSupportedProviders();

    /// <summary>
    /// Determines whether the supplied value is a secret reference understood by this provider.
    /// </summary>
    /// <param name="value">The value to inspect.</param>
    /// <returns>True when the value is a supported secret reference.</returns>
    bool IsSecretReference(string? value);
}
