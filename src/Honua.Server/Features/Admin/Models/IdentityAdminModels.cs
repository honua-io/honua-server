// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Response model for identity provider configuration.
/// </summary>
public sealed class IdentityProvidersResponse
{
    /// <summary>
    /// Whether OIDC authentication is globally enabled.
    /// </summary>
    public required bool Enabled { get; init; }

    /// <summary>
    /// List of configured identity providers.
    /// </summary>
    public required IdentityProviderStatus[] Providers { get; init; }
}

/// <summary>
/// Status of a single identity provider.
/// </summary>
public sealed class IdentityProviderStatus
{
    /// <summary>
    /// Provider type identifier (AzureAd, Google, Generic).
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Whether this provider is enabled.
    /// </summary>
    public required bool Enabled { get; init; }

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// OIDC authority URL.
    /// </summary>
    public string? Authority { get; init; }

    /// <summary>
    /// OAuth callback path.
    /// </summary>
    public string? CallbackPath { get; init; }

    /// <summary>
    /// Requested scopes.
    /// </summary>
    public string[]? Scopes { get; init; }

    /// <summary>
    /// Whether the provider configuration is valid.
    /// </summary>
    public required bool IsConfigurationValid { get; init; }
}

/// <summary>
/// Result of an identity provider connectivity test.
/// </summary>
public sealed class IdentityProviderTestResult
{
    /// <summary>
    /// Provider type that was tested.
    /// </summary>
    public required string ProviderType { get; init; }

    /// <summary>
    /// Whether the provider's OIDC discovery endpoint is reachable.
    /// </summary>
    public required bool IsReachable { get; init; }

    /// <summary>
    /// Response time in milliseconds for the discovery request.
    /// </summary>
    public double? ResponseTimeMs { get; init; }

    /// <summary>
    /// Full URL of the OIDC discovery document.
    /// </summary>
    public string? DiscoveryUrl { get; init; }

    /// <summary>
    /// Error message if the test failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Discovered token issuer.
    /// </summary>
    public string? Issuer { get; init; }
}
