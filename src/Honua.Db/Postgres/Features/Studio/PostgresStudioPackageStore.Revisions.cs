// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Studio.Domain;
using Npgsql;

namespace Honua.Db.Postgres.Features.Studio;

internal sealed partial class PostgresStudioPackageStore
{
    public async Task<StudioDraftRevision?> GetDraftRevisionAsync(
        Guid draftId, long generation, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT generation, envelope, updated_by, updated_at
            FROM {_draftRevisionsTable}
            WHERE draft_id = @draft_id AND generation = @generation
            """;
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease.Connection);
        command.Parameters.AddWithValue("@draft_id", draftId);
        command.Parameters.AddWithValue("@generation", generation);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var envelope = JsonSerializer.Deserialize(reader.GetString(1), StudioJsonContext.Default.StudioPackageEnvelope)
            ?? throw new InvalidDataException("Studio draft revision envelope was empty.");
        return new()
        {
            Generation = reader.GetInt64(0),
            Envelope = envelope,
            UpdatedBy = reader.IsDBNull(2) ? null : reader.GetString(2),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(3),
        };
    }

    public async Task<IReadOnlyList<StudioDraftRevisionSummary>> ListDraftRevisionsAsync(
        StudioDraftRevisionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var sql = $"""
            SELECT generation, updated_by, updated_at
            FROM {_draftRevisionsTable}
            WHERE draft_id = @draft_id AND generation < @before_generation
            ORDER BY generation DESC
            LIMIT @limit
            """;
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease.Connection);
        command.Parameters.AddWithValue("@draft_id", query.DraftId);
        command.Parameters.AddWithValue("@before_generation", query.BeforeGeneration ?? long.MaxValue);
        command.Parameters.AddWithValue("@limit", Math.Clamp(query.Limit, MinListLimit, MaxListLimit));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var revisions = new List<StudioDraftRevisionSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            revisions.Add(new(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return revisions;
    }
}
