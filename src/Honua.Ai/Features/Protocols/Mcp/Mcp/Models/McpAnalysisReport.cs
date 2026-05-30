// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Reporting.Domain;

namespace Honua.Ai.Protocols.Mcp.Models;

/// <summary>
/// MCP wire envelope for <c>honua://jobs/{jobId}/report</c>. Mirrors
/// <see cref="AnalysisReport"/> verbatim — the polymorphic section list reuses
/// the Core <see cref="AnalysisReportSection"/> types so the wire shape stays
/// in lock-step with the contract version.
/// </summary>
internal sealed class McpAnalysisReport
{
    [JsonPropertyName("reportId")]
    public string ReportId { get; set; } = string.Empty;

    [JsonPropertyName("reportContractVersion")]
    public string ReportContractVersion { get; set; } = string.Empty;

    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("resultPackageId")]
    public string ResultPackageId { get; set; } = string.Empty;

    [JsonPropertyName("processId")]
    public string ProcessId { get; set; } = string.Empty;

    [JsonPropertyName("processFamily")]
    public string ProcessFamily { get; set; } = string.Empty;

    [JsonPropertyName("templateId")]
    public string TemplateId { get; set; } = string.Empty;

    [JsonPropertyName("templateVersion")]
    public string TemplateVersion { get; set; } = string.Empty;

    [JsonPropertyName("summaryTitle")]
    public string SummaryTitle { get; set; } = string.Empty;

    [JsonPropertyName("summaryDescription")]
    public string? SummaryDescription { get; set; }

    [JsonPropertyName("narrativeMode")]
    public string NarrativeMode { get; set; } = string.Empty;

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; set; }

    [JsonPropertyName("assumptions")]
    public IReadOnlyList<string> Assumptions { get; set; } = [];

    [JsonPropertyName("sections")]
    public IReadOnlyList<AnalysisReportSection> Sections { get; set; } = [];

    [JsonPropertyName("renderUris")]
    public McpAnalysisReportRenderUris RenderUris { get; set; } = new();
}

/// <summary>
/// Pointers to the Markdown / HTML render endpoints so an MCP client can
/// dereference rendered output directly.
/// </summary>
internal sealed class McpAnalysisReportRenderUris
{
    [JsonPropertyName("markdown")]
    public string? Markdown { get; set; }

    [JsonPropertyName("html")]
    public string? Html { get; set; }
}
