// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Reporting.Domain;

namespace Honua.Core.Features.Reporting.Services;

/// <summary>
/// Self-contained HTML renderer. Inlines an embedded CSS sheet, generates
/// charts as inline SVG, and never references external hosts so reports work
/// offline for on-prem operators.
/// </summary>
internal sealed class HtmlReportRenderer : IAnalysisReportRenderer
{
    private const string CssResourceName = "Honua.Core.Features.Reporting.Templates.Resources.report.css";
    private static readonly string _embeddedCss = LoadEmbeddedCss();

    /// <inheritdoc />
    public AnalysisReportFormat Format => AnalysisReportFormat.Html;

    /// <inheritdoc />
    public string Render(AnalysisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!string.Equals(report.ReportContractVersion, ReportingConstants.ContractVersionV1, StringComparison.Ordinal))
        {
            throw new UnsupportedReportContractVersionException(report.ReportContractVersion, ReportingConstants.ContractVersionV1);
        }

        var sb = new StringBuilder(capacity: 4096);
        sb.AppendLine("<!doctype html>");
        sb.Append("<html lang=\"en\" data-report-contract=\"")
            .Append(WebUtility.HtmlEncode(report.ReportContractVersion))
            .AppendLine("\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\" />");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        sb.Append("<title>").Append(WebUtility.HtmlEncode(report.Summary.Title)).AppendLine("</title>");
        sb.Append("<style>").Append(_embeddedCss).AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.Append("<body class=\"honua-report\" data-report-id=\"")
            .Append(WebUtility.HtmlEncode(report.ReportId))
            .Append("\" data-template-id=\"")
            .Append(WebUtility.HtmlEncode(report.TemplateId))
            .Append("\" data-template-version=\"")
            .Append(WebUtility.HtmlEncode(report.TemplateVersion))
            .Append("\" data-narrative-mode=\"")
            .Append(WebUtility.HtmlEncode(NarrativeModeTag(report.NarrativeMode)))
            .AppendLine("\">");

