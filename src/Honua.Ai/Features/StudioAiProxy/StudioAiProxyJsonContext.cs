// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Ai.StudioAiProxy.Adapters.Models;
using Honua.Ai.StudioAiProxy.Domain;

namespace Honua.Ai.StudioAiProxy;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(StudioAiChatHttpRequest))]
[JsonSerializable(typeof(StudioAiChatEvent))]
[JsonSerializable(typeof(StudioAiChatRequest))]
[JsonSerializable(typeof(List<StudioAiChatEvent>))]
[JsonSerializable(typeof(StudioAiSignedTranscript))]
[JsonSerializable(typeof(StudioAiTranscriptSigningManifest))]
[JsonSerializable(typeof(StudioAiCapabilitiesResponse))]
[JsonSerializable(typeof(StudioAiProxyAuditDetails))]
[JsonSerializable(typeof(AnthropicProxyRequest))]
[JsonSerializable(typeof(AnthropicStreamFrame))]
[JsonSerializable(typeof(OpenAiProxyRequest))]
[JsonSerializable(typeof(OpenAiStreamChunk))]
[JsonSerializable(typeof(OpenAiProxyToolChoiceSpecific))]
[JsonSerializable(typeof(OpenAiProxyToolChoiceFunctionRef))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
internal sealed partial class StudioAiProxyJsonContext : JsonSerializerContext
{
}
