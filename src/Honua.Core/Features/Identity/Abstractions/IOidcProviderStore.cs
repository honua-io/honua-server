// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Identity.Domain;

namespace Honua.Core.Features.Identity.Abstractions;

/// <summary>
/// Store for OIDC provider configurations.
/// </summary>
public interface IOidcProviderStore
{
    /// <summary>
    /// Lists all configured OIDC providers.
    /// </summary>
    Task<IReadOnlyList<OidcProviderConfiguration>> ListProvidersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific provider by ID.
    /// </summary>
    Task<OidcProviderConfiguration?> GetProviderAsync(Guid providerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new provider configuration.
    /// </summary>
    Task<OidcProviderConfiguration> CreateProviderAsync(OidcProviderConfiguration provider, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically creates a provider only when the store currently contains fewer
    /// than <paramref name="maximumProviderCount"/> configurations.
    /// </summary>
    /// <remarks>
    /// The default implementation fails closed so existing third-party stores remain
    /// binary compatible without silently bypassing the provider-count entitlement.
    /// Stores that support provider creation must override this operation atomically.
    /// </remarks>
    Task<OidcProviderConfiguration?> CreateProviderIfBelowLimitAsync(
        OidcProviderConfiguration provider,
        int maximumProviderCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumProviderCount);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<OidcProviderConfiguration?>(null);
    }

    /// <summary>
    /// Updates an existing provider configuration.
    /// </summary>
    Task<OidcProviderConfiguration?> UpdateProviderAsync(OidcProviderConfiguration provider, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a provider configuration.
    /// </summary>
    Task<bool> DeleteProviderAsync(Guid providerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tests connectivity to a provider.
    /// </summary>
    Task<OidcProviderTestResult> TestProviderAsync(Guid providerId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of testing an OIDC provider connection.
/// </summary>
public sealed class OidcProviderTestResult
{
    /// <summary>
    /// Whether the provider is reachable and token exchange succeeds.
    /// </summary>
    public required bool IsReachable { get; init; }

    /// <summary>
    /// Human-readable status message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// When the test was performed.
    /// </summary>
    public DateTimeOffset TestedAt { get; init; } = DateTimeOffset.UtcNow;
}
