// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using Npgsql;

namespace Honua.Db.Postgres.Features.Infrastructure;

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
