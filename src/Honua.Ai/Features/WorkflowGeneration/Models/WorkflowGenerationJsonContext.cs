// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Core.Features.WorkflowPackages.Generation.Domain;

namespace Honua.Ai.WorkflowGeneration.Models;

/// <summary>
/// Source-generated JSON serialization context for workflow-generation provider types.
/// Used to (de)serialize provider wire payloads and the structured-output graph proposal.
/// Enum strings are PascalCase to match the <see cref="WorkflowGraph"/> contract that
/// <c>PUT /workflow-packages/{id}</c> already accepts (for example edge kind "Control").
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(WorkflowGenerationModelProposal))]
[JsonSerializable(typeof(WorkflowGraph))]
[JsonSerializable(typeof(WorkflowNode))]
[JsonSerializable(typeof(WorkflowEdge))]
[JsonSerializable(typeof(WorkflowSchedule))]
[JsonSerializable(typeof(WorkflowGenerationClarification))]
[JsonSerializable(typeof(WorkflowGenerationClarificationChoice))]
[JsonSerializable(typeof(WorkflowGenerationCapabilityState))]
[JsonSerializable(typeof(OpenAiChatCompletionRequest))]
[JsonSerializable(typeof(OpenAiChatCompletionResponse))]
[JsonSerializable(typeof(OpenAiMessage))]
[JsonSerializable(typeof(OpenAiResponseFormat))]
[JsonSerializable(typeof(OpenAiJsonSchema))]
[JsonSerializable(typeof(OpenAiChoice))]
[JsonSerializable(typeof(OpenAiUsage))]
[JsonSerializable(typeof(AnthropicMessagesRequest))]
[JsonSerializable(typeof(AnthropicMessagesResponse))]
[JsonSerializable(typeof(AnthropicMessage))]
[JsonSerializable(typeof(AnthropicTool))]
[JsonSerializable(typeof(AnthropicToolChoice))]
[JsonSerializable(typeof(AnthropicContentBlock))]
[JsonSerializable(typeof(AnthropicUsage))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class WorkflowGenerationJsonContext : JsonSerializerContext
{
}
