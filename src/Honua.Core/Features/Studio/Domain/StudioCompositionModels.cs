// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Studio.Domain;

/// <summary>
/// Canonical composition payload for <see cref="StudioPackageFamily.Map"/> and
/// <see cref="StudioPackageFamily.App"/> Studio package drafts. Persisted
/// verbatim in <see cref="StudioPackageEnvelope.Body"/> (honua-server#3002,
/// AD-8: composition state IS the lifecycle draft). The shape mirrors the
/// honua-sdk-js agent-tools vocabulary (<c>src/agent-tools/index.ts</c> —
/// <c>addLayer</c>/<c>setViewport</c> and the runtime's layer/viewport
/// summaries) so the server-side composition MCP tools and the SDK's
/// browser-side agent tools stay taxonomy-aligned even though the SDK surface
/// is read/local-mutate only and the server surface is the durable draft.
/// </summary>
public sealed record StudioCompositionBody
{
    /// <summary>Shared empty composition body.</summary>
    public static StudioCompositionBody Empty { get; } = new();

    /// <summary>Ordered layers composed onto the draft, bottom-to-top.</summary>
    [JsonPropertyName("layers")]
    public IReadOnlyList<StudioCompositionLayer> Layers { get; init; } = Array.Empty<StudioCompositionLayer>();

    /// <summary>Initial/current map view.</summary>
    [JsonPropertyName("view")]
    public StudioCompositionView? View { get; init; }

    /// <summary>App-family widgets bound to the composition.</summary>
    [JsonPropertyName("widgets")]
    public IReadOnlyList<StudioCompositionWidget> Widgets { get; init; } = Array.Empty<StudioCompositionWidget>();

    /// <summary>
    /// Declarative event→action bindings between this document's components
    /// (geospatial-mcp ADR-0030, <c>common/interactions.schema.json</c>).
    /// </summary>
    /// <remarks>
    /// Deliberately NULLABLE, unlike <see cref="Layers"/>/<see cref="Widgets"/>: the
    /// projection is overlaid key-by-key onto the stored document by
    /// <c>StudioCompositionBodyEditor.WriteBody</c>, and the source-generated context writes
    /// with <c>WhenWritingNull</c>. A null therefore emits nothing at all, so an ordinary
    /// layer/view/widget edit on a document that never declared interactions does not
    /// materialize an empty <c>"interactions": []</c> member into every stored map/app
    /// package. An EMPTY (non-null) list still serializes, so removing the last binding
    /// genuinely clears the stored block.
    /// </remarks>
    [JsonPropertyName("interactions")]
    public IReadOnlyList<StudioInteraction>? Interactions { get; init; }

    /// <summary>
    /// Presentation-only grid placement for the document's widgets
    /// (geospatial-mcp ADR-0030). Null when the document declares no layout;
    /// see the <see cref="Interactions"/> remarks for why.
    /// </summary>
    [JsonPropertyName("layout")]
    public StudioLayout? Layout { get; init; }

    /// <summary>
    /// Input affordances declared by this composition document — the collection a
    /// <c>control:{id}</c> reference resolves against (geospatial-mcp ADR-0031,
    /// <c>common/controls.schema.json</c>). Null when the document declares no controls;
    /// see the <see cref="Interactions"/> remarks for why the member is nullable rather
    /// than empty-by-default.
    /// </summary>
    [JsonPropertyName("controls")]
    public IReadOnlyList<StudioCompositionControl>? Controls { get; init; }

