// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Ai.Protocols.Mcp.Studio;

// -----------------------------------------------------------------------
// honua_studio_create_draft
// -----------------------------------------------------------------------

internal sealed class McpStudioCreateDraftArgument
{
    [JsonPropertyName("packageKey")]
    public string? PackageKey { get; set; }

    [JsonPropertyName("family")]
    public string? Family { get; set; }

    [JsonPropertyName("schemaVersion")]
    public string? SchemaVersion { get; set; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; set; }

    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; set; }

    [JsonPropertyName("body")]
    public JsonElement? Body { get; set; }

    [JsonPropertyName("itemId")]
    public Guid? ItemId { get; set; }

    [JsonPropertyName("baseVersionId")]
    public Guid? BaseVersionId { get; set; }
}

internal sealed record McpStudioDraftMutationOutput
{
    [JsonPropertyName("operation")]
    public required OperationHandle Operation { get; init; }

    [JsonPropertyName("draftId")]
    public Guid? DraftId { get; init; }

    [JsonPropertyName("itemId")]
    public Guid? ItemId { get; init; }

    [JsonPropertyName("packageKey")]
    public string? PackageKey { get; init; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; init; }

    [JsonPropertyName("family")]
    public StudioPackageFamily? Family { get; init; }

    [JsonPropertyName("envelope")]
    public StudioPackageEnvelope? Envelope { get; init; }

    [JsonPropertyName("validation")]
    public StudioValidationSummary? Validation { get; init; }

    [JsonPropertyName("baseVersionId")]
    public Guid? BaseVersionId { get; init; }

    [JsonPropertyName("generation")]
    public long? Generation { get; init; }

    [JsonPropertyName("createdBy")]
    public string? CreatedBy { get; init; }

    [JsonPropertyName("updatedBy")]
    public string? UpdatedBy { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    public static McpStudioDraftMutationOutput From(
        StudioDraftMutationReceipt<StudioPackageDraft> receipt) => new()
        {
            Operation = receipt.Operation,
            DraftId = receipt.Value?.DraftId,
            ItemId = receipt.Value?.ItemId,
            PackageKey = receipt.Value?.PackageKey,
            WorkspaceId = receipt.Value?.WorkspaceId,
            OwnerId = receipt.Value?.OwnerId,
            Family = receipt.Value?.Family,
            Envelope = receipt.Value?.Envelope,
            Validation = receipt.Value?.Validation,
            BaseVersionId = receipt.Value?.BaseVersionId,
            Generation = receipt.Value?.Generation,
            CreatedBy = receipt.Value?.CreatedBy,
            UpdatedBy = receipt.Value?.UpdatedBy,
            CreatedAt = receipt.Value?.CreatedAt,
            UpdatedAt = receipt.Value?.UpdatedAt,
        };
}

// -----------------------------------------------------------------------
// honua_studio_get_draft
// -----------------------------------------------------------------------

internal sealed class McpStudioDraftIdArgument
{
    [JsonPropertyName("draftId")]
    public Guid? DraftId { get; set; }
}

// -----------------------------------------------------------------------
// honua_studio_update_draft
// -----------------------------------------------------------------------

// Publication intent is not accepted here, so this updater cannot smuggle a publish signal.
internal sealed class McpStudioUpdateDraftArgument
{
    [JsonPropertyName("draftId")]
    public Guid? DraftId { get; set; }

    [JsonPropertyName("generation")]
    public long? Generation { get; set; }

    [JsonPropertyName("packageKey")]
    public string? PackageKey { get; set; }

    [JsonPropertyName("schemaVersion")]
    public string? SchemaVersion { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; set; }

    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; set; }

    [JsonPropertyName("body")]
    public JsonElement? Body { get; set; }
}

// -----------------------------------------------------------------------
// Composition tools — shared nested shapes
// -----------------------------------------------------------------------

internal sealed class McpStudioLayerInput
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("sourceId")]
    public string? SourceId { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("visible")]
    public bool? Visible { get; set; }

    [JsonPropertyName("styleRef")]
    public string? StyleRef { get; set; }

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; set; }
}

internal sealed class McpStudioViewInput
{
    [JsonPropertyName("bbox")]
    public IReadOnlyList<double>? Bbox { get; set; }

    [JsonPropertyName("center")]
    public IReadOnlyList<double>? Center { get; set; }

    [JsonPropertyName("zoom")]
    public double? Zoom { get; set; }

    [JsonPropertyName("pitch")]
    public double? Pitch { get; set; }

