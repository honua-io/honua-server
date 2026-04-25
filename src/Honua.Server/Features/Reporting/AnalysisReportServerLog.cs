// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Reporting;

/// <summary>
/// Source-generated structured log methods for the server-side reporting
/// orchestrator. Event-id range 8220-8229.
/// </summary>
internal static partial class AnalysisReportServerLog
{
    [LoggerMessage(8220, LogLevel.Information,
        "Analysis report rendered: ReportId={ReportId}, TemplateId={TemplateId}, Format={Format}, NarrativeMode={NarrativeMode}")]
    public static partial void ReportRendered(
        ILogger logger,
        string reportId,
        string templateId,
        string format,
        string narrativeMode);
}
