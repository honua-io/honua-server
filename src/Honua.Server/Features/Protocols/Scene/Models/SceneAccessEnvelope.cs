// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.Scene.Models;

/// <summary>
/// A short-lived signed token authorizing CesiumJS-style nested asset
/// cascades for a single protected scene.
/// </summary>
/// <remarks>
/// Issuance is gated by the existing dataset access policy. The token rides
/// on <c>?token=</c> for browser/WebView clients (which cannot attach bearer
/// headers to nested 3D Tiles requests) or on the <c>X-Honua-Token</c>
/// header for native clients. The token value is treated as a credential
/// and must not appear in logs.
/// </remarks>
internal sealed record SceneAccessEnvelope(
    [property: JsonPropertyName("sceneId")] string SceneId,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("refreshAfter")] DateTimeOffset RefreshAfter,
    [property: JsonPropertyName("allowedMethods")] IReadOnlyList<string> AllowedMethods)
{
    /// <summary>
    /// Renders a redacted form so accidental string interpolation cannot
    /// leak the token into a log entry.
    /// </summary>
    public override string ToString()
        => $"SceneAccessEnvelope {{ SceneId = {SceneId}, Token = [redacted], ExpiresAt = {ExpiresAt:O}, RefreshAfter = {RefreshAfter:O} }}";
}
