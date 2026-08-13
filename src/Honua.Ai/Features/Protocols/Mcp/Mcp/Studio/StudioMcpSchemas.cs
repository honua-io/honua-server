// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Ai.Protocols.Mcp.Studio;

/// <summary>
/// JSON-schema documents advertised in <c>tools/list</c> for the Studio draft
/// lifecycle and composition-mutation tools (honua-server#3002). Schemas are
/// immutable <see cref="JsonElement"/> values parsed at type-load time,
/// mirroring <see cref="Honua.Ai.Protocols.Mcp.MapTools.MapToolSchemas"/>.
/// Every mutating tool requires <c>draftId</c> + <c>generation</c> so the
/// server can enforce optimistic concurrency (a stale-generation call surfaces
/// a typed <c>failed_precondition</c> error rather than silently clobbering a
/// concurrent edit).
/// </summary>
internal static class StudioMcpSchemas
{
    /// <summary>Maximum accepted length for free-text fields (packageKey, ids, titles, notes).</summary>
    public const int MaxShortTextLength = 200;

    /// <summary>Maximum accepted length for note/rationale free text.</summary>
    public const int MaxNoteLength = 2000;

    private const string FamilyEnumJson =
        """["query", "analysis", "map", "dashboard", "report", "form", "app", "workflow", "gp", "etl"]""";

    /// <summary>
    /// The ADR-0030 component-reference grammar as a JSON-schema pattern
    /// (<c>map | layer:{id} | widget:{id} | control:{id}</c>), rendered from the shared
    /// vocabulary's prefixes. Escaped for embedding in a JSON string literal.
    /// </summary>
    private static readonly string ComponentRefPattern =
        $"^({StudioInteractionVocabulary.MapRef}"
        + $"|{StudioInteractionVocabulary.LayerRefPrefix}.+"
        + $"|{StudioInteractionVocabulary.WidgetRefPrefix}.+"
        + $"|{StudioInteractionVocabulary.ControlRefPrefix}.+)$";

    private static readonly string EventNameEnumJson = EnumJson(StudioInteractionVocabulary.EventNames);

    private static readonly string ActionVerbEnumJson = EnumJson(StudioInteractionVocabulary.ActionVerbs);

    private const string PackageKeyPropertyJson = """
        {
          "type": "string",
          "minLength": 1,
          "maxLength": 200,
          "pattern": "^[A-Za-z0-9_.-]+$",
          "description": "Machine-friendly package key (letters, numbers, dash, underscore, dot only)."
        }
        """;

    private const string LayerInputSchemaJson = """
        {
          "type": "object",
          "required": ["id"],
          "additionalProperties": false,
          "properties": {
            "id": { "type": "string", "minLength": 1, "maxLength": 200, "description": "Stable layer id, unique within the composition." },
            "sourceId": { "type": "string", "maxLength": 200, "description": "Bound source identifier." },
            "type": { "type": "string", "maxLength": 100, "description": "Layer type (e.g. fill, circle, line, raster)." },
            "title": { "type": "string", "maxLength": 200, "description": "Display title." },
            "visible": { "type": "boolean", "default": true, "description": "Layer visibility." },
            "styleRef": { "type": "string", "maxLength": 200, "description": "Bound style reference (catalog styleId or inline style key)." },
            "metadata": { "type": "object", "description": "Additional layer metadata." }
          }
        }
        """;

    // Arity and zoom/pitch ranges come from the shared Studio view contract
    // (StudioCompositionViewBounds) rather than being restated here: the live-collaboration op-log
    // validator enforces the SAME constants, so the advertised schema and the enforced admission
    // rules cannot drift apart (honua-server#2999 review).
    private static readonly string ViewInputSchemaJson = $$"""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "bbox": { "type": "array", "minItems": {{StudioCompositionViewBounds.BboxOrdinateCount}}, "maxItems": {{StudioCompositionViewBounds.BboxOrdinateCount}}, "items": { "type": "number" }, "description": "Viewport bounding box [minX, minY, maxX, maxY]." },
            "center": { "type": "array", "minItems": {{StudioCompositionViewBounds.CenterOrdinateCount}}, "maxItems": {{StudioCompositionViewBounds.CenterOrdinateCount}}, "items": { "type": "number" }, "description": "Viewport center [x, y]." },
            "zoom": { "type": "number", "minimum": {{Number(StudioCompositionViewBounds.MinZoom)}}, "maximum": {{Number(StudioCompositionViewBounds.MaxZoom)}}, "description": "Map zoom." },
            "pitch": { "type": "number", "minimum": {{Number(StudioCompositionViewBounds.MinPitch)}}, "maximum": {{Number(StudioCompositionViewBounds.MaxPitch)}}, "description": "Map pitch." },
            "bearing": { "type": "number", "description": "Map bearing." },
            "crs": { "type": "string", "maxLength": 100, "description": "Coordinate reference system (e.g. EPSG:4326)." }
          }
        }
        """;

