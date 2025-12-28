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
        var connections = new List<NpgsqlConnection>();
        try
        {
            // Create minimum pool connections in parallel for faster warmup
            var tasks = Enumerable.Range(0, minConnections)
                .Select(async _ =>
                {
                    var connection = await dataSource.OpenConnectionAsync(cancellationToken);
                    lock (connections)
                    {
                        connections.Add(connection);
                    }
                    return connection;
                });

            await Task.WhenAll(tasks);
        }
        finally
        {
            // Return all connections to the pool
            foreach (var connection in connections)
            {
                await connection.DisposeAsync();
            }
        }
    }
}
