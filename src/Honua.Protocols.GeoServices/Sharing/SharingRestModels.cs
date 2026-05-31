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
