// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
    public static readonly JsonElement ListItemsArgumentSchema = Parse("""
        {"type":"object","additionalProperties":false,"properties":{
        "q":{"type":"string","maxLength":200},"cursor":{"type":"string","maxLength":2000},
        "limit":{"type":"integer","minimum":1,"maximum":100,"default":25}}}
        """);

    public static readonly JsonElement ListVersionsArgumentSchema = Parse("""
        {"type":"object","required":["itemId"],"additionalProperties":false,"properties":{
        "itemId":{"type":"string","format":"uuid","description":"itemId from honua_studio_list_items."},"beforeVersionNumber":{"type":"integer","minimum":1},
        "limit":{"type":"integer","minimum":1,"maximum":100,"default":25}}}
        """);

    public static readonly JsonElement SaveVersionArgumentSchema = Parse("""
        {"type":"object","required":["draftId","generation"],"additionalProperties":false,"properties":{
          "draftId":{"type":"string","format":"uuid","description":"From honua_studio_create_draft or honua_studio_get_draft."},
          "generation":{"type":"integer","minimum":1,"description":"honua_studio_get_draft; refresh after each edit."},
          "changeNote":{"type":["string","null"],"maxLength":2000}}}
        """);
    public static readonly JsonElement ReopenVersionArgumentSchema = Parse("""
        {"type":"object","required":["itemId","versionId"],"additionalProperties":false,"properties":{
          "itemId":{"type":"string","format":"uuid","description":"version.itemId from honua_studio_save_version."},
          "versionId":{"type":"string","format":"uuid","description":"version.versionId from honua_studio_save_version."}}}
        """);

    public static readonly JsonElement GetVersionArgumentSchema = Parse("""
        {"type":"object","required":["itemId","versionId"],"additionalProperties":false,"properties":{
          "itemId":{"type":"string","format":"uuid","description":"version.itemId from honua_studio_save_version."},
          "versionId":{"type":"string","format":"uuid","description":"version.versionId from honua_studio_save_version."}}}
        """);

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

    private static readonly string ControlKindEnumJson = EnumJson(StudioInteractionVocabulary.ControlKinds);

    private const string PackageKeyPropertyJson = """
        {
          "type": "string",
          "minLength": 1,
          "maxLength": 200,
          "pattern": "^[A-Za-z0-9_.-]+$",
          "description": "Caller-supplied package key."
        }
        """;

    private static readonly string LayerInputSchemaJson = $$"""
        {
          "type": "object",
          "description": "Caller-authored layer; sourceId from honua_list_layers.studioSourceId.",
          "required": ["id"],
          "additionalProperties": false,
          "properties": {
            "id": { "type": "string", "minLength": 1, "maxLength": 200, "description": "Unique composition layer id." },
            "sourceId": { "type": "string", "maxLength": {{StudioCatalogSourceIdentity.MaxLength}}, "description": "Catalog source: studioSourceId from honua_list_layers or honua_describe_layer, distinct from layer.id. Portable bindings and metadata.honua.source overrides are separate." },
            "type": { "type": "string", "maxLength": 100, "description": "Layer type: fill, circle, line, raster, etc." },
            "title": { "type": "string", "maxLength": 200, "description": "Display title." },
            "visible": { "type": "boolean", "default": true, "description": "Layer visibility." },
            "styleRef": { "type": "string", "maxLength": 200, "description": "Style: catalog styleId or inline key." },
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
          "description": "Caller-selected camera; inspect with honua_studio_get_draft.",
          "additionalProperties": false,
          "properties": {
            "bbox": { "type": "array", "minItems": {{StudioCompositionViewBounds.BboxOrdinateCount}}, "maxItems": {{StudioCompositionViewBounds.BboxOrdinateCount}}, "items": { "type": "number" }, "description": "Viewport bounding box [minX, minY, maxX, maxY]." },
            "center": { "type": "array", "minItems": {{StudioCompositionViewBounds.CenterOrdinateCount}}, "maxItems": {{StudioCompositionViewBounds.CenterOrdinateCount}}, "items": { "type": "number" }, "description": "Viewport center [x, y]." },
            "zoom": { "type": "number", "minimum": {{Number(StudioCompositionViewBounds.MinZoom)}}, "maximum": {{Number(StudioCompositionViewBounds.MaxZoom)}}, "description": "Map zoom." },
            "pitch": { "type": "number", "minimum": {{Number(StudioCompositionViewBounds.MinPitch)}}, "maximum": {{Number(StudioCompositionViewBounds.MaxPitch)}}, "description": "Map pitch." },
            "bearing": { "type": "number", "minimum": {{Number(StudioCompositionViewBounds.MinBearing)}}, "exclusiveMaximum": {{Number(StudioCompositionViewBounds.MaxBearingExclusive)}}, "description": "Map bearing." },
            "crs": { "type": "string", "maxLength": 100, "description": "CRS, e.g. EPSG:4326." }
          }
        }
        """;

    private const string WidgetInputSchemaJson = """
        {
          "type": "object",
          "description": "Caller-authored widget; ids from honua_studio_get_draft.",
          "required": ["id", "kind"],
          "additionalProperties": false,
          "properties": {
            "id": { "type": "string", "minLength": 1, "maxLength": 200, "description": "Unique composition widget id." },
            "kind": { "type": "string", "minLength": 1, "maxLength": 100, "description": "Widget kind (e.g. table, chart, legend, filter)." },
            "title": { "type": "string", "maxLength": 200, "description": "Display title." },
            "sourceId": { "type": "string", "maxLength": 200, "description": "Bound source identifier." },
            "config": { "type": "object", "description": "Bounded widget configuration." }
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
          "description": "Caller-authored binding; component ids from honua_studio_get_draft.",
          "required": ["id", "on", "do"],
          "additionalProperties": false,
          "properties": {
            "id": { "type": "string", "minLength": 1, "maxLength": {{StudioInteractionVocabulary.MaxInteractionIdLength}}, "pattern": "\\S", "description": "Unique composition interaction id; existing id replaces the binding." },
            "on": {
              "type": "object",
              "required": ["ref", "event"],
              "additionalProperties": false,
              "properties": {
                "ref": { "type": "string", "pattern": "{{ComponentRefPattern}}", "description": "Component in this document: map, layer:{id}, widget:{id} or control:{id} (controls collection)." },
                "event": { "type": "string", "enum": {{EventNameEnumJson}}, "description": "Gesture: featureSelect/featureHover (layer), selection (widget), change (control), viewportChange (map)." }
              },
              "description": "Gesture source; max {{StudioInteractionVocabulary.MaxInteractionsPerEventSource}} interactions per (ref, event)."
            },
            "do": {
              "type": "object",
              "required": ["ref", "verb"],
              "additionalProperties": false,
              "properties": {
                "ref": { "type": "string", "pattern": "{{ComponentRefPattern}}", "description": "Action-target component declared in the same document." },
                "verb": { "type": "string", "enum": {{ActionVerbEnumJson}}, "description": "Presentation/exploration only; never mutates source records." },
                "args": { "type": "object", "description": "Static JSON; '$event.' strings resolve from the event payload at dispatch. No expressions." }
              },
              "description": "Action emits no events; bindings cannot cascade."
            },
            "disabled": { "type": "boolean", "default": false, "description": "Inactive binding retained in document; skipped at dispatch." }
          }
        }
        """;

    private static readonly string BindInteractionArgumentSchemaJson = $$"""
        {
          "type": "object",
          "required": ["draftId", "generation", "interaction"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string", "format": "uuid", "description": "honua_studio_create_draft: map/app/dashboard." },
            "generation": { "type": "integer", "minimum": 1, "description": "honua_studio_get_draft; stale fails." },
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
            "draftId": { "type": "string", "format": "uuid", "description": "honua_studio_create_draft: map/app/dashboard." },
            "generation": { "type": "integer", "minimum": 1, "description": "honua_studio_get_draft; stale fails." },
            "interactionId": { "type": "string", "minLength": 1, "maxLength": 200, "pattern": "\\S", "description": "interaction.id from honua_studio_get_draft; unknown ids fail." }
          }
        }
        """;

    // The closed control-kind vocabulary comes from the shared ADR-0031 contract
    // (StudioInteractionVocabulary.ControlKinds) rather than being restated here, so the
    // advertised schema, the composition-editor admission gate and the
    // StudioPackageValidator document gate cannot drift apart.
    private static readonly string ControlInputSchemaJson = $$"""
        {
          "type": "object",
          "description": "Caller-authored control; layer/source ids from honua_studio_get_draft.",
          "required": ["id", "kind"],
          "additionalProperties": false,
          "properties": {
            "id": { "type": "string", "minLength": 1, "maxLength": {{StudioInteractionVocabulary.MaxControlIdLength}}, "pattern": "\\S", "description": "Unique controls-block id; existing id replaces the control. Referenced by control:{id}." },
            "kind": { "type": "string", "enum": {{ControlKindEnumJson}}, "description": "Closed enum. Data controls timeSlider, filterSelect, filterSlider, filterDateRange and opacity emit change; others are map affordances. No feature-editing kind." },
            "title": { "type": "string", "maxLength": {{StudioInteractionVocabulary.MaxControlTitleLength}}, "description": "Host label; defaults per kind." },
            "sourceId": { "type": "string", "minLength": 1, "maxLength": {{StudioInteractionVocabulary.MaxControlSourceIdLength}}, "description": "Domain source: layer id or its bound sourceId in this document; invalid references fail. Normally set for data controls, omitted for presentation controls." },
            "config": { "type": "object", "description": "Per-kind host configuration: data only, no expressions or additional capabilities." }
          }
        }
        """;

    private static readonly string AddControlArgumentSchemaJson = $$"""
        {
          "type": "object",
          "required": ["draftId", "generation", "control"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string", "format": "uuid", "description": "honua_studio_create_draft: map/app/dashboard." },
            "generation": { "type": "integer", "minimum": 1, "description": "honua_studio_get_draft; stale fails." },
            "control": {{ControlInputSchemaJson}}
          }
        }
        """;

    private static readonly string RemoveControlArgumentSchemaJson = $$"""
        {
          "type": "object",
          "required": ["draftId", "generation", "controlId"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string", "format": "uuid", "description": "honua_studio_create_draft: map/app/dashboard." },
            "generation": { "type": "integer", "minimum": 1, "description": "honua_studio_get_draft; stale fails." },
            "controlId": { "type": "string", "minLength": 1, "maxLength": {{StudioInteractionVocabulary.MaxControlIdLength}}, "pattern": "\\S", "description": "control.id from honua_studio_get_draft; unknown ids fail." },
            "cascadeInteractions": { "type": "boolean", "default": false, "description": "True removes interactions referencing control:{controlId} in on.ref or do.ref. False (default) rejects removal while references remain." }
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
              "description": "Caller-supplied. Studio package family."
            },
            "schemaVersion": { "type": "string", "minLength": 1, "maxLength": 50, "description": "Caller-supplied family schema version." },
            "workspaceId": { "type": "string", "maxLength": 200, "description": "Workspace identifier." },
            "ownerId": { "type": "string", "maxLength": 200, "description": "Admin-only owner id; non-admin callers omit it and become owner." },
            "body": { "type": "object", "description": "Initial composition/family body; omit to start empty." },
            "itemId": { "type": "string", "format": "uuid", "description": "Existing item id to add a draft under." },
            "baseVersionId": { "type": "string", "format": "uuid", "description": "Immutable version this draft reopened, if any." }
          }
        }
        """;

    private const string DraftIdArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["draftId"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string", "format": "uuid", "description": "honua_studio_create_draft: map/app/dashboard." }
          }
        }
        """;

    private static readonly string UpdateDraftArgumentSchemaJson = $$"""
        {
          "type": "object",
          "required": ["draftId", "generation", "packageKey", "schemaVersion"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string", "format": "uuid", "description": "honua_studio_create_draft: map/app/dashboard." },
            "generation": { "type": "integer", "minimum": 1, "description": "honua_studio_get_draft; stale fails." },
            "packageKey": {{PackageKeyPropertyJson}},
            "schemaVersion": { "type": "string", "minLength": 1, "maxLength": 50, "description": "Caller-supplied family schema version." },
            "format": { "type": "string", "maxLength": 100, "description": "Family-specific package format." },
            "workspaceId": { "type": "string", "maxLength": 200, "description": "Workspace identifier." },
            "ownerId": { "type": "string", "maxLength": 200, "description": "Admin-only owner id; non-admin assignment is rejected." },
            "body": { "type": "object", "description": "Replacement composition/family body; omit to keep it." }
          }
        }
        """;

    private static readonly string AddLayerArgumentSchemaJson = $$"""
        {
          "type": "object",
          "required": ["draftId", "generation", "layer"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string", "format": "uuid", "description": "honua_studio_create_draft: map/app/dashboard." },
            "generation": { "type": "integer", "minimum": 1, "description": "honua_studio_get_draft; stale fails." },
            "layer": {{LayerInputSchemaJson}},
            "beforeId": { "type": "string", "maxLength": 200, "description": "Insert before this layer id; otherwise append." }
          }
        }
        """;

    private const string RemoveLayerArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["draftId", "generation", "layerId"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string", "format": "uuid", "description": "honua_studio_create_draft: map/app/dashboard." },
            "generation": { "type": "integer", "minimum": 1, "description": "honua_studio_get_draft; stale fails." },
            "layerId": { "type": "string", "minLength": 1, "maxLength": 200, "description": "layer.id from honua_studio_get_draft." }
          }
        }
        """;

    private const string SetLayerStyleArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["draftId", "generation", "layerId"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string", "format": "uuid", "description": "honua_studio_create_draft: map/app/dashboard." },
            "generation": { "type": "integer", "minimum": 1, "description": "honua_studio_get_draft; stale fails." },
            "layerId": { "type": "string", "minLength": 1, "maxLength": 200, "description": "layer.id from honua_studio_get_draft." },
            "styleRef": { "type": ["string", "null"], "maxLength": 200, "description": "Catalog styleId or inline style key; omit/null clears binding." },
            "styleBody": { "type": "object", "description": "Inline paint/layout overrides keyed by layerId or '<layerId>__outline'. Not with styleRef." }
          }
        }
        """;

    private const string SetMapStyleArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["draftId", "generation", "mapSpec"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string", "format": "uuid", "description": "honua_studio_create_draft: map/app/dashboard." },
            "generation": { "type": "integer", "minimum": 1, "description": "honua_studio_get_draft; stale fails." },
            "mapSpec": { "type": "object", "description": "Caller-edited mapSpec from honua_studio_get_draft; only layers, name, metadata, transition, light, lights and fog may change." }
          }
        }
        """;

    private const string SetLayerVisibilityArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["draftId", "generation", "layerId", "visible"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string", "format": "uuid", "description": "honua_studio_create_draft: map/app/dashboard." },
            "generation": { "type": "integer", "minimum": 1, "description": "honua_studio_get_draft; stale fails." },
            "layerId": { "type": "string", "minLength": 1, "maxLength": 200, "description": "layer.id from honua_studio_get_draft." },
            "visible": { "type": "boolean", "description": "Caller-supplied layer visibility." }
          }
        }
        """;

    private static readonly string SetViewArgumentSchemaJson = $$"""
        {
          "type": "object",
          "required": ["draftId", "generation", "view"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string", "format": "uuid", "description": "honua_studio_create_draft: map/app/dashboard." },
            "generation": { "type": "integer", "minimum": 1, "description": "honua_studio_get_draft; stale fails." },
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
            "draftId": { "type": "string", "format": "uuid", "description": "honua_studio_create_draft: map/app/dashboard." },
            "generation": { "type": "integer", "minimum": 1, "description": "honua_studio_get_draft; stale fails." },
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
            "draftId": { "type": "string", "format": "uuid", "description": "honua_studio_create_draft: map/app/dashboard." },
            "generation": { "type": "integer", "minimum": 1, "description": "honua_studio_get_draft; stale fails." },
            "widgetId": { "type": "string", "minLength": 1, "maxLength": 200, "description": "widget.id from honua_studio_get_draft; unknown ids fail." }
          }
        }
        """;

    private const string ProposePublicationArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["itemId", "versionId", "contentHash", "route", "visibility"],
          "additionalProperties": false,
          "properties": {
            "itemId": { "type": "string", "format": "uuid", "description": "version.itemId from honua_studio_save_version." },
            "versionId": { "type": "string", "format": "uuid", "description": "version.versionId from honua_studio_save_version." },
            "contentHash": { "type": "string", "minLength": 1, "maxLength": 128, "description": "version.contentHash from honua_studio_save_version." },
            "route": { "type": "string", "minLength": 1, "maxLength": 200, "description": "Caller-supplied. Requested target route key." },
            "visibility": { "type": "string", "enum": ["personal", "team", "organization", "public"], "description": "Caller-supplied. personal: owner; team/organization: signed-in item-tenant callers; public: anonymous readers." },
            "note": { "type": "string", "maxLength": 2000, "description": "Rationale recorded for reviewers." }
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

    public static readonly JsonElement SetMapStyleArgumentSchema = Parse(SetMapStyleArgumentSchemaJson);

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

    /// <summary>Schema for <see cref="McpStudioAddControlArgument"/>.</summary>
    public static readonly JsonElement AddControlArgumentSchema = Parse(AddControlArgumentSchemaJson);

    /// <summary>Schema for <see cref="McpStudioRemoveControlArgument"/>.</summary>
    public static readonly JsonElement RemoveControlArgumentSchema = Parse(RemoveControlArgumentSchemaJson);

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
