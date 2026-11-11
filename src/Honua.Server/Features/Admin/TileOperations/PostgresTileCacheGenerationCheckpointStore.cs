// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.ControlPlane;
using Honua.Core.Features.Tiles;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Admin.TileOperations;

// The resume point of a generation is shared by every node, so a retry on another node resumes it.
// A generation abandoned without a delete expires after 24 hours, the tile-operation progress window.
internal sealed partial class PostgresTileCacheGenerationCheckpointStore(
    JobStoreDatabase database,
    ILogger<PostgresTileCacheGenerationCheckpointStore> logger) : ITileCacheGenerationCheckpointStore, IExpiredRowSweep
{
    private const string LiveGeneration = "generation_id = @generation_id AND expires_at > now()";
    private static readonly TimeSpan _retention = TimeSpan.FromHours(24);

    private readonly string _checkpoints = database.Table("tile_generation_checkpoints");

    public async ValueTask SaveAsync(TileCacheGenerationCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        var sanitized = TileCacheGenerationCheckpointBounds.Sanitize(checkpoint);
        var record = JsonSerializer.Serialize(sanitized, TileCacheGenerationCheckpointJsonContext.Default.TileCacheGenerationCheckpoint);
        await database.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    INSERT INTO {_checkpoints} (generation_id, record, expires_at)
                    VALUES (@generation_id, @record, now() + @retention)
                    ON CONFLICT (generation_id) DO UPDATE SET record = excluded.record, expires_at = excluded.expires_at
                    """,
                    connection);
                var parameters = command.Parameters;
                parameters.Add(GenerationParameter(sanitized.GenerationId));
                parameters.Add(new NpgsqlParameter("record", NpgsqlDbType.Json) { Value = record });
                parameters.Add(new NpgsqlParameter("retention", NpgsqlDbType.Interval) { Value = _retention });
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<TileCacheGenerationCheckpoint?> LoadAsync(string generationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);

        var record = await database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand($"SELECT record::text FROM {_checkpoints} WHERE {LiveGeneration}", connection);
                command.Parameters.Add(GenerationParameter(generationId));
                return await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(record, TileCacheGenerationCheckpointJsonContext.Default.TileCacheGenerationCheckpoint);
        }
        catch (JsonException exception)
        {
            // The row is kept and the read fails: seed and warm callers treat a failed load as no
            // resume point, while a destructive delete must not erase reservation accounting it
            // cannot read.
            Log.CorruptCheckpointReadFailed(logger, generationId, exception);
            throw new InvalidDataException(
                $"Tile-cache checkpoint '{generationId}' is corrupt and cannot be resumed safely.",
                exception);
        }
    }

    public async ValueTask<bool> DeleteAsync(string generationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);

        return await database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand($"DELETE FROM {_checkpoints} WHERE {LiveGeneration}", connection);
                command.Parameters.Add(GenerationParameter(generationId));
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) > 0;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<int> DeleteExpiredAsync(int batchSize, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    WITH expired AS (
                        SELECT generation_id FROM {_checkpoints}
                        WHERE expires_at <= now()
                        ORDER BY expires_at LIMIT @batch_size FOR UPDATE SKIP LOCKED)
                    DELETE FROM {_checkpoints} AS checkpoint USING expired WHERE checkpoint.generation_id = expired.generation_id
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("batch_size", NpgsqlDbType.Integer) { Value = batchSize });
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    private static NpgsqlParameter GenerationParameter(string generationId)
        => new("generation_id", NpgsqlDbType.Text) { Value = generationId };

    private static partial class Log
    {
        [LoggerMessage(9230, LogLevel.Warning,
            "Could not read corrupt tile-cache generation checkpoint for generation {GenerationId}.")]
        public static partial void CorruptCheckpointReadFailed(ILogger logger, string generationId, Exception exception);
    }
}