    /// <summary>
    /// Canonical map-package source binding identifiers projected from the stored
    /// <c>sourceBindings</c> block. This validation-only projection is not serialized;
    /// <c>StudioCompositionBodyEditor</c> preserves the original block verbatim.
    /// </summary>
    [JsonIgnore]
    public IReadOnlySet<string> SourceBindingIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// One control entry in a composition document's <c>controls</c> collection
/// (geospatial-mcp ADR-0031, <c>common/controls.schema.json#/$defs/control</c>).
/// A control is an INPUT affordance: it renders no dataset, emits the closed
/// vocabulary's <c>change</c> event when the user operates it, and is chrome
/// rather than a <see cref="StudioLayout"/> grid item. The entry shape deliberately
/// mirrors <see cref="StudioCompositionWidget"/> so agents author both collections
/// the same way.
/// </summary>
public sealed record StudioCompositionControl
{
    /// <summary>Stable control identifier, unique within the composition's controls block.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Control kind from the closed ADR-0031 vocabulary
    /// (<see cref="StudioInteractionVocabulary.ControlKinds"/>).
    /// </summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>Host-rendered label. Hosts fall back to a per-kind default when omitted.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Identifier of the layer or datasource the control reads its domain from.
    /// Data-binding kinds normally set it; presentation-only kinds omit it.
    /// </summary>
    [JsonPropertyName("sourceId")]
    public string? SourceId { get; init; }

    /// <summary>
    /// Per-kind, host-interpreted configuration. Data, never code: it carries no
    /// expression language and grants no capability the composition surface does not
    /// already have.
    /// </summary>
    [JsonPropertyName("config")]
    public JsonElement? Config { get; init; }
}

/// <summary>
/// One declarative event→action binding in a composition document
/// (geospatial-mcp ADR-0030, <c>common/interactions.schema.json#/$defs/interaction</c>).
/// Bindings are data, never code: the only dynamic element is <c>$event.*</c> path
/// substitution inside <see cref="StudioInteractionAction.Args"/> string values.
/// </summary>
public sealed record StudioInteraction
{
    /// <summary>Stable identifier, unique within the document's interactions block.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>The user-gesture event source.</summary>
    [JsonPropertyName("on")]
    public required StudioInteractionEvent On { get; init; }

    /// <summary>The action performed when the event fires.</summary>
    [JsonPropertyName("do")]
    public required StudioInteractionAction Do { get; init; }

    /// <summary>Authored-but-inactive binding: retained in the document, skipped at dispatch.</summary>
    [JsonPropertyName("disabled")]
    public bool? Disabled { get; init; }
}

/// <summary>
/// The event source half of a <see cref="StudioInteraction"/>: a component
/// reference plus one member of the closed event set
/// (<see cref="StudioInteractionVocabulary.EventNames"/>).
/// </summary>
public sealed record StudioInteractionEvent
{
    /// <summary>Component reference (<c>map</c>, <c>layer:{id}</c>, <c>widget:{id}</c>, <c>control:{id}</c>).</summary>
    [JsonPropertyName("ref")]
    public required string Ref { get; init; }

    /// <summary>Event name from the closed set.</summary>
    [JsonPropertyName("event")]
    public required string Event { get; init; }
}

/// <summary>
/// The action half of a <see cref="StudioInteraction"/>: a component reference,
/// one member of the closed verb set
/// (<see cref="StudioInteractionVocabulary.ActionVerbs"/>), and static JSON
/// arguments. Actions never emit events, so binding graphs cannot cycle.
/// </summary>
public sealed record StudioInteractionAction
{
    /// <summary>Component reference the verb targets.</summary>
    [JsonPropertyName("ref")]
    public required string Ref { get; init; }

    /// <summary>Action verb from the closed set.</summary>
    [JsonPropertyName("verb")]
    public required string Verb { get; init; }

    /// <summary>
    /// Static JSON arguments for the verb. A string value beginning with
    /// <c>$event.</c> is replaced at dispatch time by the value at that path in the
    /// event payload. There is no expression language.
    /// </summary>
    [JsonPropertyName("args")]
    public JsonElement? Args { get; init; }
}

/// <summary>
/// Presentation-only grid placement for a composition document's widgets
/// (geospatial-mcp ADR-0030). A composition with widgets and no layout is valid;
/// hosts choose a default flow.
/// </summary>
public sealed record StudioLayout
{
    /// <summary>Grid geometry. Rows grow as needed; only columns are declared.</summary>
    [JsonPropertyName("grid")]
    public StudioLayoutGrid? Grid { get; init; }

