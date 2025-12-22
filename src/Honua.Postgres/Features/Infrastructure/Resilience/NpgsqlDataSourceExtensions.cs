// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Npgsql;
using Polly;

namespace Honua.Postgres.Features.Infrastructure.Resilience;

/// <summary>
/// Extension methods for NpgsqlDataSource to add resilience policies
/// </summary>
internal static class NpgsqlDataSourceExtensions
{
    /// <summary>
    /// Opens a connection with retry policy for transient connection errors
    /// </summary>
    /// <param name="dataSource">The Npgsql data source</param>
    /// <param name="onRetry">Optional callback for retry events (for logging)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Open database connection</returns>
    public static async Task<NpgsqlConnection> OpenConnectionWithRetryAsync(
        this NpgsqlDataSource dataSource,
        Action<Exception, TimeSpan, int>? onRetry = null,
        CancellationToken cancellationToken = default)
    {
        IAsyncPolicy retryPolicy = ResiliencePolicies.GetConnectionRetryPolicy(onRetry);

        return await retryPolicy.ExecuteAsync(async () =>
            await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }
}
