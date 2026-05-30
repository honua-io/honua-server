// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Reporting.Domain;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Server.Features.Protocols.Mcp.Models;

namespace Honua.Server.Features.Protocols.Mcp.Resources;

/// <summary>
/// MCP resource for <c>honua://jobs/{jobId}/report</c>. Delegates to
/// <see cref="IAnalysisReportService"/> so the resource preserves the same
/// authorization and not-found semantics as the underlying job-results
/// resource.
/// </summary>
internal sealed class AnalysisReportResource : IMcpResource
{
    public const string Template =
        McpResourceUris.JobsPrefix + "{jobId}" + McpResourceUris.JobReportSuffix;

    private readonly IAnalysisReportService _reportService;

    private readonly ILogger<AnalysisReportResource> _logger;

    public AnalysisReportResource(
        IAnalysisReportService reportService,
        ILogger<AnalysisReportResource> logger)
    {
        _reportService = reportService;
        _logger = logger;
    }

    public string Family => McpTelemetry.ResourceFamily.JobReports;

    public IReadOnlyList<McpResourceDescriptor> Describe() => [];

    public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() => new[]
    {
        new McpResourceTemplateDescriptor
        {
            UriTemplate = Template,
            Name = "Geoprocessing job report",
            Description = "AnalysisReport envelope for a terminal job, derived from the persisted result package.",
            MimeType = McpResourceHelpers.JsonMimeType
        }
    };

    public bool CanHandle(string uri)
    {
        if (!uri.StartsWith(McpResourceUris.JobsPrefix, StringComparison.Ordinal) ||
            !uri.EndsWith(McpResourceUris.JobReportSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var idSegment = uri.AsSpan(
            McpResourceUris.JobsPrefix.Length,
            uri.Length - McpResourceUris.JobsPrefix.Length - McpResourceUris.JobReportSuffix.Length);
        return idSegment.Length > 0 && !idSegment.Contains('/');
    }

    public async Task<McpResourcesReadResult> ReadAsync(
        HttpContext httpContext,
        string uri,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetJobReport");
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);

        var jobId = uri.Substring(
            McpResourceUris.JobsPrefix.Length,
            uri.Length - McpResourceUris.JobsPrefix.Length - McpResourceUris.JobReportSuffix.Length);
        McpLog.ResourceRead(_logger, Family, uri);

        var report = await _reportService
            .GetReportAsync(jobId, principal, cancellationToken)
            .ConfigureAwait(false);

        var wire = ToWire(report);
        return McpResourceHelpers.SingleJsonContent(uri, wire, McpJsonContext.Default.McpAnalysisReport);
    }

    private static McpAnalysisReport ToWire(AnalysisReport report) => new()
    {
        ReportId = report.ReportId,
        ReportContractVersion = report.ReportContractVersion,
        JobId = report.JobId,
        ResultPackageId = report.ResultPackageId,
        ProcessId = report.ProcessId,
        ProcessFamily = report.ProcessFamily,
        TemplateId = report.TemplateId,
        TemplateVersion = report.TemplateVersion,
        SummaryTitle = report.Summary.Title,
        SummaryDescription = report.Summary.Description,
        NarrativeMode = NarrativeModeTag(report.NarrativeMode),
        GeneratedAt = report.GeneratedAt,
        Assumptions = report.Assumptions,
        Sections = report.Sections,
        RenderUris = new McpAnalysisReportRenderUris
        {
            Markdown = $"/api/v1/analysis/reports/{report.JobId}/render?format=md",
            Html = $"/api/v1/analysis/reports/{report.JobId}/render?format=html"
        }
    };

    private static string NarrativeModeTag(NarrativeMode mode) => mode switch
    {
        NarrativeMode.LlmAssisted => ReportingConstants.NarrativeModeLlmAssistedTag,
        NarrativeMode.FallbackFromLlmError => ReportingConstants.NarrativeModeFallbackFromLlmErrorTag,
        _ => ReportingConstants.NarrativeModeDeterministicTag
    };
}