    /// <summary>Widget placements.</summary>
    [JsonPropertyName("items")]
    public IReadOnlyList<StudioLayoutItem>? Items { get; init; }
}

/// <summary>Grid geometry for a <see cref="StudioLayout"/>.</summary>
public sealed record StudioLayoutGrid
{
    /// <summary>
    /// Number of grid columns
    /// (<see cref="StudioInteractionVocabulary.MinGridColumns"/>..<see cref="StudioInteractionVocabulary.MaxGridColumns"/>;
    /// <see cref="StudioInteractionVocabulary.DefaultGridColumns"/> when omitted).
    /// </summary>
    [JsonPropertyName("columns")]
    public int? Columns { get; init; }
}

/// <summary>Grid placement for one widget (or the map) in a <see cref="StudioLayout"/>.</summary>
public sealed record StudioLayoutItem
{
    /// <summary>Component reference the placement applies to.</summary>
    [JsonPropertyName("ref")]
    public required string Ref { get; init; }

    /// <summary>Grid column of the item's left edge (0-based).</summary>
    [JsonPropertyName("x")]
    public int X { get; init; }

    /// <summary>Grid row of the item's top edge (0-based).</summary>
    [JsonPropertyName("y")]
    public int Y { get; init; }

    /// <summary>Width in grid columns.</summary>
    [JsonPropertyName("w")]
    public int W { get; init; }

    /// <summary>Height in grid rows.</summary>
    [JsonPropertyName("h")]
    public int H { get; init; }
}

/// <summary>
/// How a component reference in an <c>interactions</c>/<c>layout</c> block
/// resolves against the composition document that declares it.
/// </summary>
public enum StudioComponentRefResolution
{
    /// <summary>The reference resolves to a component declared in the document.</summary>
    Resolved = 0,

    /// <summary>The reference does not match the <c>map|layer:{id}|widget:{id}|control:{id}</c> grammar.</summary>
    Malformed = 1,

    /// <summary>The reference is well-formed but names no component in the document.</summary>
    Unresolved = 2,
}

/// <summary>
/// The single source of truth for the geospatial-mcp ADR-0030 interaction
/// vocabulary: the closed event/verb sets, the component-reference grammar, the
/// per-<c>(on.ref, on.event)</c> fan-out cap, and the layout grid bounds.
/// </summary>
/// <remarks>
/// Every admission surface that accepts an interactions/layout block enforces the same
/// constants from here — the MCP tool schemas advertise these enums, the composition body
/// editor rejects out-of-vocabulary binds at admission, and
/// <c>StudioPackageValidator</c> gates the whole document. Keeping the vocabulary (and the
/// checks themselves) in one place is the same anti-drift move
/// <see cref="StudioCompositionViewBounds"/> made for the viewport contract.
/// </remarks>
public static class StudioInteractionVocabulary
{
    /// <summary>Component reference naming the map itself.</summary>
    public const string MapRef = "map";

    /// <summary>Prefix of a layer component reference.</summary>
    public const string LayerRefPrefix = "layer:";

    /// <summary>Prefix of a widget component reference.</summary>
    public const string WidgetRefPrefix = "widget:";

    /// <summary>Prefix of a control component reference.</summary>
    public const string ControlRefPrefix = "control:";

    /// <summary>
    /// Largest number of interactions permitted to share the same
    /// <c>(on.ref, on.event)</c> pair. ADR-0030 requires implementations to bound the
    /// fan-out and RECOMMENDS 8; documents over the cap are rejected, not truncated.
    /// </summary>
    public const int MaxInteractionsPerEventSource = 8;

    /// <summary>Largest accepted interaction identifier length.</summary>
    public const int MaxInteractionIdLength = 200;

    /// <summary>Largest accepted control identifier length (ADR-0031 <c>control.id</c>).</summary>
    public const int MaxControlIdLength = 200;

    /// <summary>Largest accepted control title length (ADR-0031 <c>control.title</c>).</summary>
    public const int MaxControlTitleLength = 200;

    /// <summary>Largest accepted control source identifier length (ADR-0031 <c>control.sourceId</c>).</summary>
    public const int MaxControlSourceIdLength = 200;

    /// <summary>Smallest accepted <see cref="StudioLayoutGrid.Columns"/>.</summary>
    public const int MinGridColumns = 1;

