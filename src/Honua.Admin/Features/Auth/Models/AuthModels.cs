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
}

/// <summary>
/// Current hosted-admin auth session projected by the server.
/// </summary>
public sealed class AdminAuthSessionInfo
{
    [JsonPropertyName("isAuthenticated")]
    public bool IsAuthenticated { get; set; }

    [JsonPropertyName("providerKey")]
    public string? ProviderKey { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonPropertyName("claims")]
    public List<AdminAuthClaimInfo> Claims { get; set; } = [];
}

/// <summary>
/// Auth claim projected to the hosted admin UI.
/// </summary>
public sealed class AdminAuthClaimInfo
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}

/// <summary>
/// Request model for server-side authorize URL generation.
/// </summary>
public sealed class AdminAuthAuthorizeUrlRequest
{
}

/// <summary>
/// Response model for server-side authorize URL generation.
/// </summary>
public sealed class AdminAuthAuthorizeUrlResponse
{
    [JsonPropertyName("authorizeUrl")]
    public string AuthorizeUrl { get; set; } = "";
}

/// <summary>
/// Request model for server-side token exchange.
/// </summary>
public sealed class AdminAuthTokenRequest
{
    [JsonPropertyName("grantType")]
    public string GrantType { get; set; } = "";

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }
}

/// <summary>
/// Response model for server-side logout URL discovery.
/// </summary>
public sealed class AdminAuthLogoutUrlResponse
{
    [JsonPropertyName("logoutUrl")]
    public string? LogoutUrl { get; set; }
}

/// <summary>
/// Token response from the backend-assisted OIDC token exchange.
/// </summary>
public sealed class TokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("id_token")]
    public string? IdToken { get; set; }

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
[JsonSerializable(typeof(AdminAuthSessionInfo))]
[JsonSerializable(typeof(AdminAuthClaimInfo))]
[JsonSerializable(typeof(List<AdminAuthClaimInfo>))]
[JsonSerializable(typeof(AdminAuthAuthorizeUrlRequest))]
[JsonSerializable(typeof(AdminAuthAuthorizeUrlResponse))]
[JsonSerializable(typeof(AdminAuthTokenRequest))]
[JsonSerializable(typeof(AdminAuthLogoutUrlResponse))]
[JsonSerializable(typeof(TokenResponse))]
internal sealed partial class AuthJsonContext : JsonSerializerContext
{
}
