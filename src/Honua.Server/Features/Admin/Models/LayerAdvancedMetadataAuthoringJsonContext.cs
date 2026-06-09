// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for the advanced layer/publication metadata authoring
/// admin APIs (subtypes, attribute rules, 3D extrusion &amp; symbology, publication
/// overrides, lifecycle status).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(LayerSubtypesUpdateRequest))]
[JsonSerializable(typeof(LayerSubtypesResponse))]
[JsonSerializable(typeof(SubtypePayload))]
[JsonSerializable(typeof(SubtypeFieldOverridePayload))]
[JsonSerializable(typeof(LayerAttributeRulesUpdateRequest))]
[JsonSerializable(typeof(LayerAttributeRulesResponse))]
[JsonSerializable(typeof(AttributeRulePayload))]
[JsonSerializable(typeof(LayerExtrusionUpdateRequest))]
[JsonSerializable(typeof(LayerExtrusionResponse))]
[JsonSerializable(typeof(ExtrusionInfoPayload))]
[JsonSerializable(typeof(Symbology3DPayload))]
[JsonSerializable(typeof(Symbology3DRulePayload))]
[JsonSerializable(typeof(Symbology3DColorPayload))]
[JsonSerializable(typeof(PublicationOverridesUpdateRequest))]
[JsonSerializable(typeof(PublicationOverridesResponse))]
[JsonSerializable(typeof(LayerStatusUpdateRequest))]
[JsonSerializable(typeof(LayerStatusResponse))]
[JsonSerializable(typeof(ApiResponse<LayerSubtypesResponse>))]
[JsonSerializable(typeof(ApiResponse<LayerAttributeRulesResponse>))]
[JsonSerializable(typeof(ApiResponse<LayerExtrusionResponse>))]
[JsonSerializable(typeof(ApiResponse<PublicationOverridesResponse>))]
[JsonSerializable(typeof(ApiResponse<LayerStatusResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class LayerAdvancedMetadataAuthoringJsonContext : JsonSerializerContext
{
}