    /// <summary>Largest accepted <see cref="StudioLayoutGrid.Columns"/>.</summary>
    public const int MaxGridColumns = 24;

    /// <summary>Grid columns assumed when <see cref="StudioLayoutGrid.Columns"/> is omitted.</summary>
    public const int DefaultGridColumns = 12;

    /// <summary>
    /// The closed event set: <c>featureSelect</c>/<c>featureHover</c> (layer sources),
    /// <c>selection</c> (widget sources), <c>change</c> (control sources),
    /// <c>viewportChange</c> (map source). Extending it requires a standard ADR.
    /// </summary>
    public static IReadOnlyList<string> EventNames { get; } =
        ["featureSelect", "featureHover", "selection", "change", "viewportChange"];

    /// <summary>
    /// The closed verb set. Verbs mutate presentation/exploration state only, never
    /// source records (ADR-0028 is unaffected). Extending it requires a standard ADR.
    /// </summary>
    public static IReadOnlyList<string> ActionVerbs { get; } =
        ["setFilter", "setViewport", "selectFeature", "runWidgetQuery", "setVisibility"];

    /// <summary>Returns true when <paramref name="value"/> is a member of the closed event set.</summary>
    public static bool IsEventName(string? value) =>
        value is not null && EventNames.Contains(value, StringComparer.Ordinal);

    /// <summary>
    /// Returns whether an event belongs to the component type named by its source reference.
    /// Maps emit <c>viewportChange</c>, layers emit feature selection/hover, widgets emit
    /// <c>selection</c>, and controls emit <c>change</c>.
    /// </summary>
    public static bool IsEventSupportedBySource(string? reference, string? eventName)
        => reference switch
        {
            MapRef => string.Equals(eventName, "viewportChange", StringComparison.Ordinal),
            not null when reference.StartsWith(LayerRefPrefix, StringComparison.Ordinal) =>
                string.Equals(eventName, "featureSelect", StringComparison.Ordinal)
                || string.Equals(eventName, "featureHover", StringComparison.Ordinal),
            not null when reference.StartsWith(WidgetRefPrefix, StringComparison.Ordinal) =>
                string.Equals(eventName, "selection", StringComparison.Ordinal),
            not null when reference.StartsWith(ControlRefPrefix, StringComparison.Ordinal) =>
                string.Equals(eventName, "change", StringComparison.Ordinal),
            _ => false,
        };

    /// <summary>
    /// The closed control-kind set (geospatial-mcp ADR-0031,
    /// <c>common/controls.schema.json#/$defs/controlKind</c>). Map affordances:
    /// <c>navigation</c>, <c>scale</c>, <c>fullscreen</c>, <c>geolocate</c>, <c>search</c>,
    /// <c>measure</c>, <c>attribution</c>, <c>basemapSwitcher</c>, <c>bookmarks</c>;
    /// data-binding affordances that emit <c>change</c>: <c>timeSlider</c>,
    /// <c>filterSelect</c>, <c>filterSlider</c>, <c>filterDateRange</c>, <c>opacity</c>.
    /// There is deliberately NO feature-editing draw kind — autonomous agent mutation of
    /// source records stays behind the governed <c>edit_features</c> surface (ADR-0028).
    /// Extending the set requires a standard ADR.
    /// </summary>
    public static IReadOnlyList<string> ControlKinds { get; } =
    [
        "navigation",
        "scale",
        "fullscreen",
        "geolocate",
        "search",
        "measure",
        "timeSlider",
        "filterSelect",
        "filterSlider",
        "filterDateRange",
        "bookmarks",
        "opacity",
        "attribution",
        "basemapSwitcher",
    ];

    /// <summary>Returns true when <paramref name="value"/> is a member of the closed verb set.</summary>
    public static bool IsActionVerb(string? value) =>
        value is not null && ActionVerbs.Contains(value, StringComparer.Ordinal);

    /// <summary>Returns true when <paramref name="value"/> is a member of the closed control-kind set.</summary>
    public static bool IsControlKind(string? value) =>
        value is not null && ControlKinds.Contains(value, StringComparer.Ordinal);

