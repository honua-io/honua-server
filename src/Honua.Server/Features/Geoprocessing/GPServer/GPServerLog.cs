// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Geoprocessing.GPServer;

/// <summary>
/// Source-generated structured log methods for GPServer adapter operations.
/// Event ID range 8100-8149, separated from GeoprocessingServiceLog (8000-8099).
/// </summary>
internal static partial class GPServerLog
{
    [LoggerMessage(8100, LogLevel.Debug, "GPServer service info requested: ServiceId={ServiceId}")]
    public static partial void ServiceInfoRequested(ILogger logger, string serviceId);

    [LoggerMessage(8101, LogLevel.Debug, "GPServer task info requested: ServiceId={ServiceId}, TaskName={TaskName}")]
    public static partial void TaskInfoRequested(ILogger logger, string serviceId, string taskName);

    [LoggerMessage(8102, LogLevel.Information, "GPServer job submitted: JobId={JobId}, TaskName={TaskName}")]
    public static partial void JobSubmitted(ILogger logger, string jobId, string taskName);

    [LoggerMessage(8103, LogLevel.Debug, "GPServer job status polled: JobId={JobId}, Status={Status}")]
    public static partial void JobStatusPolled(ILogger logger, string jobId, string status);

    [LoggerMessage(8104, LogLevel.Debug, "GPServer job result requested: JobId={JobId}, ParamName={ParamName}")]
    public static partial void JobResultRequested(ILogger logger, string jobId, string paramName);

    [LoggerMessage(8105, LogLevel.Information, "GPServer job cancel requested: JobId={JobId}")]
    public static partial void JobCancelRequested(ILogger logger, string jobId);

    [LoggerMessage(8106, LogLevel.Information, "GPServer sync execute requested: TaskName={TaskName}")]
    public static partial void ExecuteRequested(ILogger logger, string taskName);

    [LoggerMessage(8107, LogLevel.Warning, "GPServer request failed: Operation={Operation}, Error={Error}")]
    public static partial void RequestFailed(ILogger logger, string operation, string error);

    [LoggerMessage(8108, LogLevel.Information, "GPServer rejected unsupported environment controls: {ControlNames}")]
    public static partial void UnsupportedEnvControlsRejected(ILogger logger, string controlNames);

    [LoggerMessage(8109, LogLevel.Warning, "GPServer job binding mismatch: JobId={JobId}, RouteService={ServiceId}, RouteTask={TaskName}")]
    public static partial void JobBindingMismatch(ILogger logger, string jobId, string serviceId, string taskName);

    [LoggerMessage(8110, LogLevel.Information, "GPServer task resolution unavailable (no process catalog): ServiceId={ServiceId}, TaskName={TaskName}")]
    public static partial void TaskResolutionUnavailable(ILogger logger, string serviceId, string taskName);

    [LoggerMessage(8111, LogLevel.Information, "GPServer service not found: ServiceId={ServiceId}")]
    public static partial void ServiceNotFound(ILogger logger, string serviceId);
}
