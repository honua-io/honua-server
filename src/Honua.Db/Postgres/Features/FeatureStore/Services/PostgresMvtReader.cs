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
        // A missing catalog grid fails here. Do not retry the tile with a null transform.
        // The grid error can surface on execute or on the first read, depending on
        // when PostGIS evaluates the pipeline. Either way it stays a client error.
        NpgsqlDataReader reader;
        try
        {
            reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (DatumGridException.IsMissingGrid(ex))
        {
            throw DatumGridException.MissingGrid(ex);
        }

        await using (reader)
        {
            try
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                    await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                var encodedBytes = reader.GetBytes(0, 0, null, 0, 0);
                if (encodedBytes > maxTileSize)
                {
                    throw new TileSizeLimitExceededException(encodedBytes, maxTileSize);
                }

                return await reader.GetFieldValueAsync<byte[]>(0, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (DatumGridException.IsMissingGrid(ex))
            {
                throw DatumGridException.MissingGrid(ex);
            }
        }
    }
}
