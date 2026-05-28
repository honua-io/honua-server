// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    private static partial class ImportLog
    {
        [LoggerMessage(
            EventId = 7400,
            Level = LogLevel.Information,
            Message = "Import started {JobId} table={TableName} format={Format} mode={Mode} bytes={TotalBytes}")]
        public static partial void ImportStarted(
            ILogger logger,
            string jobId,
            string tableName,
            string format,
            string mode,
            long? totalBytes);

        [LoggerMessage(
            EventId = 7401,
            Level = LogLevel.Information,
            Message = "Import completed {JobId} table={TableName} format={Format} mode={Mode} imported={Imported} failed={Failed} durationMs={DurationMs:F2} bytes={BytesRead}")]
        public static partial void ImportCompleted(
            ILogger logger,
            string jobId,
            string tableName,
            string format,
            string mode,
            int imported,
            int failed,
            double durationMs,
            long? bytesRead);

        [LoggerMessage(
            EventId = 7402,
            Level = LogLevel.Warning,
            Message = "Import cancelled {JobId} table={TableName} format={Format} mode={Mode} imported={Imported} failed={Failed} durationMs={DurationMs:F2} bytes={BytesRead}")]
        public static partial void ImportCancelled(
            ILogger logger,
            string jobId,
            string tableName,
            string format,
            string mode,
            int imported,
            int failed,
            double durationMs,
            long? bytesRead);

        [LoggerMessage(
            EventId = 7403,
            Level = LogLevel.Error,
            Message = "Import failed {JobId} table={TableName} format={Format} mode={Mode} imported={Imported} failed={Failed} error={ErrorMessage} durationMs={DurationMs:F2} bytes={BytesRead}")]
        public static partial void ImportFailed(
            ILogger logger,
            string jobId,
            string tableName,
            string format,
            string mode,
            int imported,
            int failed,
            string errorMessage,
            double durationMs,
            long? bytesRead);

        [LoggerMessage(
            EventId = 7407,
            Level = LogLevel.Warning,
            Message = "Import failed with exception {JobId} table={TableName}")]
        public static partial void ImportFailedWithException(
            ILogger logger,
            Exception exception,
            string jobId,
            string tableName);

        [LoggerMessage(
            EventId = 7408,
            Level = LogLevel.Debug,
            Message = "Feature insert failed table={TableName}")]
        public static partial void FeatureInsertFailed(
            ILogger logger,
            Exception exception,
            string tableName);

        [LoggerMessage(
            EventId = 7415,
            Level = LogLevel.Debug,
            Message = "Spilled non-seekable stream to temp file bytes={SpilledBytes}")]
        public static partial void SpilledToTempFile(
            ILogger logger,
            long spilledBytes);
    }

    private static partial class ShapefileLog
    {
        [LoggerMessage(
            EventId = 7404,
            Level = LogLevel.Warning,
            Message = "Failed to delete temporary zip file {ZipPath}")]
        public static partial void DeleteZipFailed(ILogger logger, Exception exception, string zipPath);

        [LoggerMessage(
            EventId = 7405,
            Level = LogLevel.Warning,
            Message = "Failed to clean up shapefile scratch directory {ScratchDir}")]
        public static partial void CleanupScratchFailed(ILogger logger, Exception exception, string scratchDir);
    }

    private static partial class GeoPackageLog
    {
        [LoggerMessage(
            EventId = 7406,
            Level = LogLevel.Warning,
            Message = "Failed to clean up GeoPackage scratch directory {ScratchDir}")]
        public static partial void CleanupScratchFailed(ILogger logger, Exception exception, string scratchDir);
    }

    private static partial class KmzLog
    {
        [LoggerMessage(
            EventId = 7409,
            Level = LogLevel.Warning,
            Message = "Failed to delete temporary KMZ file {ZipPath}")]
        public static partial void DeleteZipFailed(ILogger logger, Exception exception, string zipPath);

        [LoggerMessage(
            EventId = 7410,
            Level = LogLevel.Warning,
            Message = "Failed to clean up KMZ scratch directory {ScratchDir}")]
        public static partial void CleanupScratchFailed(ILogger logger, Exception exception, string scratchDir);
    }

    private static partial class FileGdbLog
    {
        [LoggerMessage(
            EventId = 7411,
            Level = LogLevel.Warning,
            Message = "Failed to clean up FileGDB scratch directory {ScratchDir}")]
        public static partial void CleanupScratchFailed(ILogger logger, Exception exception, string scratchDir);
    }

    private static partial class GeoParquetLog
    {
        [LoggerMessage(
            EventId = 7440,
            Level = LogLevel.Warning,
            Message = "Failed to clean up GeoParquet scratch directory {ScratchDir}")]
        public static partial void CleanupScratchFailed(ILogger logger, Exception exception, string scratchDir);

        [LoggerMessage(
            EventId = 7441,
            Level = LogLevel.Warning,
            Message = "GeoParquet file has {RowGroupCount} row group(s) with largest group containing {MaxGroupRows} rows. " +
                      "Row groups exceeding the per-group limit will materialize too much data in memory during import")]
        public static partial void LargeRowGroupDetected(ILogger logger, int rowGroupCount, long maxGroupRows);
    }
}
