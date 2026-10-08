// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Db.Postgres.Features.Infrastructure.Caching;
using Honua.Db.Postgres.Features.Security;

namespace Honua.Db.Postgres.Features.Infrastructure;

/// <summary>
/// Opens source-bound pools through shared admission, retry, timeout and tracking
/// behavior while retaining the source's own search path. The singleton cache owns pools.
/// </summary>
internal sealed class PostgresBoundConnectionProvider(
    SecureConnectionDataSourceCache dataSources,
    CachingDatabaseConnectionProvider connections)
{
    public async Task<NpgsqlConnectionLease> OpenConnectionAsync(
        string connectionId, string connectionString, CancellationToken cancellationToken = default)
    {
        // Stable IDs allow credential rotation to retire the previous pool. Legacy bindings
        // without an ID are isolated by a digest; never put credentials in a cache identifier.
        var key = string.IsNullOrWhiteSpace(connectionId)
            ? "bound-string:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connectionString)))
            : "bound-id:" + connectionId;
        var connection = await connections.OpenConnectionAsync(
            () =>
            {
                var acquisition = dataSources.Acquire(key, connectionString, preservePrimarySchema: false);
                return (acquisition.DataSource, acquisition);
            }, cancellationToken).ConfigureAwait(false);
        return new NpgsqlConnectionLease(connection, connection.RequireNpgsqlConnection());
    }
}
