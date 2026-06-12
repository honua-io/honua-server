// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.Location;

/// <summary>
/// AOT-compatible source-generated JSON context for the geocode/route MCP
/// DTOs. Kept separate from the core MCP JSON context so the location feature
/// slice owns its own serializer surface - mirrors the grounding slice's
/// <c>GroundingJsonContext</c>.
/// </summary>
[JsonSerializable(typeof(McpGeocodeArgument))]
[JsonSerializable(typeof(McpGeocodeOutput))]
[JsonSerializable(typeof(McpGeocodeCandidate))]
[JsonSerializable(typeof(McpRouteArgument))]
[JsonSerializable(typeof(McpRouteStopInput))]
[JsonSerializable(typeof(McpRouteOutput))]
[JsonSerializable(typeof(McpRouteDirectionStep))]
[JsonSerializable(typeof(JsonElement))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class LocationJsonContext : JsonSerializerContext;
