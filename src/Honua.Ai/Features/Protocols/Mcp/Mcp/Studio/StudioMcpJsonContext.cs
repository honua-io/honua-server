// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Ai.Protocols.Mcp.Studio;

/// <summary>
/// AOT-compatible source-generated JSON context for the Studio draft
/// lifecycle / composition MCP DTOs (honua-server#3002). Kept separate from
/// the core MCP JSON context so the Studio tools slice owns its own
/// serializer surface — mirrors <see cref="Honua.Ai.Protocols.Mcp.MapTools.MapToolJsonContext"/>.
/// Domain types owned by <c>Honua.Core.Features.Studio.Domain.StudioJsonContext</c>
/// (<see cref="StudioPackageDraft"/>, <see cref="StudioValidationSummary"/>,
/// <see cref="StudioPreviewPlan"/>) are re-declared here too — tools return
/// them directly (no wrapper DTO) via
/// <c>McpToolHelpers.SuccessResult(value, StudioJsonContext.Default.T)</c>
/// where a wrapper type is not needed, and this context's own copies exist
/// only for use inside the local wrapper output
/// (<see cref="McpStudioProposePublicationOutput"/>). Multiple
/// <see cref="JsonSerializerContext"/> types independently declaring the same
/// serializable type is supported by the source generator.
/// </summary>
[JsonSerializable(typeof(McpStudioCreateDraftArgument))]
[JsonSerializable(typeof(McpStudioDraftIdArgument))]
[JsonSerializable(typeof(McpStudioUpdateDraftArgument))]
[JsonSerializable(typeof(McpStudioLayerInput))]
[JsonSerializable(typeof(McpStudioViewInput))]
[JsonSerializable(typeof(McpStudioWidgetInput))]
[JsonSerializable(typeof(McpStudioAddLayerArgument))]
[JsonSerializable(typeof(McpStudioRemoveLayerArgument))]
[JsonSerializable(typeof(McpStudioSetLayerStyleArgument))]
[JsonSerializable(typeof(McpStudioSetViewArgument))]
[JsonSerializable(typeof(McpStudioAddWidgetArgument))]
[JsonSerializable(typeof(McpStudioRemoveWidgetArgument))]
[JsonSerializable(typeof(McpStudioProposePublicationArgument))]
[JsonSerializable(typeof(McpStudioProposePublicationOutput))]
[JsonSerializable(typeof(StudioPackageDraft))]
[JsonSerializable(typeof(StudioValidationSummary))]
[JsonSerializable(typeof(StudioPreviewPlan))]
[JsonSerializable(typeof(JsonElement))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class StudioMcpJsonContext : JsonSerializerContext;
