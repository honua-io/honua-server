// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.MapTools;

/// <summary>
/// AOT-compatible source-generated JSON context for the catalog / feature-query
/// / map-render MCP DTOs. Kept separate from the core MCP JSON context so the
/// map-tools feature slice owns its own serializer surface — mirrors the
/// location slice's <c>LocationJsonContext</c>.
/// </summary>
[JsonSerializable(typeof(McpListLayersArgument))]
[JsonSerializable(typeof(McpListLayersOutput))]
[JsonSerializable(typeof(McpLayerSummary))]
[JsonSerializable(typeof(McpExtent))]
[JsonSerializable(typeof(McpQueryFeaturesArgument))]
[JsonSerializable(typeof(McpQueryFeaturesOutput))]
[JsonSerializable(typeof(McpGeoJsonFeatureCollection))]
[JsonSerializable(typeof(McpRenderMapArgument))]
[JsonSerializable(typeof(McpRenderLayerRef))]
[JsonSerializable(typeof(McpRenderMapOutput))]
[JsonSerializable(typeof(McpRenderedLayer))]
[JsonSerializable(typeof(McpRenderedImage))]
[JsonSerializable(typeof(McpGetStyleArgument))]
[JsonSerializable(typeof(McpGetStyleOutput))]
[JsonSerializable(typeof(McpStyleEncodingRef))]
[JsonSerializable(typeof(McpStyleSummary))]
[JsonSerializable(typeof(McpApplyStylePresetArgument))]
[JsonSerializable(typeof(McpApplyStylePresetOutput))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonElement))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class MapToolJsonContext : JsonSerializerContext;
