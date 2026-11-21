// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Infrastructure.Monitoring;

// The last successful migration plan read by DeployPreflightProbe, shared across
// scopes. Background evaluators (ops findings every minute, the health rollup every 45 seconds)
// reuse it instead of scanning every migration journal on each pass. A plan is reused only for
// the same connection string and no migration state change in this process since it was read,
// so a migration or recovery this process runs is seen at once. A failed read clears it.
internal sealed class DeployPreflightMigrationPlanCache(TimeProvider timeProvider)
{
    private Entry? _entry;

    internal DatabaseMigrationPlan? TryGet(string connectionString, int migrationTransitions, TimeSpan maxAge)
    {
        var entry = Volatile.Read(ref _entry);
        return entry is not null
            && entry.MigrationTransitions == migrationTransitions
            && string.Equals(entry.ConnectionString, connectionString, StringComparison.Ordinal)
            && timeProvider.GetUtcNow() - entry.ReadAt <= maxAge
                ? entry.Plan
                : null;
    }

    internal void Store(string connectionString, int migrationTransitions, DatabaseMigrationPlan plan)
    {
        if (!plan.Successful)
        {
            Volatile.Write(ref _entry, null);
            return;
        }

        Volatile.Write(ref _entry, new Entry
        {
            ConnectionString = connectionString,
            MigrationTransitions = migrationTransitions,
            ReadAt = timeProvider.GetUtcNow(),
            Plan = plan,
        });
    }

    private sealed class Entry
    {
        public required string ConnectionString { get; init; }

        public required int MigrationTransitions { get; init; }

        public required DateTimeOffset ReadAt { get; init; }

        public required DatabaseMigrationPlan Plan { get; init; }
    }
}
