// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Reporting;

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
