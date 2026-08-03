// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Npgsql;

namespace Honua.Postgres.Features.Raster;

/// <summary>
/// Marker owner for the raster worker's dedicated pool. It is deliberately not registered as
/// <see cref="NpgsqlDataSource"/>, so provider work cannot resolve or consume the serving pool.
/// </summary>
internal sealed class PostgisRasterDataSource : IAsyncDisposable, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    private PostgisRasterDataSource(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public string ConnectionString => _dataSource.ConnectionString;

    internal NpgsqlDataSource DataSourceForResilience => _dataSource;

    public static PostgisRasterDataSource Create(
        string connectionString,
        PostgisRasterExecutionOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(options);

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.ConnectionStringBuilder.Pooling = true;
        builder.ConnectionStringBuilder.MinPoolSize = 0;
        builder.ConnectionStringBuilder.MaxPoolSize = options.MaxConcurrency;
        builder.ConnectionStringBuilder.Multiplexing = false;
        builder.ConnectionStringBuilder.NoResetOnClose = false;
        builder.ConnectionStringBuilder.ApplicationName = "honua-raster-postgis-worker";
        builder.ConnectionStringBuilder.CommandTimeout = Math.Max(
            1,
            (int)Math.Ceiling(options.StatementTimeout.TotalSeconds));
        builder.ConnectionStringBuilder.Timeout = Math.Max(
            1,
            (int)Math.Ceiling(options.QueueTimeout.TotalSeconds));
        return new PostgisRasterDataSource(builder.Build());
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

    public void Dispose() => _dataSource.Dispose();

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
