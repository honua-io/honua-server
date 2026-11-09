// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Protocols.Scene.Models;

namespace Honua.Protocols.Scene;

internal sealed record SceneAccessTokenPayload(
    [property: JsonPropertyName("s")] string SceneId,
    [property: JsonPropertyName("e")] long ExpiresAtUnixSeconds);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SceneAccessEnvelope))]
[JsonSerializable(typeof(SceneAccessTokenPayload))]
internal sealed partial class SceneAccessEnvelopeJsonContext : JsonSerializerContext
{
}
