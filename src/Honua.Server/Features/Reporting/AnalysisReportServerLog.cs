// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Reporting;

/// <summary>
/// Source-generated structured log methods for the server-side reporting
/// orchestrator. Event-id range 8240-8249 — placed after
/// <see cref="Honua.Core.Features.Reporting.Services.AnalysisReportLog"/>
/// (8230-8239) so the reporting pipeline owns a contiguous block.
/// </summary>
internal static partial class AnalysisReportServerLog
{
    [LoggerMessage(8240, LogLevel.Information,
        "Analysis report rendered: ReportId={ReportId}, TemplateId={TemplateId}, Format={Format}, NarrativeMode={NarrativeMode}")]
    public static partial void ReportRendered(
        ILogger logger,
        string reportId,
        string templateId,
        string format,
        string narrativeMode);
}
