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
    /// Gets the raw, credential-bearing connection string for the fixture database.
    /// Unlike <see cref="DataSource"/>'s <c>ConnectionString</c> (from which Npgsql redacts
    /// the password), this is suitable for tests that hand a connection string to code that
    /// opens its own connection — e.g. the external-PostGIS sink, whose real-world input is a
    /// customer-supplied connection string that carries credentials.
    /// </summary>
    string ConnectionString { get; }

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
}
