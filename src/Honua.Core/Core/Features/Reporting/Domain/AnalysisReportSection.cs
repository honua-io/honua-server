// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Reporting.Domain;

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
    [JsonIgnore]
    public abstract string Kind { get; }
}

public sealed record HeadingSection : AnalysisReportSection
{
    [JsonIgnore]
    public override string Kind => AnalysisReportSectionKinds.Heading;

    public required string Text { get; init; }

    public required int Level { get; init; }
}

public sealed record ParagraphSection : AnalysisReportSection
{
    [JsonIgnore]
    public override string Kind => AnalysisReportSectionKinds.Paragraph;

    public required string Text { get; init; }
}

public sealed record KeyMetricSection : AnalysisReportSection
{
    [JsonIgnore]
    public override string Kind => AnalysisReportSectionKinds.KeyMetric;

    public required string Label { get; init; }

    // The value text is already formatted and locale-invariant.
    public required string Value { get; init; }

    public string? Unit { get; init; }

    public string? SpatialReference { get; init; }
}

public sealed record TableSection : AnalysisReportSection
{
    [JsonIgnore]
    public override string Kind => AnalysisReportSectionKinds.Table;

    public string? Caption { get; init; }

    public required IReadOnlyList<string> Columns { get; init; }

    // Each row's length must match the column count.
    public required IReadOnlyList<IReadOnlyList<string>> Rows { get; init; }

    public int TruncatedRowCount { get; init; }
}

public sealed record ChartSection : AnalysisReportSection
{
    [JsonIgnore]
    public override string Kind => AnalysisReportSectionKinds.Chart;

    public string? Caption { get; init; }

    public required ReportChartKind ChartKind { get; init; }

    public required IReadOnlyList<string> Categories { get; init; }

    public required IReadOnlyList<ChartSeries> Series { get; init; }

    public string? XAxisLabel { get; init; }

    public string? YAxisLabel { get; init; }
}

public sealed record ChartSeries
{
    public required string Name { get; init; }

    public required IReadOnlyList<double> Values { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ReportChartKind>))]
public enum ReportChartKind
{
    [JsonStringEnumMemberName("bar")]
    Bar = 0,

    [JsonStringEnumMemberName("line")]
    Line = 1
}

public sealed record MapEmbedSection : AnalysisReportSection
{
    [JsonIgnore]
    public override string Kind => AnalysisReportSectionKinds.MapEmbed;

    public required string Caption { get; init; }

    public required string MapPackageUri { get; init; }
}

public sealed record NarrativeSection : AnalysisReportSection
{
    [JsonIgnore]
    public override string Kind => AnalysisReportSectionKinds.Narrative;

    public required string SlotId { get; init; }

    public required string DeterministicText { get; init; }

    public string? LlmText { get; init; }

    public required NarrativeMode Mode { get; init; }
}

public sealed record ProvenanceFooterSection : AnalysisReportSection
{
    [JsonIgnore]
    public override string Kind => AnalysisReportSectionKinds.ProvenanceFooter;

    public required string ResultPackageId { get; init; }

    public required string JobId { get; init; }

    public required IReadOnlyList<string> ProcessDefinitions { get; init; }

    public required IReadOnlyList<string> Sources { get; init; }

    public DateTimeOffset? ExecutedAt { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }
}
