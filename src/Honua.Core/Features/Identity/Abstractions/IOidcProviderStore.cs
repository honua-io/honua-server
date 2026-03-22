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