        foreach (var section in report.Sections)
        {
            RenderSection(sb, section);
        }

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static void RenderSection(StringBuilder sb, AnalysisReportSection section)
    {
        switch (section)
        {
            case HeadingSection heading:
                var level = Math.Clamp(heading.Level, 1, 6);
                sb.Append("<h").Append(level).Append('>')
                    .Append(WebUtility.HtmlEncode(heading.Text))
                    .Append("</h").Append(level).AppendLine(">");
                break;

            case ParagraphSection paragraph:
                sb.Append("<p>")
                    .Append(WebUtility.HtmlEncode(paragraph.Text))
                    .AppendLine("</p>");
                break;

            case KeyMetricSection metric:
                sb.AppendLine("<div class=\"report-key-metric\">");
                sb.Append("<div class=\"report-key-metric-label\">")
                    .Append(WebUtility.HtmlEncode(metric.Label))
                    .AppendLine("</div>");
                sb.Append("<div class=\"report-key-metric-value\">")
                    .Append(WebUtility.HtmlEncode(metric.Value));
                if (!string.IsNullOrEmpty(metric.Unit))
                {
                    sb.Append(' ').Append(WebUtility.HtmlEncode(metric.Unit));
                }
                if (!string.IsNullOrEmpty(metric.SpatialReference))
                {
                    sb.Append(" <span class=\"report-spatial-reference\">(")
                        .Append(WebUtility.HtmlEncode(metric.SpatialReference))
                        .Append(")</span>");
                }
                sb.AppendLine("</div>");
                sb.AppendLine("</div>");
                break;

            case TableSection table:
                if (!string.IsNullOrEmpty(table.Caption))
                {
                    sb.Append("<p class=\"report-caption\">")
                        .Append(WebUtility.HtmlEncode(table.Caption))
                        .AppendLine("</p>");
                }
                sb.AppendLine("<table class=\"report-table\">");
                sb.AppendLine("<thead><tr>");
                foreach (var col in table.Columns)
                {
                    sb.Append("<th>").Append(WebUtility.HtmlEncode(col)).AppendLine("</th>");
                }
                sb.AppendLine("</tr></thead>");
                sb.AppendLine("<tbody>");
                foreach (var row in table.Rows)
                {
                    sb.AppendLine("<tr>");
                    for (var i = 0; i < table.Columns.Count; i++)
                    {
                        var value = i < row.Count ? row[i] : string.Empty;
                        sb.Append("<td>").Append(WebUtility.HtmlEncode(value)).AppendLine("</td>");
                    }
                    sb.AppendLine("</tr>");
                }
                sb.AppendLine("</tbody>");
                sb.AppendLine("</table>");
                if (table.TruncatedRowCount > 0)
                {
                    sb.Append("<p class=\"report-truncated\">…and ")
                        .Append(table.TruncatedRowCount.ToString(CultureInfo.InvariantCulture))
                        .AppendLine(" additional row(s) not shown.</p>");
                }
                break;

            case ChartSection chart:
                if (!string.IsNullOrEmpty(chart.Caption))
                {
                    sb.Append("<p class=\"report-caption\">")
                        .Append(WebUtility.HtmlEncode(chart.Caption))
                        .AppendLine("</p>");
                }
                RenderInlineSvg(sb, chart);
                break;

            case MapEmbedSection map:
                sb.Append("<p class=\"report-map\">Map: <a href=\"")
                    .Append(WebUtility.HtmlEncode(map.MapPackageUri))
                    .Append("\">")
                    .Append(WebUtility.HtmlEncode(map.Caption))
                    .AppendLine("</a></p>");
                break;

            case NarrativeSection narrative:
                sb.AppendLine("<div class=\"report-narrative\">");
                sb.Append("<p>")
                    .Append(WebUtility.HtmlEncode(narrative.LlmText ?? narrative.DeterministicText))
                    .AppendLine("</p>");
                sb.Append("<div class=\"report-narrative-source\">Source: ")
                    .Append(WebUtility.HtmlEncode(NarrativeModeTag(narrative.Mode)))
                    .AppendLine("</div>");
                sb.AppendLine("</div>");
                break;

            case ProvenanceFooterSection footer:
                sb.AppendLine("<footer class=\"report-footer\">");
                sb.AppendLine("<dl>");
                AppendDefinition(sb, "Job", footer.JobId);
                AppendDefinition(sb, "Result package", footer.ResultPackageId);
                if (footer.ProcessDefinitions.Count > 0)
                {
                    AppendDefinition(sb, "Processes", string.Join(", ", footer.ProcessDefinitions));
                }
                if (footer.Sources.Count > 0)
                {
                    AppendDefinition(sb, "Sources", string.Join(", ", footer.Sources));
                }
                if (footer.ExecutedAt is { } executedAt)
                {
                    AppendDefinition(sb, "Executed at", executedAt.ToString("u", CultureInfo.InvariantCulture));
                }
                AppendDefinition(sb, "Generated at", footer.GeneratedAt.ToString("u", CultureInfo.InvariantCulture));
                sb.AppendLine("</dl>");
                sb.AppendLine("</footer>");
                break;
        }
    }

    private static void AppendDefinition(StringBuilder sb, string term, string value)
    {
        sb.Append("<dt>").Append(WebUtility.HtmlEncode(term)).AppendLine("</dt>");
        sb.Append("<dd>").Append(WebUtility.HtmlEncode(value)).AppendLine("</dd>");
    }

