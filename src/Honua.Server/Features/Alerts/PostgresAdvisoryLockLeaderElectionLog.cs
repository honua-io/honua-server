// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Alerts;

internal static partial class PostgresAdvisoryLockLeaderElectionLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to acquire alert evaluator advisory lock.")]
    public static partial void AdvisoryLockAcquireFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Alert evaluator advisory lock is no longer held; leadership dropped.")]
    public static partial void AdvisoryLockLost(ILogger logger, Exception exception);
}
