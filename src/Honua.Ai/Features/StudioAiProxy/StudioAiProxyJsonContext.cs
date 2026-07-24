// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Ai.StudioAiProxy.Adapters.Models;
using Honua.Ai.StudioAiProxy.Domain;

namespace Honua.Ai.StudioAiProxy;

/// <summary>
/// Source-generated JSON serialization context for the Studio AI proxy: endpoint request/response
/// wire types, the streamed event shape, and the provider wire payloads used internally by the
/// Anthropic and OpenAI-compatible adapters.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(StudioAiChatHttpRequest))]
[JsonSerializable(typeof(StudioAiChatEvent))]
[JsonSerializable(typeof(StudioAiCapabilitiesResponse))]
[JsonSerializable(typeof(StudioAiProxyAuditDetails))]
[JsonSerializable(typeof(AnthropicProxyRequest))]
[JsonSerializable(typeof(AnthropicStreamFrame))]
[JsonSerializable(typeof(OpenAiProxyRequest))]
[JsonSerializable(typeof(OpenAiStreamChunk))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class StudioAiProxyJsonContext : JsonSerializerContext
{
}
