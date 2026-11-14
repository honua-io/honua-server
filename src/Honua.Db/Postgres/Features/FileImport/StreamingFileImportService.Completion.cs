// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    private void ReportCommittedCompletion(IProgress<ImportProgress>? progress, ImportProgress value)
    {
        try
        {
            progress?.Report(value);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ImportLog.CompletionNotificationFailed(_logger, exception, value.JobId);
        }
    }

    private static partial class ImportLog
    {
        [LoggerMessage(
            EventId = 7448,
            Level = LogLevel.Warning,
            Message = "Completion notification failed after import {JobId} committed")]
        public static partial void CompletionNotificationFailed(ILogger logger, Exception exception, string jobId);
    }
}
