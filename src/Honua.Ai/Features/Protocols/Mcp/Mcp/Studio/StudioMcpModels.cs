// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Ai.Protocols.Mcp.Studio;

// -----------------------------------------------------------------------
// honua_studio_create_draft
// -----------------------------------------------------------------------

/// <summary>Arguments for <c>honua_studio_create_draft</c>.</summary>
internal sealed class McpStudioCreateDraftArgument
{
    [JsonPropertyName("packageKey")]
    public string? PackageKey { get; set; }

    /// <summary>Package family (query, analysis, map, dashboard, report, form, app, workflow, gp, etl).</summary>
    [JsonPropertyName("family")]
    public string? Family { get; set; }

    [JsonPropertyName("schemaVersion")]
    public string? SchemaVersion { get; set; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; set; }

    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; set; }

    /// <summary>
    /// Optional initial composition body (for <c>map</c>/<c>app</c> families, a
    /// <see cref="StudioCompositionBody"/>-shaped payload; other families carry
    /// their own family-specific JSON). Omit to start with an empty body.
    /// </summary>
    [JsonPropertyName("body")]
    public JsonElement? Body { get; set; }

    /// <summary>Existing content item id, to add a new draft under an existing item.</summary>
    [JsonPropertyName("itemId")]
    public Guid? ItemId { get; set; }

    /// <summary>Immutable version this draft was reopened from, when applicable.</summary>
    [JsonPropertyName("baseVersionId")]
    public Guid? BaseVersionId { get; set; }
}

// -----------------------------------------------------------------------
// honua_studio_get_draft
// -----------------------------------------------------------------------

/// <summary>Arguments for <c>honua_studio_get_draft</c>.</summary>
internal sealed class McpStudioDraftIdArgument
{
    [JsonPropertyName("draftId")]
    public Guid? DraftId { get; set; }
}

// -----------------------------------------------------------------------
// honua_studio_update_draft
// -----------------------------------------------------------------------

/// <summary>
/// Arguments for <c>honua_studio_update_draft</c>. A bounded, whole-envelope
/// replace scoped deliberately narrower than the REST admin surface: it
/// carries the fields an agent legitimately edits (key, workspace/owner,
/// schema version, format, body) and does NOT accept <c>bindings</c>,
/// <c>dependencies</c>, <c>provenance</c>, or <c>publicationIntent</c> — those
/// stay lifecycle-service-owned or, for publication intent, are recorded only
/// through <c>honua_studio_propose_publication</c> so this general-purpose
/// updater can never smuggle a publish signal.
/// </summary>
internal sealed class McpStudioUpdateDraftArgument
{
    [JsonPropertyName("draftId")]
    public Guid? DraftId { get; set; }

    /// <summary>Expected current draft generation (optimistic concurrency).</summary>
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

    /// <summary>Replacement composition/family body. Omit to leave the existing body unchanged.</summary>
    [JsonPropertyName("body")]
    public JsonElement? Body { get; set; }
}

// -----------------------------------------------------------------------
// Composition tools — shared nested shapes
// -----------------------------------------------------------------------

/// <summary>
/// Layer input shape shared by <c>honua_studio_add_layer</c>. Mirrors the
/// honua-sdk-js agent-tools <c>addLayer</c> layer specification.
/// </summary>
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

/// <summary>View input shape shared by <c>honua_studio_set_view</c>. Mirrors the SDK agent-tools viewport shape.</summary>
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

/// <summary>Widget input shape shared by <c>honua_studio_add_widget</c>.</summary>
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

/// <summary>Arguments for <c>honua_studio_add_layer</c>.</summary>
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

/// <summary>Arguments for <c>honua_studio_remove_layer</c>.</summary>
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

/// <summary>Arguments for <c>honua_studio_set_layer_style</c>.</summary>
internal sealed class McpStudioSetLayerStyleArgument
{
    [JsonPropertyName("draftId")]
    public Guid? DraftId { get; set; }

    [JsonPropertyName("generation")]
    public long? Generation { get; set; }

    [JsonPropertyName("layerId")]
    public string? LayerId { get; set; }

    /// <summary>Style reference (catalog styleId or inline style key). Omit/null clears the binding.</summary>
    [JsonPropertyName("styleRef")]
    public string? StyleRef { get; set; }
}

// -----------------------------------------------------------------------
// honua_studio_set_view
// -----------------------------------------------------------------------

/// <summary>Arguments for <c>honua_studio_set_view</c>.</summary>
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

/// <summary>Arguments for <c>honua_studio_add_widget</c>.</summary>
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

/// <summary>Arguments for <c>honua_studio_remove_widget</c>.</summary>
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
// honua_studio_propose_publication
// -----------------------------------------------------------------------

/// <summary>
/// Arguments for <c>honua_studio_propose_publication</c>. Records publication
/// intent ON THE DRAFT ONLY — it never calls the publish-request/rollback
/// lifecycle endpoints and never moves a current/published pointer (REQ-003,
/// REQ-009: publish/share/embed execution stays a human-confirmed action
/// outside the agent tool surface).
/// </summary>
internal sealed class McpStudioProposePublicationArgument
{
    [JsonPropertyName("draftId")]
    public Guid? DraftId { get; set; }

    [JsonPropertyName("generation")]
    public long? Generation { get; set; }

    [JsonPropertyName("route")]
    public string? Route { get; set; }

    [JsonPropertyName("visibility")]
    public string? Visibility { get; set; }

    [JsonPropertyName("embed")]
    public bool? Embed { get; set; }

    [JsonPropertyName("service")]
    public string? Service { get; set; }

    [JsonPropertyName("schedule")]
    public string? Schedule { get; set; }

    [JsonPropertyName("job")]
    public string? Job { get; set; }

    /// <summary>Human-readable rationale recorded alongside the intent for reviewer context.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

/// <summary>
/// Output for <c>honua_studio_propose_publication</c>. Carries the updated
/// draft (with the recorded <see cref="StudioPackageEnvelope.PublicationIntent"/>)
/// plus an explicit, structural confirmation that only intent was recorded.
/// </summary>
internal sealed class McpStudioProposePublicationOutput
{
    [JsonPropertyName("draft")]
    public required StudioPackageDraft Draft { get; init; }

    /// <summary>Always <see langword="true"/> on success: intent was recorded on the draft.</summary>
    [JsonPropertyName("recorded")]
    public bool Recorded { get; init; } = true;

    /// <summary>
    /// Always <see langword="true"/>: publish/share/embed execution requires a
    /// separate human-confirmed action outside this tool surface.
    /// </summary>
    [JsonPropertyName("humanConfirmationRequired")]
    public bool HumanConfirmationRequired { get; init; } = true;

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}
