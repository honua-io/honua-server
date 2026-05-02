// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Source-generated JSON context for scene dataset admin API models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SceneDatasetSummary))]
[JsonSerializable(typeof(SceneDatasetSummary[]))]
[JsonSerializable(typeof(IReadOnlyList<SceneDatasetSummary>))]
[JsonSerializable(typeof(SceneDatasetDetail))]
[JsonSerializable(typeof(SceneDatasetResolveResponse))]
[JsonSerializable(typeof(RegisterSceneDatasetRequest))]
[JsonSerializable(typeof(UpdateSceneDatasetRequest))]
[JsonSerializable(typeof(SceneExtentDto))]
[JsonSerializable(typeof(SceneCachePolicyDto))]
internal sealed partial class SceneDatasetJsonContext : JsonSerializerContext
{
}
