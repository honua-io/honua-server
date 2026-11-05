// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.Discovery;

[JsonSerializable(typeof(McpResolveEntityArgument))]
[JsonSerializable(typeof(McpResolveEntityOutput))]
[JsonSerializable(typeof(McpEntityMatch))]
[JsonSerializable(typeof(McpListCapabilitiesArgument))]
[JsonSerializable(typeof(McpListCapabilitiesOutput))]
[JsonSerializable(typeof(McpCapabilityTool))]
[JsonSerializable(typeof(McpCapabilityResource))]
[JsonSerializable(typeof(Views.McpWorkflowViewSummary))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class DiscoveryJsonContext : JsonSerializerContext;