    private const string WidgetInputSchemaJson = """
        {
          "type": "object",
          "required": ["id", "kind"],
          "additionalProperties": false,
          "properties": {
            "id": { "type": "string", "minLength": 1, "maxLength": 200, "description": "Stable widget id, unique within the composition." },
            "kind": { "type": "string", "minLength": 1, "maxLength": 100, "description": "Widget kind (e.g. table, chart, legend, filter)." },
            "title": { "type": "string", "maxLength": 200, "description": "Display title." },
            "sourceId": { "type": "string", "maxLength": 200, "description": "Bound source identifier." },
            "config": { "type": "object", "description": "Widget-specific bounded configuration." }
          }
        }
        """;

    // The closed event/verb sets, the component-reference grammar and the fan-out cap come
    // from the shared ADR-0030 vocabulary (StudioInteractionVocabulary) rather than being
    // restated here, so the advertised schema, the composition-editor admission gate and
    // the StudioPackageValidator document gate cannot drift apart — the same anti-drift
    // move ViewInputSchemaJson made for StudioCompositionViewBounds.
    private static readonly string InteractionInputSchemaJson = $$"""
        {
          "type": "object",
          "required": ["id", "on", "do"],
          "additionalProperties": false,
          "properties": {
            "id": { "type": "string", "minLength": 1, "maxLength": {{StudioInteractionVocabulary.MaxInteractionIdLength}}, "pattern": "\\S", "description": "Stable interaction id, unique within the composition. Binding an existing id replaces that interaction." },
            "on": {
              "type": "object",
              "required": ["ref", "event"],
              "additionalProperties": false,
              "properties": {
                "ref": { "type": "string", "pattern": "{{ComponentRefPattern}}", "description": "Event-source component declared in the same document: 'map', 'layer:{id}' or 'widget:{id}'. 'control:{id}' is grammatical but never resolves — Studio composition documents declare no controls collection." },
                "event": { "type": "string", "enum": {{EventNameEnumJson}}, "description": "User-gesture event: featureSelect/featureHover (layers), selection (widgets), change (controls), viewportChange (map)." }
              },
              "description": "The user-gesture event source. At most {{StudioInteractionVocabulary.MaxInteractionsPerEventSource}} interactions may share the same (ref, event) pair."
            },
            "do": {
              "type": "object",
              "required": ["ref", "verb"],
              "additionalProperties": false,
              "properties": {
                "ref": { "type": "string", "pattern": "{{ComponentRefPattern}}", "description": "Action-target component declared in the same document." },
                "verb": { "type": "string", "enum": {{ActionVerbEnumJson}}, "description": "Presentation/exploration verb. Interactions never mutate source records." },
                "args": { "type": "object", "description": "Static JSON arguments. A string value beginning with '$event.' is substituted at dispatch time from the event payload; there is no expression language." }
              },
              "description": "The action. Actions never emit events, so bindings cannot cascade."
            },
            "disabled": { "type": "boolean", "default": false, "description": "Authored-but-inactive binding: retained in the document, skipped at dispatch." }
          }
        }
        """;

    private static readonly string BindInteractionArgumentSchemaJson = $$"""
        {
          "type": "object",
          "required": ["draftId", "generation", "interaction"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string", "format": "uuid", "description": "Studio package draft id (map/app family)." },
            "generation": { "type": "integer", "minimum": 1, "description": "Expected current draft generation (optimistic concurrency)." },
            "interaction": {{InteractionInputSchemaJson}}
          }
        }
        """;

