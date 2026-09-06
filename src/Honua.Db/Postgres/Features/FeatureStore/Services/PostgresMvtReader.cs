// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using Honua.Core.Exceptions;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal static class PostgresMvtReader
{
    internal static async Task<byte[]?> ReadAsync(
        NpgsqlCommand command, long maxTileSize, CancellationToken cancellationToken)
    {
        // Sequential access keeps Npgsql from buffering the entire bytea row before
        // its length can be checked. Reject before allocating the response byte array.
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (reader.GetBytes(0, 0, null, 0, 0) > maxTileSize)
        {
            throw new TileSizeLimitExceededException();
        }

        return await reader.GetFieldValueAsync<byte[]>(0, cancellationToken).ConfigureAwait(false);
    }
}
