// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Reporting.Domain;

/// <summary>
/// Base type for every section in an <see cref="AnalysisReport"/>. Concrete
/// subtypes are sealed-record discriminated unions; <see cref="Kind"/> carries
/// the wire-level discriminator value from
/// <see cref="AnalysisReportSectionKinds"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(HeadingSection), AnalysisReportSectionKinds.Heading)]
[JsonDerivedType(typeof(ParagraphSection), AnalysisReportSectionKinds.Paragraph)]
[JsonDerivedType(typeof(KeyMetricSection), AnalysisReportSectionKinds.KeyMetric)]
[JsonDerivedType(typeof(TableSection), AnalysisReportSectionKinds.Table)]
[JsonDerivedType(typeof(ChartSection), AnalysisReportSectionKinds.Chart)]
[JsonDerivedType(typeof(MapEmbedSection), AnalysisReportSectionKinds.MapEmbed)]
[JsonDerivedType(typeof(NarrativeSection), AnalysisReportSectionKinds.Narrative)]
[JsonDerivedType(typeof(ProvenanceFooterSection), AnalysisReportSectionKinds.ProvenanceFooter)]
public abstract record AnalysisReportSection
{
    /// <summary>
    /// Stable discriminator used to round-trip section subtypes through JSON.
    /// </summary>
    [JsonIgnore]
    public abstract string Kind { get; }
}

/// <summary>
/// Section heading. Renderers map <see cref="Level"/> to <c>#</c>/<c>&lt;h&gt;</c>
/// nesting; allowed range is 1-6.
/// </summary>
public sealed record HeadingSection : AnalysisReportSection
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => AnalysisReportSectionKinds.Heading;

    /// <summary>Visible heading text.</summary>
    public required string Text { get; init; }

    /// <summary>Heading level (1-6).</summary>
    public required int Level { get; init; }
}

/// <summary>
/// A plain-text paragraph rendered factually (no narrative provenance).
/// </summary>
public sealed record ParagraphSection : AnalysisReportSection
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => AnalysisReportSectionKinds.Paragraph;

    /// <summary>Paragraph body text.</summary>
    public required string Text { get; init; }
}

/// <summary>
/// A single key/value metric (e.g. "Total area: 12.4 km²"). Distinct from a
/// row in <see cref="TableSection"/> because metrics carry units explicitly.
/// </summary>
public sealed record KeyMetricSection : AnalysisReportSection
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => AnalysisReportSectionKinds.KeyMetric;

    /// <summary>Display label for the metric.</summary>
    public required string Label { get; init; }

    /// <summary>Already-formatted, locale-invariant value text.</summary>
    public required string Value { get; init; }

    /// <summary>Optional unit annotation (e.g. <c>m²</c>, <c>km</c>).</summary>
    public string? Unit { get; init; }

    /// <summary>Optional spatial reference annotation (e.g. <c>EPSG:4326</c>).</summary>
    public string? SpatialReference { get; init; }
}

/// <summary>
/// Tabular section. Rows are bounded by <c>Reporting:MaxTableRows</c>; excess
/// rows are summarized via <see cref="TruncatedRowCount"/>.
/// </summary>
public sealed record TableSection : AnalysisReportSection
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => AnalysisReportSectionKinds.Table;

    /// <summary>Optional table caption rendered above the table.</summary>
    public string? Caption { get; init; }

    /// <summary>Ordered list of column headers.</summary>
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>Ordered list of rows; each row's length must match <see cref="Columns"/>.</summary>
    public required IReadOnlyList<IReadOnlyList<string>> Rows { get; init; }

    /// <summary>
    /// Number of rows omitted because the source data exceeded the configured
    /// row cap. Zero when no truncation occurred.
    /// </summary>
    public int TruncatedRowCount { get; init; }
}

