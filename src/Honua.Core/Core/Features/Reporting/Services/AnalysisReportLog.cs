// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Reporting.Services;

// These event ids sit after the grounding range so a dashboard can subscribe without colliding with grounding.
internal static partial class AnalysisReportLog
{
    [LoggerMessage(8230, LogLevel.Warning,
        "Reporting narrative provider failed; falling back to deterministic text. TemplateId={TemplateId}, Exception={ExceptionType}")]
    public static partial void NarrativeFallback(
        ILogger logger,
        string templateId,
        string exceptionType);
}
