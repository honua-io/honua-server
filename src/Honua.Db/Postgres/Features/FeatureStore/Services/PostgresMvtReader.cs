// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using Honua.Core.Exceptions;
using Honua.Core.Features.Tiles;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal static class PostgresMvtReader
{
    internal static async Task<byte[]?> ReadAsync(
        NpgsqlCommand command, long maxTileSize, CancellationToken cancellationToken)
    {
        using var pooled = await ReadPooledAsync(command, maxTileSize, cancellationToken).ConfigureAwait(false);
        return pooled is null ? null : pooled.Span.ToArray();
    }

    internal static async Task<PooledTileData?> ReadPooledAsync(
        NpgsqlCommand command, long maxTileSize, CancellationToken cancellationToken)
    {
        // Sequential access keeps Npgsql from buffering the entire bytea row before
        // its length can be checked. Reject before renting the response byte buffer.
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
                return await ReadTilePayloadAsync(reader, maxTileSize, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (DatumGridException.IsMissingGrid(ex))
            {
                throw DatumGridException.MissingGrid(ex);
            }
        }
    }

    private static async Task<PooledTileData?> ReadTilePayloadAsync(
        NpgsqlDataReader reader, long maxTileSize, CancellationToken cancellationToken)
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

        if (encodedBytes == 0)
        {
            return null;
        }

        var tile = PooledTileData.Rent(checked((int)encodedBytes));
        try
        {
            await using var stream = await reader.GetStreamAsync(0, cancellationToken).ConfigureAwait(false);
            await stream.ReadExactlyAsync(tile.WritableMemory, cancellationToken).ConfigureAwait(false);
            return tile;
        }
        catch
        {
            tile.Dispose();
            throw;
        }
    }
}
