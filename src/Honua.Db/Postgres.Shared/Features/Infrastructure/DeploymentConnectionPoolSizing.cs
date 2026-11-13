// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace Honua.Db.Postgres.Features.Infrastructure;

// The serving defaults (max 200, min 20 per pool) suit one task on a large database. Several tasks on a small instance
// exhaust its connection limit, and every task holds its idle floor. When the deployment states its task count or its
// database size, each task's pool is sized from the database's limit; values the operator set are left alone.
internal static class DeploymentConnectionPoolSizing
{
    internal const string DatabaseInstanceClassKey = "Database:InstanceClass";

    internal const string DatabaseMaxConnectionsKey = "Database:MaxConnections";

    // PostgreSQL's own max_connections default, assumed when only the task count is known.
    internal const int UnstatedMaxConnections = 100;

    // The rest stays free for migrations, maintenance sessions and the database's reserved superuser slots.
    internal const double ApplicationShare = 0.8;

    // A task's data-source pool, plus the pool behind code that opens connections from its connection string.
    internal const int PoolsPerTask = 2;

    internal const int SmallestPool = 4;

    internal const int IdleConnectionsPerPool = 2;

    // RDS sets max_connections to instance memory / 9531392 bytes (about 112 per GiB, less what the OS keeps), capped at 5000.
    private const int ConnectionsPerGibibyte = 90;

    private const int RdsMaxConnectionsCeiling = 5000;

    private static readonly Dictionary<string, int> BurstableMemoryGibibytes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["micro"] = 1,
        ["small"] = 2,
        ["medium"] = 4,
        ["large"] = 8,
        ["xlarge"] = 16,
        ["2xlarge"] = 32,
    };

    internal static void Apply(ConnectionLimits limits, IConfigurationSection section, IConfiguration configuration)
    {
        var tasks = DeploymentTaskCount.Resolve(configuration);
        var maxConnections = ResolveMaxConnections(configuration);
        if (tasks <= 1 && maxConnections is null)
        {
            return;
        }

        var perPool = (int)((maxConnections ?? UnstatedMaxConnections) * ApplicationShare) / (tasks * PoolsPerTask);
        if (IsUnset(section, nameof(ConnectionLimits.MaxConnectionPoolSize)))
        {
            limits.MaxConnectionPoolSize = Math.Clamp(perPool, SmallestPool, limits.MaxConnectionPoolSize);
        }

        if (IsUnset(section, nameof(ConnectionLimits.MinConnectionPoolSize)))
        {
            limits.MinConnectionPoolSize = Math.Min(IdleConnectionsPerPool, limits.MaxConnectionPoolSize);
        }

        if (IsUnset(section, nameof(ConnectionLimits.MaxConcurrentQueries)))
        {
            limits.MaxConcurrentQueries = Math.Min(limits.MaxConcurrentQueries, limits.MaxConnectionPoolSize);
        }
    }

    internal static int? ResolveMaxConnections(IConfiguration configuration)
    {
        if (int.TryParse(configuration[DatabaseMaxConnectionsKey], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stated)
            && stated > 0)
        {
            return stated;
        }

        return MemoryGibibytes(configuration[DatabaseInstanceClassKey]) is { } memory
            ? Math.Min(RdsMaxConnectionsCeiling, memory * ConnectionsPerGibibyte)
            : null;
    }

    // db.<family><generation>[options].<size>, for example db.t4g.small, db.m7g.large or db.r6i.2xlarge.
    internal static int? MemoryGibibytes(string? instanceClass)
    {
        var parts = instanceClass?.Trim().ToLowerInvariant().Split('.');
        if (parts is not ["db", { Length: > 0 } family, { Length: > 0 } size])
        {
            return null;
        }

        return family[0] switch
        {
            't' => BurstableMemoryGibibytes.TryGetValue(size, out var memory) ? memory : null,
            'm' => ScaledMemory(size, largeGibibytes: 8),
            'r' => ScaledMemory(size, largeGibibytes: 16),
            _ => null,
        };
    }

    // General-purpose and memory-optimised sizes double from large: xlarge is twice large, Nxlarge is N times xlarge.
    private static int? ScaledMemory(string size, int largeGibibytes)
    {
        if (size.Equals("large", StringComparison.OrdinalIgnoreCase))
        {
            return largeGibibytes;
        }

        if (!size.EndsWith("xlarge", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var multiplier = size[..^"xlarge".Length];
        if (multiplier.Length == 0)
        {
            return largeGibibytes * 2;
        }

        return int.TryParse(multiplier, NumberStyles.None, CultureInfo.InvariantCulture, out var factor) && factor > 0
            ? largeGibibytes * 2 * factor
            : null;
    }

    private static bool IsUnset(IConfigurationSection section, string key)
        => string.IsNullOrWhiteSpace(section[key]);
}
