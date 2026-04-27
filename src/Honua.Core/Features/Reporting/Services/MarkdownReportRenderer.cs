// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Reporting.Domain;

namespace Honua.Core.Features.Reporting.Services;

/// <summary>
/// CommonMark Markdown renderer. Walks <see cref="AnalysisReport.Sections"/>
/// in declared order and emits a deterministic body using
/// <see cref="StringBuilder"/> with invariant culture formatting.
/// </summary>
internal sealed class MarkdownReportRenderer : IAnalysisReportRenderer
{
    /// <inheritdoc />
    public AnalysisReportFormat Format => AnalysisReportFormat.Markdown;

    /// <inheritdoc />
    public string Render(AnalysisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!string.Equals(report.ReportContractVersion, ReportingConstants.ContractVersionV1, StringComparison.Ordinal))
        {
            throw new UnsupportedReportContractVersionException(report.ReportContractVersion, ReportingConstants.ContractVersionV1);
        }

        var sb = new StringBuilder(capacity: 1024);
        foreach (var section in report.Sections)
        {
            RenderSection(sb, section);
            sb.AppendLine();
        }

        // Trim trailing whitespace and the extra blank line emitted after the
        // last section so the body ends with exactly one newline (no
        // trailing spaces, no blank line at EOF).
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void RenderSection(StringBuilder sb, AnalysisReportSection section)
    {
        switch (section)
        {
            case HeadingSection heading:
                var level = Math.Clamp(heading.Level, 1, 6);
                sb.Append('#', level).Append(' ').AppendLine(heading.Text);
                break;

            case ParagraphSection paragraph:
                sb.AppendLine(paragraph.Text);
                break;

            case KeyMetricSection metric:
                sb.Append("- **").Append(metric.Label).Append("**: ").Append(metric.Value);
                if (!string.IsNullOrEmpty(metric.Unit))
                {
                    sb.Append(' ').Append(metric.Unit);
                }
                if (!string.IsNullOrEmpty(metric.SpatialReference))
                {
                    sb.Append(" (").Append(metric.SpatialReference).Append(')');
                }
                sb.AppendLine();
                break;

            case TableSection table:
                if (!string.IsNullOrEmpty(table.Caption))
                {
                    sb.Append('_').Append(table.Caption).AppendLine("_");
                }
                RenderMarkdownTable(sb, table);
                if (table.TruncatedRowCount > 0)
                {
                    sb.Append("_…and ")
                        .Append(table.TruncatedRowCount.ToString(CultureInfo.InvariantCulture))
                        .AppendLine(" additional row(s) not shown._");
                }
                break;

            case ChartSection chart:
                if (!string.IsNullOrEmpty(chart.Caption))
                {
                    sb.Append('_').Append(chart.Caption).AppendLine("_");
                }
                RenderChartAsTable(sb, chart);
                break;

            case MapEmbedSection map:
                sb.Append("- Map: [")
                    .Append(map.Caption)
                    .Append("](")
                    .Append(map.MapPackageUri)
                    .AppendLine(")");
                break;

            case NarrativeSection narrative:
                sb.AppendLine(narrative.LlmText ?? narrative.DeterministicText);
                break;

            case ProvenanceFooterSection footer:
                // Render each footer entry as a list item rather than relying
                // on Markdown hard-break (two-trailing-space) syntax, which
                // produced checked-in trailing whitespace on the goldens.
                sb.AppendLine("---");
                sb.Append("- _Job_: `").Append(footer.JobId).AppendLine("`");
                sb.Append("- _Result package_: `").Append(footer.ResultPackageId).AppendLine("`");
                if (footer.ProcessDefinitions.Count > 0)
                {
                    sb.Append("- _Processes_: ")
                        .AppendLine(string.Join(", ", footer.ProcessDefinitions));
                }
                if (footer.Sources.Count > 0)
                {
                    sb.Append("- _Sources_: ")
                        .AppendLine(string.Join(", ", footer.Sources));
                }
                if (footer.ExecutedAt is { } executedAt)
                {
                    sb.Append("- _Executed at_: ")
                        .AppendLine(executedAt.ToString("u", CultureInfo.InvariantCulture));
                }
                sb.Append("- _Generated at_: ")
                    .AppendLine(footer.GeneratedAt.ToString("u", CultureInfo.InvariantCulture));
                break;
        }
    }

    private static void RenderMarkdownTable(StringBuilder sb, TableSection table)
    {
        sb.Append('|');
        foreach (var col in table.Columns)
        {
            sb.Append(' ').Append(EscapePipe(col)).Append(" |");
        }
        sb.AppendLine();

        sb.Append('|');
        for (var i = 0; i < table.Columns.Count; i++)
        {
            sb.Append("---|");
        }
        sb.AppendLine();

        foreach (var row in table.Rows)
        {
            sb.Append('|');
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var cell = i < row.Count ? row[i] : string.Empty;
                sb.Append(' ').Append(EscapePipe(cell)).Append(" |");
            }
            sb.AppendLine();
        }
    }

    private static void RenderChartAsTable(StringBuilder sb, ChartSection chart)
    {
        sb.Append('|').Append(' ').Append(chart.XAxisLabel ?? "Category").Append(" |");
        foreach (var series in chart.Series)
        {
            sb.Append(' ').Append(EscapePipe(series.Name)).Append(" |");
        }
        sb.AppendLine();

        sb.Append('|');
        for (var i = 0; i <= chart.Series.Count; i++)
        {
            sb.Append("---|");
        }
        sb.AppendLine();

        for (var rowIndex = 0; rowIndex < chart.Categories.Count; rowIndex++)
        {
            sb.Append('|').Append(' ').Append(EscapePipe(chart.Categories[rowIndex])).Append(" |");
            foreach (var series in chart.Series)
            {
                var value = rowIndex < series.Values.Count ? series.Values[rowIndex] : 0d;
                sb.Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append(" |");
            }
            sb.AppendLine();
        }
    }

    private static string EscapePipe(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
