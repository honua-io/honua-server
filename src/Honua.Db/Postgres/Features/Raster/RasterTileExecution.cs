// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Db.Postgres.Features.Infrastructure;

namespace Honua.Db.Postgres.Features.Raster;

/// <summary>Executes bounded tile SQL without compiling unused sampler fallback branches.</summary>
internal static class RasterTileExecution
{
    internal static async Task DisableJitAsync(
        DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var settings = connection.CreateCommand();
        settings.Transaction = transaction;
        // Planning sees every fallback, even when COALESCE selects the native fast path.
        // LLVM compilation can cost seconds for a subsecond raster operation. LOCAL keeps
        // this execution policy inside the tile/import transaction and out of pooled sessions.
        settings.CommandText = "SET LOCAL jit = off";
        await settings.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<object?> ExecuteScalarAsync(DbCommand command, CancellationToken cancellationToken)
    {
        var connection = command.Connection ?? throw new InvalidOperationException("Tile command requires a connection.");
        if (command.Transaction is { } existing)
        {
            await DisableJitAsync(connection, existing, cancellationToken).ConfigureAwait(false);
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        command.Transaction = transaction;
        try
        {
            await DisableJitAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            command.Transaction = null;
        }
    }
}
