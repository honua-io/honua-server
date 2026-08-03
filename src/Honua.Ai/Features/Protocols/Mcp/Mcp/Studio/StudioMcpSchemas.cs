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

    /// <summary>Schema for <see cref="McpStudioSetViewArgument"/>.</summary>
    public static readonly JsonElement SetViewArgumentSchema = Parse(SetViewArgumentSchemaJson);

    /// <summary>Schema for <see cref="McpStudioAddWidgetArgument"/>.</summary>
    public static readonly JsonElement AddWidgetArgumentSchema = Parse(AddWidgetArgumentSchemaJson);

    /// <summary>Schema for <see cref="McpStudioRemoveWidgetArgument"/>.</summary>
    public static readonly JsonElement RemoveWidgetArgumentSchema = Parse(RemoveWidgetArgumentSchemaJson);

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
}
