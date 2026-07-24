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
/// One app-family widget bound to the composition (App package family only;
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