    /// <summary>
    /// Returns true when <paramref name="value"/> is a well-formed <c>control:{id}</c>
    /// reference (grammar only). Controls are chrome rather than layout grid items, so the
    /// layout gate uses this to keep them out of the <c>layout.items</c> reference space
    /// even though they now resolve for interactions.
    /// </summary>
    public static bool IsControlRef(string? value) =>
        value is not null && HasNonEmptyId(value, ControlRefPrefix);

    /// <summary>
    /// Resolves a <see cref="StudioCompositionControl.SourceId"/> against the layers and
    /// datasources <paramref name="body"/> declares (geospatial-mcp ADR-0031: a control's
    /// source identifier resolution is a validation-gate responsibility, "as for layer
    /// references").
    /// </summary>
    /// <remarks>
    /// A composition document declares its data surface through <see cref="StudioCompositionBody.Layers"/>
    /// only — there is no separate datasources collection — so a control's source resolves
    /// against either a layer's <see cref="StudioCompositionLayer.Id"/> (the layer itself)
    /// or its <see cref="StudioCompositionLayer.SourceId"/> (the datasource that layer
    /// binds). Both spellings are legitimate: a <c>filterSelect</c> populated from a
    /// composed layer names the layer, while one reading a datasource the document already
    /// binds names that source.
    /// </remarks>
    /// <param name="body">The composition document the source identifier must resolve within.</param>
    /// <param name="sourceId">The source identifier to resolve.</param>
    /// <returns><see langword="true"/> when the identifier names a declared layer or datasource.</returns>
    public static bool IsDeclaredSourceId(StudioCompositionBody body, string? sourceId)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return false;
        }

        return body.SourceBindingIds.Contains(sourceId)
            || (body.Layers ?? []).Any(layer => layer is not null
            && (string.Equals(layer.Id, sourceId, StringComparison.Ordinal)
                || string.Equals(layer.SourceId, sourceId, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Returns true when <paramref name="value"/> matches the component-reference
    /// grammar <c>map | layer:{id} | widget:{id} | control:{id}</c> (grammar only —
    /// <see cref="ResolveRef"/> answers whether it resolves).
    /// </summary>
    public static bool IsComponentRef(string? value) =>
        value is not null
        && (string.Equals(value, MapRef, StringComparison.Ordinal)
            || HasNonEmptyId(value, LayerRefPrefix)
            || HasNonEmptyId(value, WidgetRefPrefix)
            || HasNonEmptyId(value, ControlRefPrefix));

    /// <summary>
    /// Resolves a component reference against <paramref name="body"/>: <c>map</c> always
    /// resolves, <c>layer:{id}</c> resolves against the document's layers,
    /// <c>widget:{id}</c> against its widgets, and <c>control:{id}</c> against its
    /// controls (geospatial-mcp ADR-0031 — before the controls collection existed a
    /// control reference could never resolve).
    /// </summary>
    /// <param name="body">The composition document the reference must resolve within.</param>
    /// <param name="value">The reference to resolve.</param>
    /// <returns>How the reference resolved.</returns>
    public static StudioComponentRefResolution ResolveRef(StudioCompositionBody body, string? value)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!IsComponentRef(value))
        {
            return StudioComponentRefResolution.Malformed;
        }

        if (string.Equals(value, MapRef, StringComparison.Ordinal))
        {
            return StudioComponentRefResolution.Resolved;
        }

        if (value!.StartsWith(ControlRefPrefix, StringComparison.Ordinal))
        {
            var controlId = value[ControlRefPrefix.Length..];
            return (body.Controls ?? []).Any(control => control is not null
                    && string.Equals(control.Id, controlId, StringComparison.Ordinal))
                ? StudioComponentRefResolution.Resolved
                : StudioComponentRefResolution.Unresolved;
        }

        if (value.StartsWith(LayerRefPrefix, StringComparison.Ordinal))
        {
            var layerId = value[LayerRefPrefix.Length..];
            return (body.Layers ?? []).Any(layer => layer is not null
                    && string.Equals(layer.Id, layerId, StringComparison.Ordinal))
                ? StudioComponentRefResolution.Resolved
                : StudioComponentRefResolution.Unresolved;
        }

        var widgetId = value[WidgetRefPrefix.Length..];
        return (body.Widgets ?? []).Any(widget => widget is not null
                && string.Equals(widget.Id, widgetId, StringComparison.Ordinal))
            ? StudioComponentRefResolution.Resolved
            : StudioComponentRefResolution.Unresolved;
    }

    /// <summary>
    /// Renders the human-readable reason a reference did not resolve, or an empty
    /// string for <see cref="StudioComponentRefResolution.Resolved"/>. Shared so the
    /// editor's admission error and the validator's diagnostic read identically.
    /// </summary>
    /// <param name="value">The offending reference.</param>
    /// <param name="resolution">The resolution outcome to describe.</param>
    /// <returns>The reason text.</returns>
    public static string DescribeResolution(string? value, StudioComponentRefResolution resolution) => resolution switch
    {
        StudioComponentRefResolution.Resolved => string.Empty,
        StudioComponentRefResolution.Malformed =>
            $"Component reference '{value}' is not a valid reference; use 'map', 'layer:{{id}}', 'widget:{{id}}' or 'control:{{id}}'.",
        _ => $"Component reference '{value}' does not resolve to a component declared in this composition document.",
    };

    private static bool HasNonEmptyId(string value, string prefix) =>
        value.Length > prefix.Length && value.StartsWith(prefix, StringComparison.Ordinal);
}

/// <summary>
/// One composed layer. Mirrors the SDK agent-tools <c>HonuaAgentLayerSummary</c> /
/// <c>addLayer</c> shape (id, sourceId, type, title, visible) plus a
/// <see cref="StyleRef"/> binding for <c>honua_studio_set_layer_style</c>.
/// </summary>
public sealed record StudioCompositionLayer
{
    /// <summary>Stable layer identifier, unique within the composition.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Bound source identifier.</summary>
    [JsonPropertyName("sourceId")]
    public string? SourceId { get; init; }

