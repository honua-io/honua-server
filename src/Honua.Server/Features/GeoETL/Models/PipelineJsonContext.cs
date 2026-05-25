// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.GeoETL.Models;

/// <summary>
/// Source-generated JSON context for the GeoETL pipeline API DTOs. Keeps the GeoETL
/// HTTP surface AOT/trim safe per ADR-0038 (no reflection-based serialization).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PipelineDefinitionRequest))]
[JsonSerializable(typeof(PipelineDefinitionResponse))]
[JsonSerializable(typeof(PipelineListResponse))]
[JsonSerializable(typeof(PipelineExecutionResponse))]
[JsonSerializable(typeof(PipelineExecutionListResponse))]
[JsonSerializable(typeof(ApiResponse<PipelineDefinitionResponse>))]
[JsonSerializable(typeof(ApiResponse<PipelineListResponse>))]
[JsonSerializable(typeof(ApiResponse<PipelineExecutionResponse>))]
[JsonSerializable(typeof(ApiResponse<PipelineExecutionListResponse>))]
internal sealed partial class PipelineJsonContext : JsonSerializerContext
{
}
