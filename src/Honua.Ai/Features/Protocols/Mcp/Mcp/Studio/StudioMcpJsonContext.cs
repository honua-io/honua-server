// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Ai.Protocols.Mcp.Studio;

[JsonSerializable(typeof(McpStudioSaveVersionArgument))]
[JsonSerializable(typeof(McpStudioReopenVersionArgument))]
[JsonSerializable(typeof(McpStudioGetVersionArgument))]
[JsonSerializable(typeof(McpStudioGetVersionOutput))]
[JsonSerializable(typeof(McpStudioSaveVersionOutput))]
[JsonSerializable(typeof(McpStudioCreateDraftArgument))]
[JsonSerializable(typeof(McpStudioDraftMutationOutput))]
[JsonSerializable(typeof(McpStudioDraftIdArgument))]
[JsonSerializable(typeof(McpStudioUpdateDraftArgument))]
[JsonSerializable(typeof(McpStudioLayerInput))]
[JsonSerializable(typeof(McpStudioViewInput))]
[JsonSerializable(typeof(McpStudioWidgetInput))]
[JsonSerializable(typeof(McpStudioAddLayerArgument))]
[JsonSerializable(typeof(McpStudioRemoveLayerArgument))]
[JsonSerializable(typeof(McpStudioSetLayerStyleArgument))]
[JsonSerializable(typeof(McpStudioSetLayerVisibilityArgument))]
[JsonSerializable(typeof(McpStudioSetViewArgument))]
[JsonSerializable(typeof(McpStudioAddWidgetArgument))]
[JsonSerializable(typeof(McpStudioRemoveWidgetArgument))]
[JsonSerializable(typeof(McpStudioInteractionEventInput))]
[JsonSerializable(typeof(McpStudioInteractionActionInput))]
[JsonSerializable(typeof(McpStudioInteractionInput))]
[JsonSerializable(typeof(McpStudioBindInteractionArgument))]
[JsonSerializable(typeof(McpStudioRemoveInteractionArgument))]
[JsonSerializable(typeof(McpStudioControlInput))]
[JsonSerializable(typeof(McpStudioAddControlArgument))]
[JsonSerializable(typeof(McpStudioRemoveControlArgument))]
[JsonSerializable(typeof(McpStudioProposePublicationArgument))]
[JsonSerializable(typeof(McpStudioProposePublicationOutput))]
[JsonSerializable(typeof(StudioPackageDraft))]
[JsonSerializable(typeof(StudioValidationSummary))]
[JsonSerializable(typeof(StudioPreviewPlan))]
[JsonSerializable(typeof(JsonElement))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
internal sealed partial class StudioMcpJsonContext : JsonSerializerContext;
