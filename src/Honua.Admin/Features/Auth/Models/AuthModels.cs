// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Admin.Features.Auth.Models;

/// <summary>
/// Client-side representation of the admin auth configuration from the server.
/// </summary>
public sealed class AdminAuthConfig
{
    [JsonPropertyName("oidcEnabled")]
    public bool OidcEnabled { get; set; }

    [JsonPropertyName("providers")]
    public List<AuthProviderInfo> Providers { get; set; } = [];

    [JsonPropertyName("apiKeyFallbackEnabled")]
    public bool ApiKeyFallbackEnabled { get; set; }
}

/// <summary>
/// Client-safe metadata for a single OIDC provider.
/// </summary>
public sealed class AuthProviderInfo
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("authority")]
    public string Authority { get; set; } = "";

    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = "";

    [JsonPropertyName("scopes")]
    public string[] Scopes { get; set; } = ["openid", "profile", "email"];

    [JsonPropertyName("redirectPath")]
    public string RedirectPath { get; set; } = "/admin/auth/callback";

    [JsonPropertyName("supportsLogout")]
    public bool SupportsLogout { get; set; }

    [JsonPropertyName("postLogoutRedirectPath")]
    public string? PostLogoutRedirectPath { get; set; }
}

/// <summary>
/// OIDC discovery document (subset of fields used for PKCE flow).
/// </summary>
public sealed class OidcDiscoveryDocument
{
    [JsonPropertyName("authorization_endpoint")]
    public string AuthorizationEndpoint { get; set; } = "";

    [JsonPropertyName("token_endpoint")]
    public string TokenEndpoint { get; set; } = "";

    [JsonPropertyName("end_session_endpoint")]
    public string? EndSessionEndpoint { get; set; }

    [JsonPropertyName("userinfo_endpoint")]
    public string? UserinfoEndpoint { get; set; }
}

/// <summary>
/// Token response from the OIDC token endpoint.
/// </summary>
public sealed class TokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("id_token")]
    public string? IdToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "Bearer";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}

/// <summary>
/// Source-generated JSON context for admin auth client models (AOT/trimming compatible).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AdminAuthConfig))]
[JsonSerializable(typeof(AuthProviderInfo))]
[JsonSerializable(typeof(List<AuthProviderInfo>))]
[JsonSerializable(typeof(OidcDiscoveryDocument))]
[JsonSerializable(typeof(TokenResponse))]
internal sealed partial class AuthJsonContext : JsonSerializerContext
{
}