/// <summary>
/// Chart data; renderers convert this into inline SVG. <see cref="Kind"/>
/// stays the section discriminator; the chart kind is on
/// <see cref="ChartKind"/>.
/// </summary>
public sealed record ChartSection : AnalysisReportSection
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => AnalysisReportSectionKinds.Chart;

    /// <summary>Optional caption rendered above the chart.</summary>
    public string? Caption { get; init; }

    /// <summary>Bar or line chart kind.</summary>
    public required ReportChartKind ChartKind { get; init; }

    /// <summary>Category labels for the X axis.</summary>
    public required IReadOnlyList<string> Categories { get; init; }

    /// <summary>One or more named series of numeric values.</summary>
    public required IReadOnlyList<ChartSeries> Series { get; init; }

    /// <summary>Optional X axis label.</summary>
    public string? XAxisLabel { get; init; }

    /// <summary>Optional Y axis label.</summary>
    public string? YAxisLabel { get; init; }
}

/// <summary>
/// Single named numeric series for a <see cref="ChartSection"/>.
/// </summary>
public sealed record ChartSeries
{
    /// <summary>Series display label.</summary>
    public required string Name { get; init; }

    /// <summary>Numeric values aligned to <see cref="ChartSection.Categories"/>.</summary>
    public required IReadOnlyList<double> Values { get; init; }
}

/// <summary>
/// Chart variant emitted by reporting renderers.
/// </summary>
public enum ReportChartKind
{
    /// <summary>Vertical bar chart.</summary>
    Bar = 0,

    /// <summary>Polyline chart.</summary>
    Line = 1
}

/// <summary>
/// Pointer to a related map package on the MCP surface
/// (<c>honua://map-packages/{id}</c>).
/// </summary>
public sealed record MapEmbedSection : AnalysisReportSection
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => AnalysisReportSectionKinds.MapEmbed;

    /// <summary>Display caption for the embedded map.</summary>
    public required string Caption { get; init; }

    /// <summary>Stable MCP resource URI of the underlying map package.</summary>
    public required string MapPackageUri { get; init; }
}

/// <summary>
/// Narrative paragraph produced by the narrative provider. A
/// <see cref="DeterministicText"/> is always present; <see cref="LlmText"/> is
/// set when the LLM provider successfully filled the slot.
/// </summary>
public sealed record NarrativeSection : AnalysisReportSection
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => AnalysisReportSectionKinds.Narrative;

    /// <summary>Stable identifier for the narrative slot the template defined.</summary>
    public required string SlotId { get; init; }

    /// <summary>Always-available deterministic factual paragraph.</summary>
    public required string DeterministicText { get; init; }

    /// <summary>Optional LLM-authored paragraph that supersedes the deterministic text.</summary>
    public string? LlmText { get; init; }

    /// <summary>
    /// Source of the rendered text for this slot. Mirrors
    /// <see cref="AnalysisReport.NarrativeMode"/> at the slot level so consumers
    /// can show a per-slot provenance badge.
    /// </summary>
    public required NarrativeMode Mode { get; init; }
}

/// <summary>
/// Footer summarizing the provenance record from the originating result
/// package. Renderers emit a compact list of sources, processes, and timestamps.
/// </summary>
public sealed record ProvenanceFooterSection : AnalysisReportSection
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => AnalysisReportSectionKinds.ProvenanceFooter;

    /// <summary>Identifier of the originating result package.</summary>
    public required string ResultPackageId { get; init; }

    /// <summary>Identifier of the originating geoprocessing job.</summary>
    public required string JobId { get; init; }

    /// <summary>Identifiers of the geoprocessing operations applied.</summary>
    public required IReadOnlyList<string> ProcessDefinitions { get; init; }

    /// <summary>Source dataset identifiers used to produce the result.</summary>
    public required IReadOnlyList<string> Sources { get; init; }

    /// <summary>When the originating workflow execution completed.</summary>
    public DateTimeOffset? ExecutedAt { get; init; }

    /// <summary>When the report itself was generated.</summary>
    public required DateTimeOffset GeneratedAt { get; init; }
}
