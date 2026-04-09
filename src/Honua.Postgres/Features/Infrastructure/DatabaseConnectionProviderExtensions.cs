// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Postgres.Features.Infrastructure;

internal static class DatabaseConnectionProviderExtensions
{
    /// <summary>
    /// Opens a database connection and returns a lease that keeps the original
    /// <see cref="System.Data.Common.DbConnection"/> wrapper alive. The returned
    /// lease implicitly converts to <see cref="Npgsql.NpgsqlConnection"/> so
    /// Npgsql-specific call sites continue to work, while disposing the lease
    /// runs any wrapper-level release callbacks (for example the query
    /// concurrency gate slot release in <see cref="SemaphoreReleasingConnection"/>).
    /// </summary>
    public static async Task<NpgsqlConnectionLease> OpenNpgsqlConnectionAsync(
        this IDatabaseConnectionProvider connectionProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);

        var dbConnection = await connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new NpgsqlConnectionLease(dbConnection, dbConnection.RequireNpgsqlConnection());
        }
        catch
        {
            await dbConnection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
