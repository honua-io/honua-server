// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Grounding.Mcp;

/// <summary>
/// AOT-compatible source-generated JSON context for the grounding MCP
/// DTOs. Kept separate from the core MCP JSON context so the grounding feature
/// slice owns its own serializer surface and does not force the protocol
/// context to recompile whenever the grounding output shape evolves.
/// </summary>
[JsonSerializable(typeof(McpGroundCandidatesArgument))]
[JsonSerializable(typeof(McpClarifyIntentArgument))]
[JsonSerializable(typeof(McpCallerContextInput))]
[JsonSerializable(typeof(McpIntentConstraintsInput))]
[JsonSerializable(typeof(McpClarificationResponseInput))]
[JsonSerializable(typeof(McpGroundingOutput))]
[JsonSerializable(typeof(McpWorkflowFamilyClassification))]
[JsonSerializable(typeof(McpDraftIntent))]
[JsonSerializable(typeof(McpAnalysisIntentView))]
[JsonSerializable(typeof(McpPublishIntentView))]
[JsonSerializable(typeof(McpGroundingProvenance))]
[JsonSerializable(typeof(McpGroundingProvenanceSource))]
[JsonSerializable(typeof(McpCandidateRanking))]
[JsonSerializable(typeof(McpGroundingCandidate))]
[JsonSerializable(typeof(McpClarificationEnvelope))]
[JsonSerializable(typeof(McpClarificationQuestionView))]
[JsonSerializable(typeof(McpClarificationOptionView))]
[JsonSerializable(typeof(JsonElement))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class GroundingJsonContext : JsonSerializerContext;
