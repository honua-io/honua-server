// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.Views;

/// <summary>
/// AOT-compatible source-generated JSON context for the workflow-view wire shapes
/// (honua-server#3428). Kept separate from the core MCP JSON context so the view
/// slice owns its serializer surface — mirrors the discovery, map-tools, and
/// location slices.
/// </summary>
[JsonSerializable(typeof(McpWorkflowViewToolsListResult))]
[JsonSerializable(typeof(McpWorkflowViewMeta))]
[JsonSerializable(typeof(McpWorkflowViewStageMeta))]
[JsonSerializable(typeof(McpWorkflowViewSummary))]
[JsonSerializable(typeof(McpToolMetadataEnvelope))]
[JsonSerializable(typeof(McpStudioToolClassification))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class WorkflowViewJsonContext : JsonSerializerContext;