    /// <summary>Layer type (e.g. <c>fill</c>, <c>circle</c>, <c>line</c>, <c>raster</c>).</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Display title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Layer visibility.</summary>
    [JsonPropertyName("visible")]
    public bool Visible { get; init; } = true;

    /// <summary>Bound style reference (catalog <c>styleId</c> or inline style key).</summary>
    [JsonPropertyName("styleRef")]
    public string? StyleRef { get; init; }

    /// <summary>Additional layer metadata.</summary>
    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }
}

/// <summary>
/// Composition view. Mirrors the SDK agent-tools <c>HonuaAgentViewport</c> /
/// <c>setViewport</c> shape exactly (bbox, center, zoom, pitch, bearing, crs).
/// </summary>
public sealed record StudioCompositionView
{
    /// <summary>Viewport bounding box <c>[minX, minY, maxX, maxY]</c>.</summary>
    [JsonPropertyName("bbox")]
    public IReadOnlyList<double>? Bbox { get; init; }

    /// <summary>Viewport center <c>[x, y]</c>.</summary>
    [JsonPropertyName("center")]
    public IReadOnlyList<double>? Center { get; init; }

    /// <summary>Map zoom.</summary>
    [JsonPropertyName("zoom")]
    public double? Zoom { get; init; }

    /// <summary>Map pitch.</summary>
    [JsonPropertyName("pitch")]
    public double? Pitch { get; init; }

    /// <summary>Map bearing.</summary>
    [JsonPropertyName("bearing")]
    public double? Bearing { get; init; }

    /// <summary>Coordinate reference system (e.g. <c>EPSG:4326</c>).</summary>
    [JsonPropertyName("crs")]
    public string? Crs { get; init; }
}

