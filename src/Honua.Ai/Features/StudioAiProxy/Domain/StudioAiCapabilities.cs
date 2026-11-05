// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Ai.StudioAiProxy.Domain;

public sealed class StudioAiCapability
{
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; init; }

    [JsonPropertyName("toolSupport")]
    public bool ToolSupport { get; init; }

    [JsonPropertyName("streaming")]
    public bool Streaming { get; init; } = true;

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; init; }

    [JsonPropertyName("configured")]
    public bool Configured { get; init; }
}

public sealed class StudioAiCapabilitiesResponse
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("defaultProvider")]
    public string DefaultProvider { get; init; } = string.Empty;

    [JsonPropertyName("providers")]
    public IReadOnlyList<StudioAiCapability> Providers { get; init; } = [];

    [JsonPropertyName("transcriptSigning")]
    public StudioAiTranscriptSigningManifest? TranscriptSigning { get; init; }
}
