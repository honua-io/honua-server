// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.NlQuery.Domain;

namespace Honua.Server.Features.NlQuery.Models;

/// <summary>
/// Source-generated JSON serialization context for NL query types.
/// Ensures AOT compatibility with no reflection-based serialization.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FilterPlan))]
[JsonSerializable(typeof(FilterPlanClause))]
[JsonSerializable(typeof(ComparisonClause))]
[JsonSerializable(typeof(SpatialClause))]
[JsonSerializable(typeof(TemporalClause))]
[JsonSerializable(typeof(NestedClause))]
[JsonSerializable(typeof(OpenAiChatCompletionRequest))]
[JsonSerializable(typeof(OpenAiChatCompletionResponse))]
[JsonSerializable(typeof(OpenAiMessage))]
[JsonSerializable(typeof(OpenAiResponseFormat))]
[JsonSerializable(typeof(OpenAiJsonSchema))]
[JsonSerializable(typeof(OpenAiChoice))]
[JsonSerializable(typeof(OpenAiUsage))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class NlQueryJsonContext : JsonSerializerContext
{
}