/// <summary>
/// The single source of truth for <see cref="StudioCompositionView"/>'s numeric contract:
/// coordinate arity plus the zoom/pitch ranges the MCP composition tool schemas advertise.
/// </summary>
/// <remarks>
/// The wire model cannot express any of this — <c>IReadOnlyList&lt;double&gt;</c> deserializes
/// any-length arrays and <c>double?</c> accepts any magnitude — so every admission surface that
/// accepts a view has to enforce it. Keeping the bounds (and the check itself) here stops the MCP
/// tool schema and the live-collaboration op-log validator from drifting apart: the MCP schema
/// advertised <c>zoom &lt;= 24</c> / <c>pitch &lt;= 85</c> while the collaboration append path
/// admitted <c>{"zoom":25}</c> and consumed a permanent op-log cursor for it
/// (honua-server#2999 review).
/// </remarks>
public static class StudioCompositionViewBounds
{
    /// <summary>Ordinate count required by <see cref="StudioCompositionView.Bbox"/>.</summary>
    public const int BboxOrdinateCount = 4;

    /// <summary>Ordinate count required by <see cref="StudioCompositionView.Center"/>.</summary>
    public const int CenterOrdinateCount = 2;

    /// <summary>Smallest accepted <see cref="StudioCompositionView.Zoom"/>.</summary>
    public const double MinZoom = 0;

    /// <summary>Largest accepted <see cref="StudioCompositionView.Zoom"/>.</summary>
    public const double MaxZoom = 24;

    /// <summary>Smallest accepted <see cref="StudioCompositionView.Pitch"/> in degrees.</summary>
    public const double MinPitch = 0;

    /// <summary>Largest accepted <see cref="StudioCompositionView.Pitch"/> in degrees.</summary>
    public const double MaxPitch = 85;

    /// <summary>
    /// Validates <paramref name="view"/> against the shared coordinate-arity and zoom/pitch
    /// bounds. A <see langword="null"/> view is valid (the member is optional).
    /// </summary>
    /// <param name="view">View to check, or <see langword="null"/>.</param>
    /// <param name="error">Human-readable reason when the view is out of contract.</param>
    /// <returns><see langword="true"/> when the view satisfies the shared contract.</returns>
    public static bool TryValidate(StudioCompositionView? view, out string error)
    {
        if (view is null)
        {
            error = string.Empty;
            return true;
        }

        if (view.Bbox is { Count: not BboxOrdinateCount } bbox)
        {
            error = $"The viewport 'bbox' requires exactly {BboxOrdinateCount} coordinates; got {bbox.Count}.";
            return false;
        }

        if (view.Bbox is { Count: BboxOrdinateCount } orderedBbox &&
            !(orderedBbox[0] <= orderedBbox[2] && orderedBbox[1] <= orderedBbox[3]))
        {
            // The same ordering is required by the canonical Studio map validator. NaN also
            // fails these comparisons, so a collaboration edit cannot checkpoint an extent
            // that the package lifecycle subsequently rejects.
            error = "The viewport 'bbox' must be ordered as [minX,minY,maxX,maxY].";
            return false;
        }

        if (view.Center is { Count: not CenterOrdinateCount } center)
        {
            error = $"The viewport 'center' requires exactly {CenterOrdinateCount} coordinates; got {center.Count}.";
            return false;
        }

        // NaN/Infinity fail both comparisons, which is intended: neither is a renderable view.
        if (view.Zoom is { } zoom && !(zoom >= MinZoom && zoom <= MaxZoom))
        {
            error = $"The viewport 'zoom' must be between {MinZoom} and {MaxZoom}.";
            return false;
        }

        if (view.Pitch is { } pitch && !(pitch >= MinPitch && pitch <= MaxPitch))
        {
            error = $"The viewport 'pitch' must be between {MinPitch} and {MaxPitch}.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

/// <summary>
/// One widget bound to a map/app/dashboard composition (
/// <c>honua_studio_add_widget</c> / <c>honua_studio_remove_widget</c>).
/// </summary>
public sealed record StudioCompositionWidget
{
    /// <summary>Stable widget identifier, unique within the composition.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Widget kind (e.g. <c>table</c>, <c>chart</c>, <c>legend</c>, <c>filter</c>).</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>Display title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Bound source identifier, when the widget reads from a single source.</summary>
    [JsonPropertyName("sourceId")]
    public string? SourceId { get; init; }

    /// <summary>Widget-specific bounded configuration.</summary>
    [JsonPropertyName("config")]
    public JsonElement? Config { get; init; }
}
