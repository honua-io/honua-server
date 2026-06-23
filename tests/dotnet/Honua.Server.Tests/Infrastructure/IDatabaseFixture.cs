// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Npgsql;

namespace Honua.Server.Tests.Infrastructure;

/// <summary>
/// Abstraction for database test fixtures to avoid direct infrastructure dependencies in Server tests.
/// This maintains Clean Architecture by keeping Server tests independent of specific database implementations.
/// </summary>
public interface IDatabaseFixture : IAsyncLifetime
{
    /// <summary>
    /// Gets the NpgsqlDataSource for database operations.
    /// </summary>
    NpgsqlDataSource DataSource { get; }

    /// <summary>
    /// Creates an isolated schema for test execution.
    /// </summary>
    /// <param name="testClassName">Name of the test class for schema naming</param>
    /// <returns>Schema name to use for the test</returns>
    Task<string> CreateIsolatedSchemaAsync(string testClassName);

    /// <summary>
    /// Drops an isolated schema after test completion.
    /// </summary>
    /// <param name="schemaName">Schema name to drop</param>
    Task DropSchemaAsync(string schemaName);

    /// <summary>
    /// Executes raw SQL in a specific schema for test setup.
    /// </summary>
    /// <param name="sql">SQL to execute</param>
    /// <param name="schemaName">Schema to execute in (optional)</param>
    Task ExecuteAsync(string sql, string? schemaName = null);

    /// <summary>
    /// Creates a test-owned schema under the shared seed advisory lock (honua-server#1568 residual).
    /// Use this instead of a bare <c>ExecuteAsync("CREATE SCHEMA ...")</c> so per-test namespace DDL
    /// serializes with locked teardown/seed paths rather than racing a concurrent collection's
    /// <c>DROP SCHEMA ... CASCADE</c> on the shared catalog and deadlocking (40P01).
    /// </summary>
    /// <param name="schemaName">Schema to create.</param>
    Task CreateSchemaUnderLockAsync(string schemaName);

    /// <summary>
    /// Executes a schema/catalog-mutating DDL statement (e.g. <c>DROP TABLE public.*</c>) under the
    /// shared seed advisory lock (honua-server#1568 residual). Use for cleanup DDL touching the
    /// process-shared <c>public</c>/<c>honua</c> namespaces so it serializes with the locked
    /// schema-mutation surface rather than racing the catalog.
    /// </summary>
    /// <param name="sql">DDL to apply.</param>
    /// <param name="schemaName">Optional schema to set on <c>search_path</c> first.</param>
    Task ExecuteDdlUnderLockAsync(string sql, string? schemaName = null);

    /// <summary>
    /// Applies a raw seed/setup script that mutates the literal, process-global <c>honua</c>
    /// schema while holding the shared seed advisory lock (honua-server#1568, signature 2).
    /// Use this instead of <see cref="ExecuteAsync"/> for DDL/inserts against the global
    /// <c>honua.*</c> catalog so parallel <c>[Collection("Database")]</c> tests serialize on one
    /// lock rather than deadlocking (40P01) on the shared catalog.
    /// </summary>
    /// <param name="sql">The raw seed/setup SQL to apply.</param>
    /// <param name="schemaName">Optional schema to set on <c>search_path</c> before applying the SQL.</param>
    Task ApplyGlobalSeedSqlAsync(string sql, string? schemaName = null);

    /// <summary>
    /// Runs the full embedded DbUp migration set into <paramref name="schemaName"/> while holding the
    /// shared seed advisory lock (honua-server#1568 follow-up). The migration set includes
    /// <c>001_CreateHonuaSchema.sql</c>, which mutates the literal, process-global <c>honua</c> schema
    /// that per-test <c>search_path</c> isolation does NOT scope; routing it through the lock keeps
    /// parallel <c>[Collection("Database")]</c> upgrades from racing the shared catalog and deadlocking
    /// (40P01) against locked seeders. Use this instead of a bare
    /// <c>DeployChanges...PerformUpgrade()</c>.
    /// </summary>
    /// <param name="schemaName">Isolated schema to deploy into.</param>
    /// <param name="migrationsAssembly">Assembly whose embedded migration scripts are deployed.</param>
    /// <returns>The DbUp upgrade result.</returns>
    Task<DbUp.Engine.DatabaseUpgradeResult> RunEmbeddedMigrationsUnderLockAsync(
        string schemaName,
        System.Reflection.Assembly migrationsAssembly);
}
