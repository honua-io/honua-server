// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.Views;

[JsonSerializable(typeof(McpWorkflowViewToolsListResult))]
[JsonSerializable(typeof(McpWorkflowViewMeta))]
[JsonSerializable(typeof(McpWorkflowViewStageMeta))]
[JsonSerializable(typeof(McpWorkflowViewSummary))]
[JsonSerializable(typeof(McpToolMetadataEnvelope))]
[JsonSerializable(typeof(McpStudioToolClassification))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class WorkflowViewJsonContext : JsonSerializerContext;
