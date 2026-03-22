// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Identity.Domain;

/// <summary>
/// Represents a configured OIDC identity provider (Entra ID, Okta, Auth0, etc.).
/// </summary>
public sealed class OidcProviderConfiguration
{
    /// <summary>
    /// Unique identifier for this provider configuration.
    /// </summary>
    public Guid ProviderId { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Human-readable name (e.g., "Corporate Entra ID", "Okta Production").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Provider type (e.g., "AzureAd", "Okta", "Auth0", "Generic").
    /// </summary>
    public required string ProviderType { get; init; }

    /// <summary>
    /// OIDC authority URL (issuer).
    /// </summary>
    public required string Authority { get; init; }

    /// <summary>
    /// Client ID for this provider.
    /// </summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// Client secret (stored encrypted, never returned in API responses).
    /// </summary>
    public string? ClientSecret { get; init; }

    /// <summary>
    /// Whether this provider is currently enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Whether the provider is reachable based on last health check.
    /// </summary>
    public bool IsHealthy { get; init; }

    /// <summary>
    /// When the provider configuration was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the provider configuration was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Last health check timestamp.
    /// </summary>
    public DateTimeOffset? LastHealthCheck { get; init; }
}
