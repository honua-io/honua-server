// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Migration;

internal sealed partial class GeoservicesImportJobExecutor
{
    private static partial class Log
    {
        [LoggerMessage(7703, LogLevel.Information, "Processing import job {JobId}")]
        public static partial void JobStarted(ILogger logger, string jobId);

        [LoggerMessage(7704, LogLevel.Information, "Import job {JobId} completed: {FeatureCount} features in {DurationSeconds:F1}s")]
        public static partial void JobCompleted(ILogger logger, string jobId, int featureCount, double durationSeconds);

        [LoggerMessage(7705, LogLevel.Warning, "Import job {JobId} failed: {ErrorMessage} (duration: {DurationSeconds:F1}s)")]
        public static partial void JobFailed(ILogger logger, string jobId, string errorMessage, double durationSeconds);

        [LoggerMessage(7706, LogLevel.Warning, "Import job {JobId} cancelled (duration: {DurationSeconds:F1}s)")]
        public static partial void JobCancelled(ILogger logger, string jobId, double durationSeconds);

        [LoggerMessage(7707, LogLevel.Error, "Import job {JobId} threw exception")]
        public static partial void JobException(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(7708, LogLevel.Warning, "Import job {JobId} has no readable request on its execution job")]
        public static partial void JobRequestNotFound(ILogger logger, string jobId);

        [LoggerMessage(7709, LogLevel.Warning, "Failed to update progress for job {JobId}")]
        public static partial void ProgressUpdateFailed(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(7712, LogLevel.Warning, "Import job {JobId} blocked by service URL validation: {ServiceUrl}")]
        public static partial void JobRejectedUnsafeServiceUrl(ILogger logger, string jobId, string serviceUrl);

        [LoggerMessage(7716, LogLevel.Warning, "Import job {JobId} requires operator review (fidelity verdict {FidelityVerdict}, duration: {DurationSeconds:F1}s)")]
        public static partial void JobNeedsReview(ILogger logger, string jobId, string fidelityVerdict, double durationSeconds);
    }
}