    private static void RenderInlineSvg(StringBuilder sb, ChartSection chart)
    {
        const int width = 480;
        const int height = 220;
        const int paddingLeft = 48;
        const int paddingRight = 16;
        const int paddingTop = 16;
        const int paddingBottom = 36;

        var plotWidth = width - paddingLeft - paddingRight;
        var plotHeight = height - paddingTop - paddingBottom;

        sb.Append("<svg class=\"report-svg\" viewBox=\"0 0 ")
            .Append(width).Append(' ').Append(height)
            .Append("\" width=\"").Append(width)
            .Append("\" height=\"").Append(height)
            .Append("\" role=\"img\" aria-label=\"")
            .Append(WebUtility.HtmlEncode(chart.Caption ?? "Chart"))
            .AppendLine("\">");

        var maxValue = MaxAbsValue(chart);
        if (maxValue <= 0d)
        {
            maxValue = 1d;
        }

        sb.Append("<line class=\"axis\" x1=\"").Append(paddingLeft)
            .Append("\" y1=\"").Append(paddingTop)
            .Append("\" x2=\"").Append(paddingLeft)
            .Append("\" y2=\"").Append(paddingTop + plotHeight)
            .AppendLine("\" />");
        sb.Append("<line class=\"axis\" x1=\"").Append(paddingLeft)
            .Append("\" y1=\"").Append(paddingTop + plotHeight)
            .Append("\" x2=\"").Append(paddingLeft + plotWidth)
            .Append("\" y2=\"").Append(paddingTop + plotHeight)
            .AppendLine("\" />");

        for (var step = 1; step <= 4; step++)
        {
            var y = paddingTop + plotHeight - (step * plotHeight / 4);
            sb.Append("<line class=\"gridline\" x1=\"").Append(paddingLeft)
                .Append("\" y1=\"").Append(y)
                .Append("\" x2=\"").Append(paddingLeft + plotWidth)
                .Append("\" y2=\"").Append(y)
                .AppendLine("\" />");
            var label = (maxValue * step / 4).ToString(CultureInfo.InvariantCulture);
            sb.Append("<text class=\"label\" x=\"").Append(paddingLeft - 6)
                .Append("\" y=\"").Append(y + 4)
                .Append("\" text-anchor=\"end\">")
                .Append(WebUtility.HtmlEncode(label))
                .AppendLine("</text>");
        }

        var categoryCount = Math.Max(chart.Categories.Count, 1);
        var slotWidth = plotWidth / (double)categoryCount;

        switch (chart.ChartKind)
        {
            case ReportChartKind.Bar:
                RenderBars(sb, chart, paddingLeft, paddingTop, plotHeight, slotWidth, maxValue);
                break;
            case ReportChartKind.Line:
                RenderLines(sb, chart, paddingLeft, paddingTop, plotHeight, slotWidth, maxValue);
                break;
        }

        for (var i = 0; i < chart.Categories.Count; i++)
        {
            var labelX = paddingLeft + ((i + 0.5) * slotWidth);
            sb.Append("<text class=\"label\" x=\"")
                .Append(labelX.ToString("F2", CultureInfo.InvariantCulture))
                .Append("\" y=\"").Append(paddingTop + plotHeight + 14)
                .Append("\" text-anchor=\"middle\">")
                .Append(WebUtility.HtmlEncode(chart.Categories[i]))
                .AppendLine("</text>");
        }

        if (!string.IsNullOrEmpty(chart.YAxisLabel))
        {
            var labelX = paddingLeft - 36;
            var labelY = paddingTop + (plotHeight / 2);
            sb.Append("<text class=\"label\" x=\"").Append(labelX)
                .Append("\" y=\"").Append(labelY)
                .Append("\" text-anchor=\"middle\" transform=\"rotate(-90 ")
                .Append(labelX).Append(' ').Append(labelY).Append(")\">")
                .Append(WebUtility.HtmlEncode(chart.YAxisLabel))
                .AppendLine("</text>");
        }

        if (!string.IsNullOrEmpty(chart.XAxisLabel))
        {
            sb.Append("<text class=\"label\" x=\"")
                .Append(paddingLeft + (plotWidth / 2))
                .Append("\" y=\"").Append(height - 4)
                .Append("\" text-anchor=\"middle\">")
                .Append(WebUtility.HtmlEncode(chart.XAxisLabel))
                .AppendLine("</text>");
        }

        sb.AppendLine("</svg>");
    }

