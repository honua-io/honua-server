// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Npgsql;

namespace Honua.Postgres.Features.Infrastructure;

/// <summary>
/// PERFORMANCE OPTIMIZATION: Extensions for NpgsqlDataSource to optimize connection handling
/// </summary>
internal static class NpgsqlDataSourceExtensions
{
    /// <summary>
    /// Pre-warms the connection pool for better startup performance
    /// Creates and immediately returns connections to establish the minimum pool size
    /// </summary>
    /// <param name="dataSource">The data source to warm up</param>
    /// <param name="minConnections">Minimum number of connections to pre-create (default: 5)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public static async Task WarmupConnectionPoolAsync(this NpgsqlDataSource dataSource, int minConnections = 5, CancellationToken cancellationToken = default)
    {
        var connections = new NpgsqlConnection[minConnections];
        try
        {
            // Create minimum pool connections in parallel for faster warmup
            var tasks = new Task[minConnections];
            for (var i = 0; i < minConnections; i++)
            {
                var index = i;
                tasks[i] = Task.Run(async () =>
                {
                    var connection = await dataSource.OpenConnectionAsync(cancellationToken);
                    connections[index] = connection;
                }, cancellationToken);
            }

            await Task.WhenAll(tasks);
        }
        finally
        {
            // Return all connections to the pool
            foreach (var connection in connections)
            {
                if (connection != null)
                {
                    await connection.DisposeAsync();
                }
            }
        }
    }
}
