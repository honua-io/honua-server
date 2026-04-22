// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Export;

internal static partial class ExportJobServiceLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to roll back persisted export request metadata for job {JobId} after the progress record could not be created.")]
    public static partial void ProgressRollbackFailed(ILogger logger, string jobId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to persist error status for export job {JobId} after a processing failure.")]
    public static partial void FailedStatusPersistenceFailed(ILogger logger, string jobId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to persist missing-request status for export job {JobId}.")]
    public static partial void MissingRequestStatusPersistenceFailed(ILogger logger, string jobId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Skipping export recovery for job {JobId} because the Redis claim key could not be inspected.")]
    public static partial void RecoveryClaimInspectionFailed(ILogger logger, string jobId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Export job lease renewal was lost while processing a background export.")]
    public static partial void LeaseRenewalLost(ILogger logger);
}
