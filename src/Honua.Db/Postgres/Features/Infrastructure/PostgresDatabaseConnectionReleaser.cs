// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Db.Postgres.Features.Infrastructure;

/// <summary>
/// PostgreSQL <see cref="IDatabaseConnectionReleaser"/>: clears the Npgsql pool of every data
/// source <see cref="PostgresDataSourceFactory"/> has built (the catalog data source and every
/// cached per-connection data source) and the connection-string pools bare connections use.
/// </summary>
internal sealed class PostgresDatabaseConnectionReleaser : IDatabaseConnectionReleaser
{
    /// <inheritdoc />
    public ValueTask ReleasePooledConnectionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PostgresDataSourceFactory.ReleasePooledConnections();
        return ValueTask.CompletedTask;
    }
}
