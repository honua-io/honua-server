// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Alerts;

internal static partial class PostgresAdvisoryLockLeaderElectionLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to acquire alert evaluator advisory lock.")]
    public static partial void AdvisoryLockAcquireFailed(ILogger logger, Exception exception);
}
