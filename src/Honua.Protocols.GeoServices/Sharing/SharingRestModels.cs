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
