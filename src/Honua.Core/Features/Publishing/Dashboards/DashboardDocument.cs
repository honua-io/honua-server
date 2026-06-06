// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Publishing.Dashboards;

/// <summary>
/// Minimal-but-real <c>honua.dashboard-document.v1</c> model. The console serializes its dashboard-builder
/// editor state into this shape (format/title/description/narrative/breakpoint/bindings/panels) and
/// round-trips a generated document back through its dashboard-document mapper, so the property names here
/// match that wire contract. A dashboard is the close analog of a report — both are a layout of
/// panels/charts (Vega-Lite) over data bindings — but a dashboard adds interactive panel kinds
/// (filter, metric/kpi) and a responsive-preview breakpoint. Like the report document, the dashboard has
/// no server-side publish validator (the publication registry hashes the payload opaquely); this concrete
/// model exists so NL generation can produce — and a structural gate can validate — a real, addressable
/// dashboard structure rather than an unconstrained blob.
/// </summary>
public sealed class DashboardDocument
{
    /// <summary>Wire format discriminator for this dashboard document.</summary>
    [JsonPropertyName("format")]
    public string Format { get; init; } = DashboardDocumentFormats.V1;

    /// <summary>Human-readable dashboard title. Required.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Optional dashboard description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Optional narrative block rendered above the layout.</summary>
    [JsonPropertyName("narrative")]
    public string? Narrative { get; init; }

    /// <summary>Suggested/normalized server route slug for the published dashboard.</summary>
    [JsonPropertyName("routeSlug")]
    public string? RouteSlug { get; init; }

    /// <summary>Responsive-preview breakpoint the layout targets (desktop, tablet, mobile).</summary>
    [JsonPropertyName("breakpoint")]
    public string? Breakpoint { get; init; }

    /// <summary>Source content bindings every panel can read from, by alias.</summary>
    [JsonPropertyName("bindings")]
    public DashboardBinding[] Bindings { get; init => field = value ?? []; } = [];

    /// <summary>Ordered layout slots holding chart, map, table, filter, and metric panels.</summary>
    [JsonPropertyName("panels")]
    public DashboardPanel[] Panels { get; init => field = value ?? []; } = [];
}

/// <summary>A source content binding dashboard panels reference by alias.</summary>
public sealed class DashboardBinding
{
    /// <summary>Author-facing alias panels reference (for example <c>requests</c>).</summary>
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
/// A layout panel. Chart panels carry an embedded Vega-Lite spec; map/table/filter/metric panels are
/// layout slots bound (where relevant) to a declared data binding alias. Filter panels link/cross-filter
/// the other panels; metric panels render a single KPI value.
/// </summary>
public sealed class DashboardPanel
{
    /// <summary>Stable, unique handle for the panel within the document.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>One of <see cref="DashboardPanelKinds"/>: chart, map, table, filter, metric.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = DashboardPanelKinds.Chart;

    /// <summary>Panel title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Binding alias this panel reads from (chart/map/table/metric panels).</summary>
    [JsonPropertyName("bindingAlias")]
    public string? BindingAlias { get; init; }

    /// <summary>
    /// Vega-Lite chart spec, embedded as a JSON object. Required (and non-empty) for chart panels;
    /// null for non-chart panels.
    /// </summary>
    [JsonPropertyName("chartSpec")]
    public JsonElement? ChartSpec { get; init; }

    /// <summary>Field a <see cref="DashboardPanelKinds.Filter"/> panel filters on (cross-filter/linking key).</summary>
    [JsonPropertyName("field")]
    public string? Field { get; init; }
}

/// <summary>Stable wire format discriminators for dashboard documents.</summary>
public static class DashboardDocumentFormats
{
    /// <summary>Current dashboard document format.</summary>
    public const string V1 = "honua.dashboard-document.v1";
}

/// <summary>Allowed dashboard panel kinds.</summary>
public static class DashboardPanelKinds
{
    /// <summary>Vega-Lite chart panel.</summary>
    public const string Chart = "chart";

    /// <summary>Map layout panel.</summary>
    public const string Map = "map";

    /// <summary>Tabular layout panel.</summary>
    public const string Table = "table";

    /// <summary>Interactive filter / cross-filter panel.</summary>
    public const string Filter = "filter";

    /// <summary>Single-value metric / KPI panel.</summary>
    public const string Metric = "metric";

    /// <summary>The allowed set, for structural validation and prompt grounding.</summary>
    public static readonly string[] All = [Chart, Map, Table, Filter, Metric];
}
