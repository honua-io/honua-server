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

internal sealed class AdminAuthAuthorizeUrlRequest
{
    public string? State { get; set; }

    public string? CodeChallenge { get; set; }
}

internal sealed class AdminAuthAuthorizeUrlResponse
{
    public required string AuthorizeUrl { get; init; }
}

internal sealed class AdminAuthTokenRequest
{
    public string? GrantType { get; set; }

    public string? Code { get; set; }

    public string? CodeVerifier { get; set; }

    public string? RefreshToken { get; set; }
}

internal sealed class AdminAuthTokenResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("id_token")]
    public string? IdToken { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

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
