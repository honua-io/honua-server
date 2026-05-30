// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Protocols.Scene.Models;

namespace Honua.Protocols.Scene;

/// <summary>
/// Compact internal payload encoded into the wire token. The two-letter
/// names keep the envelope short under URL-encoding.
/// </summary>
/// <param name="SceneId">Scene identifier the token is bound to.</param>
/// <param name="ExpiresAtUnixSeconds">Absolute expiry as Unix epoch seconds (UTC).</param>
internal sealed record SceneAccessTokenPayload(
    [property: JsonPropertyName("s")] string SceneId,
    [property: JsonPropertyName("e")] long ExpiresAtUnixSeconds);

/// <summary>
/// Source-generated JSON serialization context for scene access envelope
/// types. Keeps the feature AOT/trimming-safe and avoids reflection.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SceneAccessEnvelope))]
[JsonSerializable(typeof(SceneAccessTokenPayload))]
internal sealed partial class SceneAccessEnvelopeJsonContext : JsonSerializerContext
{
}
