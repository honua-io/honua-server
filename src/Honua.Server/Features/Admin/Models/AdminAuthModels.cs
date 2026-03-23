// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Response model for the admin auth bootstrap endpoint.
/// Exposes only client-safe OIDC provider metadata; no secrets are included.
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

    /// <summary>
    /// Gets or sets whether API key authentication fallback should remain visible.
    /// True when no OIDC providers are configured and the environment allows API key access.
    /// </summary>
    public bool ApiKeyFallbackEnabled { get; set; }
}

/// <summary>
/// Client-safe metadata for a single OIDC provider.
/// Never includes client secrets or server-side configuration.
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

    /// <summary>
    /// Gets or sets the OIDC authority / issuer URL.
    /// </summary>
    public required string Authority { get; set; }

    /// <summary>
    /// Gets or sets the public client ID for PKCE flow.
    /// </summary>
    public required string ClientId { get; set; }

    /// <summary>
    /// Gets or sets the scopes to request during authorization.
    /// </summary>
    public string[] Scopes { get; set; } = ["openid", "profile", "email"];

    /// <summary>
    /// Gets or sets the redirect callback path for the authorization response.
    /// </summary>
    public string RedirectPath { get; set; } = "/admin/auth/callback";

    /// <summary>
    /// Gets or sets whether the provider supports a logout endpoint.
    /// </summary>
    public bool SupportsLogout { get; set; }

    /// <summary>
    /// Gets or sets the post-logout redirect path.
    /// </summary>
    public string? PostLogoutRedirectPath { get; set; }
}