    private static void RenderBars(
        StringBuilder sb,
        ChartSection chart,
        int paddingLeft,
        int paddingTop,
        int plotHeight,
        double slotWidth,
        double maxValue)
    {
        var seriesCount = Math.Max(chart.Series.Count, 1);
        var barSlotWidth = slotWidth / seriesCount;
        for (var seriesIndex = 0; seriesIndex < chart.Series.Count; seriesIndex++)
        {
            var series = chart.Series[seriesIndex];
            for (var categoryIndex = 0; categoryIndex < chart.Categories.Count; categoryIndex++)
            {
                var value = categoryIndex < series.Values.Count ? series.Values[categoryIndex] : 0d;
                var safeValue = Math.Abs(value);
                var barHeight = (safeValue / maxValue) * plotHeight;
                var barX = paddingLeft + (categoryIndex * slotWidth) + (seriesIndex * barSlotWidth) + 1;
                var barY = paddingTop + plotHeight - barHeight;
                sb.Append("<rect class=\"bar\" x=\"")
                    .Append(barX.ToString("F2", CultureInfo.InvariantCulture))
                    .Append("\" y=\"").Append(barY.ToString("F2", CultureInfo.InvariantCulture))
                    .Append("\" width=\"").Append((barSlotWidth - 2).ToString("F2", CultureInfo.InvariantCulture))
                    .Append("\" height=\"").Append(barHeight.ToString("F2", CultureInfo.InvariantCulture))
                    .Append("\"><title>")
                    .Append(WebUtility.HtmlEncode(series.Name))
                    .Append(": ")
                    .Append(value.ToString(CultureInfo.InvariantCulture))
                    .AppendLine("</title></rect>");
            }
        }
    }

    private static void RenderLines(
        StringBuilder sb,
        ChartSection chart,
        int paddingLeft,
        int paddingTop,
        int plotHeight,
        double slotWidth,
        double maxValue)
    {
        foreach (var series in chart.Series)
        {
            sb.Append("<polyline class=\"line\" points=\"");
            for (var categoryIndex = 0; categoryIndex < chart.Categories.Count; categoryIndex++)
            {
                var value = categoryIndex < series.Values.Count ? series.Values[categoryIndex] : 0d;
                var safeValue = Math.Abs(value);
                var x = paddingLeft + ((categoryIndex + 0.5) * slotWidth);
                var y = paddingTop + plotHeight - ((safeValue / maxValue) * plotHeight);
                if (categoryIndex > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(x.ToString("F2", CultureInfo.InvariantCulture))
                    .Append(',')
                    .Append(y.ToString("F2", CultureInfo.InvariantCulture));
            }
            sb.AppendLine("\" />");
        }
    }

    private static double MaxAbsValue(ChartSection chart)
    {
        var max = 0d;
        foreach (var series in chart.Series)
        {
            foreach (var value in series.Values)
            {
                var abs = Math.Abs(value);
                if (abs > max)
                {
                    max = abs;
                }
            }
        }
        return max;
    }

    private static string NarrativeModeTag(NarrativeMode mode) => mode switch
    {
        NarrativeMode.LlmAssisted => ReportingConstants.NarrativeModeLlmAssistedTag,
        NarrativeMode.FallbackFromLlmError => ReportingConstants.NarrativeModeFallbackFromLlmErrorTag,
        _ => ReportingConstants.NarrativeModeDeterministicTag
    };

    private static string LoadEmbeddedCss()
    {
        var assembly = typeof(HtmlReportRenderer).Assembly;
        using var stream = assembly.GetManifestResourceStream(CssResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded CSS resource '{CssResourceName}' was not found in {assembly.FullName}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
