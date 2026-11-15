// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using DbUp.Engine;
using Npgsql;

namespace Honua.Db.Postgres.Features.Infrastructure.Migrations;

internal static class PostgresMigrationDeadlockRetry
{
    private const int MaximumAttempts = 3;
    private static readonly TimeSpan _retryDelay = TimeSpan.FromMilliseconds(100);

    internal static async Task<DatabaseUpgradeResult> RunAsync(Func<UpgradeEngine> createUpgrader, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = createUpgrader().PerformUpgrade();
            if (result.Successful || attempt == MaximumAttempts || !IsDeadlock(result.Error))
            {
                return result;
            }

            // DbUp has rolled back the scripts and journal. Rebuild its cached journal state too.
            await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsDeadlock(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected })
            {
                return true;
            }
        }

        return false;
    }
}
