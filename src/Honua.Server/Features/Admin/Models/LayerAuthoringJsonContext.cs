// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for the layer authoring admin APIs (relationships, popup-info, drawing-info).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(LayerRelationshipUpdateRequest))]
[JsonSerializable(typeof(LayerRelationshipUpdateItem))]
[JsonSerializable(typeof(LayerRelationshipResponse))]
[JsonSerializable(typeof(LayerRelationshipItem))]
[JsonSerializable(typeof(LayerAuthoringDocumentResponse))]
[JsonSerializable(typeof(ApiResponse<LayerRelationshipResponse>))]
[JsonSerializable(typeof(ApiResponse<LayerAuthoringDocumentResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class LayerAuthoringJsonContext : JsonSerializerContext
{
}
