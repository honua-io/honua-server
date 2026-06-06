// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Publishing.Reports;

/// <summary>
/// Minimal-but-real <c>honua.report-document.v1</c> model. The console serializes its report-builder
/// editor state into this shape (format/title/description/narrative/bindings/panels) and round-trips a
/// generated document back through <c>StudioReportDocument.ApplyTo</c>, so the property names here match
/// that wire contract exactly. Unlike the form package, the report document has no server-side publish
/// validator: the publication registry hashes the payload opaquely. This concrete model exists so NL
/// generation can produce — and a structural gate can validate — a real, addressable report structure
/// rather than an unconstrained blob.
/// </summary>
public sealed class ReportDocument
{
    /// <summary>Wire format discriminator for this report document.</summary>
    [JsonPropertyName("format")]
    public string Format { get; init; } = ReportDocumentFormats.V1;

    /// <summary>Human-readable report title. Required.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Optional report description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Optional narrative block rendered above the layout.</summary>
    [JsonPropertyName("narrative")]
    public string? Narrative { get; init; }

    /// <summary>Suggested/normalized server route slug for the published report.</summary>
    [JsonPropertyName("routeSlug")]
    public string? RouteSlug { get; init; }

    /// <summary>Source content bindings every panel can read from, by alias.</summary>
    [JsonPropertyName("bindings")]
    public ReportBinding[] Bindings { get; init => field = value ?? []; } = [];

    /// <summary>Ordered layout panels (chart, map, table, text).</summary>
    [JsonPropertyName("panels")]
    public ReportPanel[] Panels { get; init => field = value ?? []; } = [];
}

/// <summary>A source content binding panels reference by alias.</summary>
public sealed class ReportBinding
{
    /// <summary>Author-facing alias panels reference (for example <c>incidents</c>).</summary>
    [JsonPropertyName("alias")]
    public string Alias { get; init; } = string.Empty;

    /// <summary>Server content item id the alias resolves to.</summary>
    [JsonPropertyName("contentRef")]
    public string ContentRef { get; init; } = string.Empty;

    /// <summary>Optional pinned content version (for example <c>v7</c>); blank pins to latest at publish.</summary>
    [JsonPropertyName("versionPin")]
    public string? VersionPin { get; init; }
}

/// <summary>
/// A layout panel. Chart panels carry an embedded Vega-Lite spec; map/table/text panels are layout slots
/// bound (where relevant) to a declared data binding alias.
/// </summary>
public sealed class ReportPanel
{
    /// <summary>Stable, unique handle for the panel within the document.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>One of <see cref="ReportPanelKinds"/>: chart, map, table, text.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = ReportPanelKinds.Chart;

    /// <summary>Panel title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Binding alias this panel reads from (chart/map/table panels).</summary>
    [JsonPropertyName("bindingAlias")]
    public string? BindingAlias { get; init; }

    /// <summary>
    /// Vega-Lite chart spec, embedded as a JSON object. Required (and non-empty) for chart panels;
    /// null for non-chart panels.
    /// </summary>
    [JsonPropertyName("chartSpec")]
    public JsonElement? ChartSpec { get; init; }

    /// <summary>Static text body for <see cref="ReportPanelKinds.Text"/> panels.</summary>
    [JsonPropertyName("body")]
    public string? Body { get; init; }
}

/// <summary>Stable wire format discriminators for report documents.</summary>
public static class ReportDocumentFormats
{
    /// <summary>Current report document format.</summary>
    public const string V1 = "honua.report-document.v1";
}

/// <summary>Allowed report panel kinds.</summary>
public static class ReportPanelKinds
{
    /// <summary>Vega-Lite chart panel.</summary>
    public const string Chart = "chart";

    /// <summary>Map layout panel.</summary>
    public const string Map = "map";

    /// <summary>Tabular layout panel.</summary>
    public const string Table = "table";

    /// <summary>Static narrative/text panel.</summary>
    public const string Text = "text";

    /// <summary>The allowed set, for structural validation and prompt grounding.</summary>
    public static readonly string[] All = [Chart, Map, Table, Text];
}
