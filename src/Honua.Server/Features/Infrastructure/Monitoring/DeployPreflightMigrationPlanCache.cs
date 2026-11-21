// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// The last successful migration plan read by <see cref="DeployPreflightProbe"/>, shared across
/// scopes. Background evaluators (ops findings every minute, the health rollup every 45 seconds)
/// reuse it instead of scanning every migration journal on each pass. A plan is reused only for
/// the same connection string and the same in-process migration status, so a migration this
/// process runs is seen at once.
/// </summary>
internal sealed class DeployPreflightMigrationPlanCache(TimeProvider timeProvider)
{
    private Entry? _entry;

    internal DatabaseMigrationPlan? TryGet(string connectionString, MigrationLifecycleStatus status, TimeSpan maxAge)
    {
        var entry = Volatile.Read(ref _entry);
        return entry is not null
            && entry.Status == status
            && string.Equals(entry.ConnectionString, connectionString, StringComparison.Ordinal)
            && timeProvider.GetUtcNow() - entry.ReadAt <= maxAge
                ? entry.Plan
                : null;
    }

    internal void Store(string connectionString, MigrationLifecycleStatus status, DatabaseMigrationPlan plan)
    {
        if (plan.Successful)
        {
            Volatile.Write(ref _entry, new Entry
            {
                ConnectionString = connectionString,
                Status = status,
                ReadAt = timeProvider.GetUtcNow(),
                Plan = plan,
            });
        }
    }

    private sealed class Entry
    {
        public required string ConnectionString { get; init; }

        public required MigrationLifecycleStatus Status { get; init; }

        public required DateTimeOffset ReadAt { get; init; }

        public required DatabaseMigrationPlan Plan { get; init; }
    }
}
