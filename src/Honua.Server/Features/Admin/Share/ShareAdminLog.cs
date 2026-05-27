// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Share.Domain;

namespace Honua.Server.Features.Admin.Share;

internal static partial class ShareAdminLog
{
    [LoggerMessage(EventId = 121600, Level = LogLevel.Information, Message = "Share export definition {ExportId} created for {ServiceName}/{LayerId} with destination status {DestinationStatus}.")]
    public static partial void ExportDefinitionCreated(
        ILogger logger,
        string exportId,
        string serviceName,
        int layerId,
        ShareExportDestinationStatus destinationStatus);

    [LoggerMessage(EventId = 121601, Level = LogLevel.Information, Message = "Share export run {RunId} triggered for definition {ExportId}; jobRunId={JobRunId}.")]
    public static partial void ExportRunTriggered(
        ILogger logger,
        string runId,
        string exportId,
        string? jobRunId);

    [LoggerMessage(EventId = 121602, Level = LogLevel.Warning, Message = "Share export definition {ExportId} cannot run because destination status is {DestinationStatus}.")]
    public static partial void ExportDestinationUnavailable(
        ILogger logger,
        string exportId,
        ShareExportDestinationStatus destinationStatus);

    [LoggerMessage(EventId = 121603, Level = LogLevel.Error, Message = "Share admin endpoint {Operation} failed.")]
    public static partial void EndpointFailed(
        ILogger logger,
        string operation,
        Exception exception);

    [LoggerMessage(EventId = 121604, Level = LogLevel.Error, Message = "Share export run {RunId} for definition {ExportId} failed to dispatch; job {JobRunId} was rolled back.")]
    public static partial void ExportDispatchFailed(
        ILogger logger,
        string runId,
        string exportId,
        string jobRunId,
        Exception exception);

    [LoggerMessage(EventId = 121605, Level = LogLevel.Error, Message = "Share export run {RunId} for definition {ExportId} could not be persisted before dispatch; job {JobRunId} was rolled back.")]
    public static partial void ExportRunPersistFailed(
        ILogger logger,
        string runId,
        string exportId,
        string jobRunId,
        Exception exception);

    [LoggerMessage(EventId = 121606, Level = LogLevel.Warning, Message = "Share export run {RunId} for definition {ExportId} could not be reconciled to terminal status {Status} from job {JobRunId}.")]
    public static partial void ExportRunReconcileFailed(
        ILogger logger,
        string runId,
        string exportId,
        ShareExportRunStatus status,
        string jobRunId,
        Exception exception);
}
