// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// API response model for an OIDC provider (excludes secrets).
/// </summary>
public sealed class OidcProviderResponse
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public required Guid ProviderId { get; init; }

    /// <summary>
    /// Human-readable name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Provider type (AzureAd, Okta, Auth0, Generic).
    /// </summary>
    public required string ProviderType { get; init; }

    /// <summary>
    /// OIDC authority URL.
    /// </summary>
    public required string Authority { get; init; }

    /// <summary>
    /// Client ID.
    /// </summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// Whether the provider is enabled.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Whether the provider is currently healthy.
    /// </summary>
    public bool IsHealthy { get; init; }

    /// <summary>
    /// When the provider was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When the provider was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Last health check timestamp.
    /// </summary>
    public DateTimeOffset? LastHealthCheck { get; init; }
}

/// <summary>
/// Request to create a new OIDC provider.
/// </summary>
public sealed class CreateOidcProviderRequest
{
    /// <summary>
    /// Human-readable name.
    /// </summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public required string Name { get; init; }

    /// <summary>
    /// Provider type.
    /// </summary>
    [Required]
    public required string ProviderType { get; init; }

    /// <summary>
    /// OIDC authority URL.
    /// </summary>
    [Required]
    [Url]
    public required string Authority { get; init; }

    /// <summary>
    /// Client ID.
    /// </summary>
    [Required]
    public required string ClientId { get; init; }

    /// <summary>
    /// Client secret.
    /// </summary>
    public string? ClientSecret { get; init; }

    /// <summary>
    /// Whether the provider is enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Request to update an existing OIDC provider.
/// </summary>
public sealed class UpdateOidcProviderRequest
{
    /// <summary>
    /// Updated name.
    /// </summary>
    [StringLength(200, MinimumLength = 1)]
    public string? Name { get; init; }

    /// <summary>
    /// Updated authority URL.
    /// </summary>
    [Url]
    public string? Authority { get; init; }

    /// <summary>
    /// Updated client ID.
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// Updated client secret.
    /// </summary>
    public string? ClientSecret { get; init; }

    /// <summary>
    /// Updated enabled state.
    /// </summary>
    public bool? Enabled { get; init; }
}

/// <summary>
/// API response model for an OIDC provider connectivity test.
/// </summary>
public sealed class OidcProviderTestResponse
{
    /// <summary>
    /// Provider ID that was tested.
    /// </summary>
    public required Guid ProviderId { get; init; }

    /// <summary>
    /// Whether the provider is reachable.
    /// </summary>
    public required bool IsReachable { get; init; }

    /// <summary>
    /// Status message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// When the test was performed.
    /// </summary>
    public DateTimeOffset TestedAt { get; init; }
}
