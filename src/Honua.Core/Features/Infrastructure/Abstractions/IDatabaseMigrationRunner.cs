// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Runs database schema migrations for the configured data store.
/// </summary>
public interface IDatabaseMigrationRunner
{
    /// <summary>
    /// Builds a point-in-time migration plan without applying scripts.
    /// </summary>
    /// <param name="connectionString">Connection string for the target database.</param>
    /// <param name="migrationsAssembly">Assembly containing embedded migration scripts.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Migration plan for the current database state.</returns>
    Task<DatabaseMigrationPlan> PlanMigrationsAsync(
        string connectionString,
        Assembly migrationsAssembly,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs database migrations using the provided connection string and migrations assembly.
    /// </summary>
    /// <param name="connectionString">Connection string for the target database.</param>
    /// <param name="migrationsAssembly">Assembly containing embedded migration scripts.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the migration run.</returns>
    Task<DatabaseMigrationResult> RunMigrationsAsync(
        string connectionString,
        Assembly migrationsAssembly,
        CancellationToken cancellationToken = default);
}
