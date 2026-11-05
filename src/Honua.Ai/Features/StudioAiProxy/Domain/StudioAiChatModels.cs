// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;
using Honua.Core.Features.Security.Admission;

namespace Honua.Ai.StudioAiProxy.Domain;

public enum StudioAiRole
{
    System,

    User,

    Assistant,

    Tool
}

public sealed class StudioAiMessage
{
    public required StudioAiRole Role { get; init; }

    public string Content { get; init; } = string.Empty;

    public string? ToolCallId { get; init; }

    public string? ToolName { get; init; }

    public IReadOnlyList<StudioAiToolCall>? ToolCalls { get; init; }
}

public sealed class StudioAiToolCall
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public JsonElement Arguments { get; init; }
}

public sealed class StudioAiToolDefinition
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public JsonElement InputSchema { get; init; }

    public JsonElement? Annotations { get; init; }

    public JsonElement? OutputSchema { get; init; }

    // Annotations and output schemas are appended to the model-visible description because provider tool APIs do not expose them as their own fields.
    internal string? BuildProviderDescription()
    {
        if (Annotations is null && OutputSchema is null)
        {
            return Description;
        }

        var description = new StringBuilder(Description);
        AppendContract(description, "Tool annotations", Annotations);
        AppendContract(description, "Expected structured output schema", OutputSchema);
        return description.ToString();
    }

    private static void AppendContract(StringBuilder description, string label, JsonElement? contract)
    {
        if (contract is null)
        {
            return;
        }

        if (description.Length > 0)
        {
            description.AppendLine().AppendLine();
        }

        description.Append(label).Append(" (JSON): ").Append(contract.Value.GetRawText());
    }
}

public enum StudioAiToolChoiceMode
{
    Auto,

    None,

    Required,

    Specific
}

public sealed class StudioAiToolChoice
{
    public StudioAiToolChoiceMode Mode { get; init; } = StudioAiToolChoiceMode.Auto;

    public string? ToolName { get; init; }
}

public sealed class StudioAiChatRequest
{
    // The admission identity is built by the server and is never accepted from caller JSON.
    [JsonIgnore]
    public AiExecutionContext? ExecutionContext { get; init; }

    internal byte[]? AcceptedRequestJson { get; init; }

    public StudioAiTranscriptCertification? Certification { get; init; }
    public string? Provider { get; init; }

    public string? Model { get; init; }

    public string? System { get; init; }

    public required IReadOnlyList<StudioAiMessage> Messages { get; init; }

    public IReadOnlyList<StudioAiToolDefinition>? Tools { get; init; }

    public StudioAiToolChoice? ToolChoice { get; init; }

    public int? MaxTokens { get; init; }

    public double? Temperature { get; init; }
}

public sealed class StudioAiChatHttpRequest
{
    [JsonPropertyName("certification")]
    public StudioAiTranscriptCertification? Certification { get; init; }
    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("system")]
    public string? System { get; init; }

    [JsonPropertyName("messages")]
    public List<StudioAiChatHttpMessage>? Messages { get; init; } = [];

    [JsonPropertyName("tools")]
    public List<StudioAiChatHttpTool>? Tools { get; init; }

    [JsonPropertyName("toolChoice")]
    public StudioAiChatHttpToolChoice? ToolChoice { get; init; }

    [JsonPropertyName("maxTokens")]
    public int? MaxTokens { get; init; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }
}

public sealed class StudioAiChatHttpMessage
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string? Content { get; init; } = string.Empty;

    [JsonPropertyName("toolCallId")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("toolName")]
    public string? ToolName { get; init; }

    [JsonPropertyName("toolCalls")]
    public List<StudioAiChatHttpToolCall>? ToolCalls { get; init; }
}

public sealed class StudioAiChatHttpToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; init; }
}

public sealed class StudioAiChatHttpTool
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("inputSchema")]
    public JsonElement InputSchema { get; init; }

    [JsonPropertyName("annotations")]
    public JsonElement? Annotations { get; init; }

    [JsonPropertyName("outputSchema")]
    public JsonElement? OutputSchema { get; init; }
}

public sealed class StudioAiChatHttpToolChoice
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "auto";

    [JsonPropertyName("toolName")]
    public string? ToolName { get; init; }
}