    [JsonPropertyName("bearing")]
    public double? Bearing { get; set; }

    [JsonPropertyName("crs")]
    public string? Crs { get; set; }
}

internal sealed class McpStudioWidgetInput
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("sourceId")]
    public string? SourceId { get; set; }

    [JsonPropertyName("config")]
    public JsonElement? Config { get; set; }
}

// -----------------------------------------------------------------------
// honua_studio_add_layer
// -----------------------------------------------------------------------

internal sealed class McpStudioAddLayerArgument
{
    [JsonPropertyName("draftId")]
    public Guid? DraftId { get; set; }

    [JsonPropertyName("generation")]
    public long? Generation { get; set; }

    [JsonPropertyName("layer")]
    public McpStudioLayerInput? Layer { get; set; }

    [JsonPropertyName("beforeId")]
    public string? BeforeId { get; set; }
}

// -----------------------------------------------------------------------
// honua_studio_remove_layer
// -----------------------------------------------------------------------

internal sealed class McpStudioRemoveLayerArgument
{
    [JsonPropertyName("draftId")]
    public Guid? DraftId { get; set; }

    [JsonPropertyName("generation")]
    public long? Generation { get; set; }

    [JsonPropertyName("layerId")]
    public string? LayerId { get; set; }
}

// -----------------------------------------------------------------------
// honua_studio_set_layer_style
// -----------------------------------------------------------------------

internal sealed class McpStudioSetLayerStyleArgument
{
    [JsonPropertyName("draftId")]
    public Guid? DraftId { get; set; }

    [JsonPropertyName("generation")]
    public long? Generation { get; set; }

    [JsonPropertyName("layerId")]
    public string? LayerId { get; set; }

    [JsonPropertyName("styleRef")]
    public string? StyleRef { get; set; }
}

// -----------------------------------------------------------------------
// honua_studio_set_layer_visibility
// -----------------------------------------------------------------------

internal sealed class McpStudioSetLayerVisibilityArgument
{
    [JsonPropertyName("draftId")]
    public Guid? DraftId { get; set; }

    [JsonPropertyName("generation")]
    public long? Generation { get; set; }

    [JsonPropertyName("layerId")]
    public string? LayerId { get; set; }

    // An omitted visibility is rejected rather than defaulted, so a layer is never toggled into a state the caller did not ask for.
    [JsonPropertyName("visible")]
    public bool? Visible { get; set; }
}

// -----------------------------------------------------------------------
// honua_studio_set_view
// -----------------------------------------------------------------------

internal sealed class McpStudioSetViewArgument
{
    [JsonPropertyName("draftId")]
    public Guid? DraftId { get; set; }

    [JsonPropertyName("generation")]
    public long? Generation { get; set; }

    [JsonPropertyName("view")]
    public McpStudioViewInput? View { get; set; }
}

// -----------------------------------------------------------------------
// honua_studio_add_widget
// -----------------------------------------------------------------------

internal sealed class McpStudioAddWidgetArgument
{
    [JsonPropertyName("draftId")]
    public Guid? DraftId { get; set; }

    [JsonPropertyName("generation")]
    public long? Generation { get; set; }

    [JsonPropertyName("widget")]
    public McpStudioWidgetInput? Widget { get; set; }
}

// -----------------------------------------------------------------------
// honua_studio_remove_widget
// -----------------------------------------------------------------------

internal sealed class McpStudioRemoveWidgetArgument
{
    [JsonPropertyName("draftId")]
    public Guid? DraftId { get; set; }

    [JsonPropertyName("generation")]
    public long? Generation { get; set; }

    [JsonPropertyName("widgetId")]
    public string? WidgetId { get; set; }
}

