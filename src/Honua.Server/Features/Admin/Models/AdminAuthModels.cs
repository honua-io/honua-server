// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Response model for the admin auth bootstrap endpoint.
/// Exposes only minimal provider-selection metadata; no OIDC connection details are included.
/// </summary>
public sealed class AdminAuthConfigResponse
{
    /// <summary>
    /// Gets or sets whether OIDC authentication is enabled.
    /// </summary>
    public bool OidcEnabled { get; set; }

    /// <summary>
    /// Gets or sets the configured OIDC providers (client-safe metadata only).
    /// </summary>
    public List<AdminAuthProviderInfo> Providers { get; set; } = [];
}

/// <summary>
/// Minimal metadata for a single OIDC provider.
/// </summary>
public sealed class AdminAuthProviderInfo
{
    /// <summary>
    /// Gets or sets the provider key used for selection (e.g., "azuread", "google", "oidc").
    /// </summary>
    public required string Key { get; set; }

    /// <summary>
    /// Gets or sets the display name for the provider shown in the UI.
    /// </summary>
    public required string DisplayName { get; set; }
}

/// <summary>
/// Server-projected authentication state for the hosted admin UI.
/// </summary>
public sealed class AdminAuthSessionResponse
{
    /// <summary>
    /// Gets or sets whether the current browser session is authenticated.
    /// </summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>
    /// Gets or sets the selected provider key when available.
    /// </summary>
    public string? ProviderKey { get; set; }

    /// <summary>
    /// Gets or sets the UTC expiry for the current admin session.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Gets or sets the authenticated claims projected for the hosted admin UI.
    /// </summary>
    public List<AdminAuthClaimInfo> Claims { get; set; } = [];
}

/// <summary>
/// Claim projected from the authenticated admin session.
/// </summary>
public sealed class AdminAuthClaimInfo
{
    /// <summary>
    /// Gets or sets the claim type.
    /// </summary>
    public required string Type { get; set; }

    /// <summary>
    /// Gets or sets the claim value.
    /// </summary>
    public required string Value { get; set; }
}

internal sealed class AdminAuthAuthorizeUrlRequest
{
}

internal sealed class AdminAuthAuthorizeUrlResponse
{
    public required string AuthorizeUrl { get; init; }
}

internal sealed class AdminAuthTokenRequest
{
    public string? GrantType { get; set; }

    public string? Code { get; set; }

    public string? State { get; set; }
}

internal sealed class AdminAuthTokenResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("id_token")]
    public string? IdToken { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("token_type")]
    public string TokenType { get; init; } = "Bearer";

    [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("scope")]
    public string? Scope { get; init; }
}

internal sealed class AdminAuthLogoutUrlResponse
{
    public string? LogoutUrl { get; init; }
}

internal sealed class AdminAuthOidcDiscoveryDocument
{
    [System.Text.Json.Serialization.JsonPropertyName("authorization_endpoint")]
    public string AuthorizationEndpoint { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("token_endpoint")]
    public string TokenEndpoint { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("end_session_endpoint")]
    public string? EndSessionEndpoint { get; init; }
}
