// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Geoprocessing;

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
