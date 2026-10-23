// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geoprocessing;

/// <summary>
/// Source-generated logs for the shared execution-admission lease.
/// Event IDs 8027-8030. The other admission logs stay in
/// <c>Honua.Geoprocessing.ExecutionAdmissionLog</c> (8020-8026, 8031-8032).
/// </summary>
internal static partial class ExecutionAdmissionLeaseLog
{
    [LoggerMessage(8027, LogLevel.Warning,
        "Shared execution-admission lease unavailable; rejecting the submission with backpressure.")]
    public static partial void SharedLeaseUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(8028, LogLevel.Warning,
        "Shared execution-admission lease still contended after {WaitedMilliseconds} ms; rejecting the submission with backpressure.")]
    public static partial void SharedLeaseContended(ILogger logger, long waitedMilliseconds);

    [LoggerMessage(8029, LogLevel.Warning,
        "Shared execution-admission lease expired before the job record was created; rejecting the submission with backpressure.")]
    public static partial void SharedLeaseLost(ILogger logger);

    [LoggerMessage(8030, LogLevel.Warning,
        "Releasing the shared execution-admission lease failed; it expires with its TTL.")]
    public static partial void SharedLeaseReleaseFailed(ILogger logger, Exception exception);
}
