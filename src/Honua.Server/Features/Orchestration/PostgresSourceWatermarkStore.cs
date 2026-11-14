// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.ControlPlane;
using Honua.Core.Features.Migration.Watermark;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Orchestration;

internal sealed class PostgresSourceWatermarkStore(JobStoreDatabase database) : ISourceWatermarkStore
{
    private readonly string _watermarks = database.Table("source_watermarks");

    public Task<SourceWatermark?> GetAsync(
        string pipelineId,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(pipelineId, sourceId);
        return database.ExecuteAsync<SourceWatermark?>(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"SELECT record FROM {_watermarks} WHERE pipeline_id = @pipeline_id AND source_id = @source_id",
                    connection);
                AddIdentity(command, pipelineId, sourceId);
                var payload = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                return payload is string json ? Deserialize(json) : null;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);
    }

    public Task<SourceWatermark> AdvanceAsync(
        SourceWatermark watermark,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(watermark);
        ValidateIdentity(watermark.PipelineId, watermark.SourceId);
        return database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = CreateAdvanceCommand(connection, watermark);
                var payload = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                return Deserialize((string)payload!);
            },
            StatementRepeat.NotAfterSend,
            cancellationToken);
    }

    public Task ClearAsync(
        string pipelineId,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(pipelineId, sourceId);
        return database.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"DELETE FROM {_watermarks} WHERE pipeline_id = @pipeline_id AND source_id = @source_id",
                    connection);
                AddIdentity(command, pipelineId, sourceId);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.NotAfterSend,
            cancellationToken);
    }

    private NpgsqlCommand CreateAdvanceCommand(NpgsqlConnection connection, SourceWatermark watermark)
    {
        var command = new NpgsqlCommand(
            $$"""
            INSERT INTO {{_watermarks}} AS current_watermark (pipeline_id, source_id, record, timestamp_ticks)
            VALUES (@pipeline_id, @source_id, @record, @timestamp_ticks)
            ON CONFLICT (pipeline_id, source_id) DO UPDATE SET
                record = CASE
                    WHEN current_watermark.timestamp_ticks IS NOT NULL AND EXCLUDED.timestamp_ticks IS NOT NULL
                        AND EXCLUDED.timestamp_ticks <= current_watermark.timestamp_ticks
                    THEN jsonb_set(current_watermark.record, '{updatedAt}', EXCLUDED.record -> 'updatedAt')
                    ELSE EXCLUDED.record END,
                timestamp_ticks = CASE
                    WHEN current_watermark.timestamp_ticks IS NOT NULL AND EXCLUDED.timestamp_ticks IS NOT NULL
                        AND EXCLUDED.timestamp_ticks <= current_watermark.timestamp_ticks
                    THEN current_watermark.timestamp_ticks ELSE EXCLUDED.timestamp_ticks END
            RETURNING record
            """,
            connection);
        AddIdentity(command, watermark.PipelineId, watermark.SourceId);
        command.Parameters.AddWithValue("record", NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(watermark, SourceWatermarkJsonContext.Default.SourceWatermark));
        command.Parameters.AddWithValue("timestamp_ticks", NpgsqlDbType.Bigint,
            (object?)watermark.AsTimestamp()?.UtcTicks ?? DBNull.Value);
        return command;
    }

    private static void ValidateIdentity(string pipelineId, string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
    }

    private static void AddIdentity(NpgsqlCommand command, string pipelineId, string sourceId)
    {
        command.Parameters.AddWithValue("pipeline_id", NpgsqlDbType.Text, pipelineId);
        command.Parameters.AddWithValue("source_id", NpgsqlDbType.Text, sourceId);
    }

    private static SourceWatermark Deserialize(string payload)
        => JsonSerializer.Deserialize(payload, SourceWatermarkJsonContext.Default.SourceWatermark)
            ?? throw new InvalidDataException("The stored source watermark is invalid.");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SourceWatermark))]
internal sealed partial class SourceWatermarkJsonContext : JsonSerializerContext
{
}