// -----------------------------------------------------------------------
// honua_studio_bind_interaction / honua_studio_remove_interaction
// (geospatial-mcp ADR-0030 bind_interaction / remove_interaction)
// -----------------------------------------------------------------------

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class McpStudioInteractionEventInput
{
    [JsonPropertyName("ref")]
    public string? Ref { get; set; }

    [JsonPropertyName("event")]
    public string? Event { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class McpStudioInteractionActionInput
{
    [JsonPropertyName("ref")]
    public string? Ref { get; set; }

    [JsonPropertyName("verb")]
    public string? Verb { get; set; }

    [JsonPropertyName("args")]
    public JsonElement Args { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class McpStudioInteractionInput
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("on")]
    public McpStudioInteractionEventInput? On { get; set; }

    [JsonPropertyName("do")]
    public McpStudioInteractionActionInput? Do { get; set; }

    [JsonPropertyName("disabled")]
    public JsonElement Disabled { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class McpStudioBindInteractionArgument
{
    [JsonPropertyName("draftId")]
    public Guid? DraftId { get; set; }

    [JsonPropertyName("generation")]
    public long? Generation { get; set; }

    [JsonPropertyName("interaction")]
    public McpStudioInteractionInput? Interaction { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class McpStudioRemoveInteractionArgument
{
    [JsonPropertyName("draftId")]
    public Guid? DraftId { get; set; }

    [JsonPropertyName("generation")]
    public long? Generation { get; set; }

    [JsonPropertyName("interactionId")]
    public string? InteractionId { get; set; }
}

// -----------------------------------------------------------------------
// honua_studio_add_control / honua_studio_remove_control
// (geospatial-mcp ADR-0031 add_control / remove_control)
// -----------------------------------------------------------------------

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class McpStudioControlInput
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("sourceId")]
    public string? SourceId { get; set; }

    [JsonPropertyName("config")]
    public JsonElement Config { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class McpStudioAddControlArgument
{
    [JsonPropertyName("draftId")]
    public Guid? DraftId { get; set; }

    [JsonPropertyName("generation")]
    public long? Generation { get; set; }

    [JsonPropertyName("control")]
    public McpStudioControlInput? Control { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class McpStudioRemoveControlArgument
{
    [JsonPropertyName("draftId")]
    public Guid? DraftId { get; set; }

    [JsonPropertyName("generation")]
    public long? Generation { get; set; }

    [JsonPropertyName("controlId")]
    public string? ControlId { get; set; }

    // Interactions that still reference the control fail the call unless cascade is set, so a dangling binding is never kept.
    [JsonPropertyName("cascadeInteractions")]
    public bool? CascadeInteractions { get; set; }
}

// -----------------------------------------------------------------------
// honua_studio_propose_publication
// -----------------------------------------------------------------------

internal sealed class McpStudioProposePublicationArgument
{
    [JsonPropertyName("itemId")]
    public Guid? ItemId { get; set; }

    [JsonPropertyName("versionId")]
    public Guid? VersionId { get; set; }

    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; set; }

    [JsonPropertyName("route")]
    public string? Route { get; set; }

    [JsonPropertyName("visibility")]
    public string? Visibility { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

internal sealed class McpStudioProposePublicationOutput
{
    [JsonPropertyName("operation")]
    public required OperationHandle Operation { get; init; }

    [JsonPropertyName("operationInstanceId")]
    public required string OperationInstanceId { get; init; }

    [JsonPropertyName("proposalId")]
    public string? ProposalId { get; init; }

    [JsonPropertyName("proposalUri")]
    public string? ProposalUri { get; init; }

    [JsonPropertyName("auditId")]
    public required string AuditId { get; init; }

    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }

    [JsonPropertyName("idempotencyIdentity")]
    public required string IdempotencyIdentity { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("humanConfirmationRequired")]
    public bool HumanConfirmationRequired { get; init; } = true;

    [JsonPropertyName("shareUrl")]
    public string? ShareUrl { get; init; }

    [JsonPropertyName("deploymentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeploymentId { get; init; }

    [JsonPropertyName("deploymentResourceUri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeploymentResourceUri { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

internal sealed record McpStudioSaveVersionArgument
{
    [JsonPropertyName("draftId")] public required Guid DraftId { get; init; }
    [JsonPropertyName("generation")] public required long Generation { get; init; }
    [JsonPropertyName("changeNote")] public string? ChangeNote { get; init; }
}

internal sealed record McpStudioReopenVersionArgument
{
    [JsonPropertyName("itemId")] public required Guid ItemId { get; init; }
    [JsonPropertyName("versionId")] public required Guid VersionId { get; init; }
}

internal sealed record McpStudioGetVersionArgument
{
    [JsonPropertyName("itemId")] public required Guid ItemId { get; init; }
    [JsonPropertyName("versionId")] public required Guid VersionId { get; init; }
}

internal sealed record McpStudioGetVersionOutput
{
    [JsonPropertyName("versionId")] public required Guid VersionId { get; init; }
    [JsonPropertyName("contentHash")] public required string ContentHash { get; init; }
}

internal sealed record McpStudioSaveVersionOutput
{
    [JsonPropertyName("operation")] public required OperationHandle Operation { get; init; }
    [JsonPropertyName("version")] public StudioContentVersion? Version { get; init; }
}
