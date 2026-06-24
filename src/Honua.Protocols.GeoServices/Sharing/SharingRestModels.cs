// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.Sharing;

/// <summary>
/// Successful response body for <c>/sharing/rest/generateToken</c>.
/// </summary>
/// <remarks>
/// Shape matches the ArcGIS Portal sharing API so existing Esri clients can
/// consume the response without modification:
/// <code>{ "token": "...", "expires": 1234567890, "ssl": true }</code>
/// </remarks>
internal sealed record GenerateTokenResponse
{
    /// <summary>
    /// Opaque token to attach to subsequent <c>/rest/services</c> requests.
    /// </summary>
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    /// <summary>
    /// Token expiry expressed as Unix milliseconds, the canonical ArcGIS shape.
    /// </summary>
    [JsonPropertyName("expires")]
    public required long Expires { get; init; }

    /// <summary>
    /// Whether the issuing portal requires secured transport. Always
    /// <see langword="true"/> for Honua-issued tokens.
    /// </summary>
    [JsonPropertyName("ssl")]
    public bool Ssl { get; init; } = true;
}

/// <summary>
/// Successful response body for <c>POST /sharing/rest/oauth2/token</c>.
/// </summary>
/// <remarks>
/// Shape matches the ArcGIS OAuth2 token endpoint so ArcGIS Pro / Field Maps can
/// consume it directly:
/// <code>{ "access_token": "...", "expires_in": 3600, "refresh_token": "..." }</code>
/// Unlike <see cref="GenerateTokenResponse"/> (whose <c>expires</c> is Unix
/// milliseconds), <c>expires_in</c> is the token lifetime in <em>seconds</em>, per
/// the Esri oauth2/token convention.
/// </remarks>
internal sealed record OAuth2TokenResponse
{
    /// <summary>Opaque portal access token minted via <c>IPortalTokenIssuer</c>.</summary>
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    /// <summary>Token lifetime in seconds.</summary>
    [JsonPropertyName("expires_in")]
    public required long ExpiresIn { get; init; }

    /// <summary>Refresh token for the named user, when issued.</summary>
    [JsonPropertyName("refresh_token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RefreshToken { get; init; }

    /// <summary>
    /// Space-delimited scopes actually granted (RFC 6749 §5.1), present for the
    /// first-class client_credentials grant (#1888) when scopes were granted.
    /// </summary>
    [JsonPropertyName("scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope { get; init; }

    /// <summary>Token type; always <c>Bearer</c>.</summary>
    [JsonPropertyName("token_type")]
    public string TokenType { get; init; } = "Bearer";
}

/// <summary>
/// OAuth2 error envelope (RFC 6749 §5.2) returned by <c>oauth2/token</c>.
/// </summary>
internal sealed record OAuth2ErrorResponse
{
    /// <summary>Machine-readable OAuth2 error code.</summary>
    [JsonPropertyName("error")]
    public required string Error { get; init; }

    /// <summary>Human-readable error description.</summary>
    [JsonPropertyName("error_description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorDescription { get; init; }
}

/// <summary>
/// RFC 7662 token introspection response (ADR-0054, #1890). An inactive token
/// returns only <c>active=false</c> (RFC 7662 §2.2); the remaining fields are
/// present only for an active token.
/// </summary>
internal sealed record OAuth2IntrospectionResponse
{
    /// <summary>Whether the presented token is currently active (live, not revoked).</summary>
    [JsonPropertyName("active")]
    public required bool Active { get; init; }

    /// <summary>Subject the token was issued to (the principal id).</summary>
    [JsonPropertyName("sub")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sub { get; init; }

    /// <summary>Human-readable identifier for the resource owner (mirrors <c>sub</c>).</summary>
    [JsonPropertyName("username")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Username { get; init; }

    /// <summary>Space-delimited roles/scopes carried by the token.</summary>
    [JsonPropertyName("scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope { get; init; }

    /// <summary>Token type; <c>Bearer</c> for an active token.</summary>
    [JsonPropertyName("token_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TokenType { get; init; }

    /// <summary>Expiry as seconds since the Unix epoch (<c>exp</c>).</summary>
    [JsonPropertyName("exp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Exp { get; init; }
}
