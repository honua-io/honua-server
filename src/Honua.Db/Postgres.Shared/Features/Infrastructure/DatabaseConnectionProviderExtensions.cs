// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Db.Postgres.Features.Infrastructure;

internal static class DatabaseConnectionProviderExtensions
{
    public static async Task<NpgsqlConnectionLease> OpenNpgsqlConnectionAsync(
        this IAdoNetDatabaseConnectionProvider connectionProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);

        if (PostgresMutationTransaction.TryBorrow(connectionProvider, out var borrowed))
        {
            return borrowed;
        }

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
