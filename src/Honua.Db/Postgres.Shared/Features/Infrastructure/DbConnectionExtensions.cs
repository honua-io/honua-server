// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Npgsql;

namespace Honua.Postgres.Features.Infrastructure;

internal static class DbConnectionExtensions
{
    public static NpgsqlConnection RequireNpgsqlConnection(this DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection switch
        {
            NpgsqlConnection npgsqlConnection => npgsqlConnection,
            SemaphoreReleasingConnection wrappedConnection => wrappedConnection.InnerConnection,
            _ => throw new InvalidOperationException(
                $"Expected an {nameof(NpgsqlConnection)} but received {connection.GetType().FullName}.")
        };
    }
}
