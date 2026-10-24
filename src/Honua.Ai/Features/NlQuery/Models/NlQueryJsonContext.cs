// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.NlQuery.Domain;

namespace Honua.Ai.NlQuery.Models;

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
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class NlQueryJsonContext : JsonSerializerContext
{
}
