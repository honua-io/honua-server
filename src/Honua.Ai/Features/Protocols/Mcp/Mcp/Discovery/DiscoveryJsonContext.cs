// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.Discovery;

/// <summary>
/// AOT-compatible source-generated JSON context for the discovery MCP tools
/// (<c>honua_resolve_entity</c>, <c>honua_list_capabilities</c>). Kept separate
/// from the core MCP JSON context so the discovery slice owns its serializer
/// surface — mirrors the map-tools and location slices.
/// </summary>
[JsonSerializable(typeof(McpResolveEntityArgument))]
[JsonSerializable(typeof(McpResolveEntityOutput))]
[JsonSerializable(typeof(McpEntityMatch))]
[JsonSerializable(typeof(McpListCapabilitiesArgument))]
[JsonSerializable(typeof(McpListCapabilitiesOutput))]
[JsonSerializable(typeof(McpCapabilityTool))]
[JsonSerializable(typeof(McpCapabilityResource))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class DiscoveryJsonContext : JsonSerializerContext;
