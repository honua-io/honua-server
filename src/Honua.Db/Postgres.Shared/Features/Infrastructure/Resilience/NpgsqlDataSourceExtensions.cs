// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Npgsql;
using Polly;

namespace Honua.Db.Postgres.Features.Infrastructure.Resilience;

internal static class NpgsqlDataSourceExtensions
{
    public static async Task<NpgsqlConnection> OpenConnectionWithRetryAsync(
        this NpgsqlDataSource dataSource,
        Action<Exception, TimeSpan, int>? onRetry = null,
        CancellationToken cancellationToken = default)
    {
        var context = ResiliencePolicies.CreateRetryContext(onRetry);

        return await ResiliencePolicies.GetConnectionRetryPolicy(dataSource).ExecuteAsync(
            async (_) => await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false),
            context).ConfigureAwait(false);
    }

    public static async Task<T> ExecuteWithDeadlockRetryAsync<T>(
        this NpgsqlDataSource dataSource,
        Func<Task<T>> operation,
        Action<Exception, TimeSpan, int>? onRetry = null,
        CancellationToken cancellationToken = default)
    {
        var context = ResiliencePolicies.CreateRetryContext(onRetry);

        return await ResiliencePolicies.GetDeadlockRetryPolicy(dataSource).ExecuteAsync(
            async (_) => await operation().ConfigureAwait(false),
            context).ConfigureAwait(false);
    }

    public static async Task ExecuteWithDeadlockRetryAsync(
        this NpgsqlDataSource dataSource,
        Func<Task> operation,
        Action<Exception, TimeSpan, int>? onRetry = null,
        CancellationToken cancellationToken = default)
    {
        var context = ResiliencePolicies.CreateRetryContext(onRetry);

        await ResiliencePolicies.GetDeadlockRetryPolicy(dataSource).ExecuteAsync(
            async (_) => await operation().ConfigureAwait(false),
            context).ConfigureAwait(false);
    }
}
