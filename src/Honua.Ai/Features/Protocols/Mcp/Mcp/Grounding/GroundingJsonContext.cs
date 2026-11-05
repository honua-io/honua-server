// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.Grounding;

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
[JsonSerializable(typeof(McpElicitationRequest))]
[JsonSerializable(typeof(McpElicitationSchema))]
[JsonSerializable(typeof(McpElicitationProperty))]
[JsonSerializable(typeof(JsonElement))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class GroundingJsonContext : JsonSerializerContext;
