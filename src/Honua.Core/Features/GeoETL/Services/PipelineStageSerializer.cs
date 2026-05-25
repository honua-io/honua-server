// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.GeoETL.Domain;

namespace Honua.Core.Features.GeoETL.Services;

/// <summary>
/// Source-generated JSON context for persisting a pipeline's stage chain as a JSONB
/// document in <c>honua.pipeline_definitions.stages_json</c>. Kept separate from the
/// HTTP DTO context so the durable storage shape evolves independently of the wire shape
/// and stays AOT/trim safe (no reflection-based serialization). See ADR-0038 §
/// Discriminated-union versioning.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(PipelineStage[]))]
[JsonSerializable(typeof(PipelineStage))]
[JsonSerializable(typeof(ConnectorConfig))]
[JsonSerializable(typeof(TransformConfig))]
internal sealed partial class PipelineStageJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Serializes and deserializes a pipeline's ordered stage chain to and from the JSONB
/// document persisted by the PostgreSQL definition store. Centralizes the round-trip so
/// the durable store and its tests share one contract.
/// </summary>
public static class PipelineStageSerializer
{
    /// <summary>
    /// Serializes the stage chain to its JSONB document string.
    /// </summary>
    /// <param name="stages">Ordered stage chain.</param>
    /// <returns>The JSON document.</returns>
    public static string Serialize(IReadOnlyList<PipelineStage> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        var array = stages as PipelineStage[] ?? stages.ToArray();
        return JsonSerializer.Serialize(array, PipelineStageJsonContext.Default.PipelineStageArray);
    }

    /// <summary>
    /// Deserializes a stage-chain JSONB document back into the domain stage list.
    /// </summary>
    /// <param name="json">The JSON document.</param>
    /// <returns>The deserialized stage chain (empty when the document is null or blank).</returns>
    public static IReadOnlyList<PipelineStage> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize(json, PipelineStageJsonContext.Default.PipelineStageArray) ?? [];
    }
}