    private const string RemoveInteractionArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["draftId", "generation", "interactionId"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string", "format": "uuid", "description": "Studio package draft id (map/app family)." },
            "generation": { "type": "integer", "minimum": 1, "description": "Expected current draft generation (optimistic concurrency)." },
            "interactionId": { "type": "string", "minLength": 1, "maxLength": 200, "pattern": "\\S", "description": "Id of the interaction to remove. Removing an unknown id is an error, not a no-op." }
          }
        }
        """;

    private static readonly string CreateDraftArgumentSchemaJson = $$"""
        {
          "type": "object",
          "required": ["packageKey", "family", "schemaVersion"],
          "additionalProperties": false,
          "properties": {
            "packageKey": {{PackageKeyPropertyJson}},
            "family": {
              "type": "string",
              "enum": {{FamilyEnumJson}},
              "description": "Studio package family."
            },
            "schemaVersion": { "type": "string", "minLength": 1, "maxLength": 50, "description": "Envelope schema version for the family." },
            "workspaceId": { "type": "string", "maxLength": 200, "description": "Workspace identifier." },
            "ownerId": { "type": "string", "maxLength": 200, "description": "Owner principal identifier." },
            "body": { "type": "object", "description": "Optional initial composition/family body. Omit to start empty." },
            "itemId": { "type": "string", "format": "uuid", "description": "Existing content item id, to add a draft under an existing item." },
            "baseVersionId": { "type": "string", "format": "uuid", "description": "Immutable version this draft was reopened from, when applicable." }
          }
        }
        """;

    private const string DraftIdArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["draftId"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string", "format": "uuid", "description": "Studio package draft id." }
          }
        }
        """;

    private static readonly string UpdateDraftArgumentSchemaJson = $$"""
        {
          "type": "object",
          "required": ["draftId", "generation", "packageKey", "schemaVersion"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string", "format": "uuid", "description": "Studio package draft id." },
            "generation": { "type": "integer", "minimum": 1, "description": "Expected current draft generation (optimistic concurrency). A mismatch surfaces a failed_precondition error; re-fetch with honua_studio_get_draft and retry." },
            "packageKey": {{PackageKeyPropertyJson}},
            "schemaVersion": { "type": "string", "minLength": 1, "maxLength": 50, "description": "Envelope schema version for the family." },
            "format": { "type": "string", "maxLength": 100, "description": "Family-specific package format." },
            "workspaceId": { "type": "string", "maxLength": 200, "description": "Workspace identifier." },
            "ownerId": { "type": "string", "maxLength": 200, "description": "Owner principal identifier." },
            "body": { "type": "object", "description": "Replacement composition/family body. Omit to leave the existing body unchanged." }
          }
        }
        """;

    private static readonly string AddLayerArgumentSchemaJson = $$"""
        {
          "type": "object",
          "required": ["draftId", "generation", "layer"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string", "format": "uuid", "description": "Studio package draft id (map/app family)." },
            "generation": { "type": "integer", "minimum": 1, "description": "Expected current draft generation (optimistic concurrency)." },
            "layer": {{LayerInputSchemaJson}},
            "beforeId": { "type": "string", "maxLength": 200, "description": "Optional layer id before which to insert the new layer; appended when omitted or unmatched." }
          }
        }
        """;

    private const string RemoveLayerArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["draftId", "generation", "layerId"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string", "format": "uuid", "description": "Studio package draft id (map/app family)." },
            "generation": { "type": "integer", "minimum": 1, "description": "Expected current draft generation (optimistic concurrency)." },
            "layerId": { "type": "string", "minLength": 1, "maxLength": 200, "description": "Id of the layer to remove." }
          }
        }
        """;

    private const string SetLayerStyleArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["draftId", "generation", "layerId"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string", "format": "uuid", "description": "Studio package draft id (map/app family)." },
            "generation": { "type": "integer", "minimum": 1, "description": "Expected current draft generation (optimistic concurrency)." },
            "layerId": { "type": "string", "minLength": 1, "maxLength": 200, "description": "Id of the layer to style." },
            "styleRef": { "type": ["string", "null"], "maxLength": 200, "description": "Style reference (catalog styleId or inline style key). Omit or set null to clear the binding." }
          }
        }
        """;

    private const string SetLayerVisibilityArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["draftId", "generation", "layerId", "visible"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string", "format": "uuid", "description": "Studio package draft id (map/app family)." },
            "generation": { "type": "integer", "minimum": 1, "description": "Expected current draft generation (optimistic concurrency)." },
            "layerId": { "type": "string", "minLength": 1, "maxLength": 200, "description": "Id of the layer to show or hide." },
            "visible": { "type": "boolean", "description": "Whether the layer is visible in the composition." }
          }
        }
        """;

    private static readonly string SetViewArgumentSchemaJson = $$"""
        {
          "type": "object",
          "required": ["draftId", "generation", "view"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string", "format": "uuid", "description": "Studio package draft id (map/app family)." },
            "generation": { "type": "integer", "minimum": 1, "description": "Expected current draft generation (optimistic concurrency)." },
            "view": {{ViewInputSchemaJson}}
          }
        }
        """;

    private static readonly string AddWidgetArgumentSchemaJson = $$"""
        {
          "type": "object",
          "required": ["draftId", "generation", "widget"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string", "format": "uuid", "description": "Studio package draft id (app family)." },
            "generation": { "type": "integer", "minimum": 1, "description": "Expected current draft generation (optimistic concurrency)." },
            "widget": {{WidgetInputSchemaJson}}
          }
        }
        """;

    private const string RemoveWidgetArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["draftId", "generation", "widgetId"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string", "format": "uuid", "description": "Studio package draft id (app family)." },
            "generation": { "type": "integer", "minimum": 1, "description": "Expected current draft generation (optimistic concurrency)." },
            "widgetId": { "type": "string", "minLength": 1, "maxLength": 200, "description": "Id of the widget to remove." }
          }
        }
        """;

    private const string ProposePublicationArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["draftId", "generation"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string", "format": "uuid", "description": "Studio package draft id." },
            "generation": { "type": "integer", "minimum": 1, "description": "Expected current draft generation (optimistic concurrency)." },
            "route": { "type": "string", "maxLength": 200, "description": "Proposed target route key." },
            "visibility": { "type": "string", "maxLength": 100, "description": "Proposed visibility target." },
            "embed": { "type": "boolean", "description": "Whether embedding should be enabled if published." },
            "service": { "type": "string", "maxLength": 200, "description": "Proposed service publication hint." },
            "schedule": { "type": "string", "maxLength": 200, "description": "Proposed schedule expression or key." },
            "job": { "type": "string", "maxLength": 200, "description": "Proposed job publication hint." },
            "note": { "type": "string", "maxLength": 2000, "description": "Human-readable rationale recorded for reviewer context." }
          }
        }
        """;

    /// <summary>Schema for <see cref="McpStudioCreateDraftArgument"/>.</summary>
    public static readonly JsonElement CreateDraftArgumentSchema = Parse(CreateDraftArgumentSchemaJson);

    /// <summary>Schema for <see cref="McpStudioDraftIdArgument"/> (get, validate, preview).</summary>
    public static readonly JsonElement DraftIdArgumentSchema = Parse(DraftIdArgumentSchemaJson);

    /// <summary>Schema for <see cref="McpStudioUpdateDraftArgument"/>.</summary>
    public static readonly JsonElement UpdateDraftArgumentSchema = Parse(UpdateDraftArgumentSchemaJson);

    /// <summary>Schema for <see cref="McpStudioAddLayerArgument"/>.</summary>
    public static readonly JsonElement AddLayerArgumentSchema = Parse(AddLayerArgumentSchemaJson);

    /// <summary>Schema for <see cref="McpStudioRemoveLayerArgument"/>.</summary>
    public static readonly JsonElement RemoveLayerArgumentSchema = Parse(RemoveLayerArgumentSchemaJson);

    /// <summary>Schema for <see cref="McpStudioSetLayerStyleArgument"/>.</summary>
    public static readonly JsonElement SetLayerStyleArgumentSchema = Parse(SetLayerStyleArgumentSchemaJson);

    /// <summary>Schema for <see cref="McpStudioSetLayerVisibilityArgument"/>.</summary>
    public static readonly JsonElement SetLayerVisibilityArgumentSchema = Parse(SetLayerVisibilityArgumentSchemaJson);

    /// <summary>Schema for <see cref="McpStudioSetViewArgument"/>.</summary>
    public static readonly JsonElement SetViewArgumentSchema = Parse(SetViewArgumentSchemaJson);

    /// <summary>Schema for <see cref="McpStudioAddWidgetArgument"/>.</summary>
    public static readonly JsonElement AddWidgetArgumentSchema = Parse(AddWidgetArgumentSchemaJson);

    /// <summary>Schema for <see cref="McpStudioRemoveWidgetArgument"/>.</summary>
    public static readonly JsonElement RemoveWidgetArgumentSchema = Parse(RemoveWidgetArgumentSchemaJson);

    /// <summary>Schema for <see cref="McpStudioBindInteractionArgument"/>.</summary>
    public static readonly JsonElement BindInteractionArgumentSchema = Parse(BindInteractionArgumentSchemaJson);

    /// <summary>Schema for <see cref="McpStudioRemoveInteractionArgument"/>.</summary>
    public static readonly JsonElement RemoveInteractionArgumentSchema = Parse(RemoveInteractionArgumentSchemaJson);

    /// <summary>Schema for <see cref="McpStudioProposePublicationArgument"/>.</summary>
    public static readonly JsonElement ProposePublicationArgumentSchema = Parse(ProposePublicationArgumentSchemaJson);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Renders a shared numeric bound as a culture-invariant JSON number literal, so the emitted
    /// schema is identical on every host locale.
    /// </summary>
    private static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Renders a shared closed vocabulary as a JSON string-array literal, so the advertised
    /// enum is generated from the domain contract rather than restated in the schema text.
    /// </summary>
    private static string EnumJson(IReadOnlyList<string> values) =>
        "[" + string.Join(", ", values.Select(value => "\"" + value + "\"")) + "]";
}
