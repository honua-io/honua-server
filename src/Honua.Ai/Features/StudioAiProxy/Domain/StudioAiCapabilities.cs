// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Ai.StudioAiProxy.Domain;

/// <summary>
/// Capability descriptor for one configured provider, so Studio clients can adapt (context length,
/// tool support) without any provider-specific code (REQ-003 of honua-server#3000).
/// </summary>
public sealed class StudioAiCapability
{
    /// <summary>The operator-chosen provider name (a key under <c>StudioAiProxy:Providers</c>).</summary>
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    /// <summary>Which adapter this provider routes through: <c>"anthropic"</c>, <c>"openai"</c>, or <c>"bedrock"</c>.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>The configured model id.</summary>
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    /// <summary>Configured max-tokens ceiling for this provider.</summary>
    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; init; }

    /// <summary>Whether this provider accepts tool/function-call definitions.</summary>
    [JsonPropertyName("toolSupport")]
    public bool ToolSupport { get; init; }

    /// <summary>Whether this provider streams (all v0 adapters do).</summary>
    [JsonPropertyName("streaming")]
    public bool Streaming { get; init; } = true;

    /// <summary>Whether this is the provider used when a chat request omits <c>provider</c>.</summary>
    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; init; }

    /// <summary>
    /// Whether the provider has everything it needs to be called (model + credentials resolvable).
    /// A provider can be declared but not yet configured (missing key) — surfaced here rather than
    /// omitted so operators can see the gap.
    /// </summary>
    [JsonPropertyName("configured")]
    public bool Configured { get; init; }
}

/// <summary>Response body for <c>GET /api/v1/studio/ai/capabilities</c>.</summary>
public sealed class StudioAiCapabilitiesResponse
{
    /// <summary>Whether the Studio AI proxy feature is enabled at all.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    /// <summary>Name of the default provider, or empty when none is configured.</summary>
    [JsonPropertyName("defaultProvider")]
    public string DefaultProvider { get; init; } = string.Empty;

    /// <summary>Every declared provider, configured or not.</summary>
    [JsonPropertyName("providers")]
    public IReadOnlyList<StudioAiCapability> Providers { get; init; } = [];
}
