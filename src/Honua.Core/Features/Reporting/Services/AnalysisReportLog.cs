// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Reporting.Services;

/// <summary>
/// Source-generated structured log methods for the analysis reporting feature.
/// Event ID range 8200-8219 — appended to the existing log-id ledger.
/// </summary>
internal static partial class AnalysisReportLog
{
    [LoggerMessage(8200, LogLevel.Warning,
        "Reporting narrative provider failed; falling back to deterministic text. TemplateId={TemplateId}, Exception={ExceptionType}, Message={Message}")]
    public static partial void NarrativeFallback(
        ILogger logger,
        string templateId,
        string exceptionType,
        string message);

    [LoggerMessage(8201, LogLevel.Information,
        "Analysis report rendered: ReportId={ReportId}, TemplateId={TemplateId}, Format={Format}, NarrativeMode={NarrativeMode}")]
    public static partial void ReportRendered(
        ILogger logger,
        string reportId,
        string templateId,
        string format,
        string narrativeMode);
}
