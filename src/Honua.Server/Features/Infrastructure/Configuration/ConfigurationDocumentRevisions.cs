// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Infrastructure.Configuration;

internal sealed record ConfigurationRevision
{
    public required long RevisionId { get; init; }

    public required string Change { get; init; }

    public long? DocumentRevision { get; init; }

    public required long Generation { get; init; }

    public long? RestoredFrom { get; init; }

    public required DateTimeOffset ChangedAt { get; init; }
}

internal enum ConfigurationUndoOutcome
{
    Undone,
    NothingToUndo,
    Superseded,
    RecreateRefused
}

internal sealed record ConfigurationUndoResult(ConfigurationUndoOutcome Outcome, ConfigurationRevision? Revision = null);

internal sealed record ConfigurationDocumentKey(string Kind, string Key);

// Whether an undo may bring back a document whose newest change deleted it.
internal enum DeletedDocumentUndo
{
    Refuse,
    Recreate
}

internal sealed record ConfigurationUndoScope(ConfigurationDocumentKey Document, DeletedDocumentUndo Deleted);

// The caller names the newest revision it saw; a later change by anyone else makes the undo Superseded.
internal sealed record ConfigurationUndoRequest(ConfigurationUndoScope Scope, long ExpectedRevisionId);

// Settings history: migration 212 records every document write as a revision under the generation it
// produced. Undo restores the state before the newest change as a new revision, so generations only rise.
internal sealed partial class PostgresConfigurationDocuments
{
    internal const int RevisionsKept = 32;
    private const string RevisionColumns = "revision_id, change, document_revision, generation, restored_from, changed_at";

    private string Revisions => database.Table("configuration_document_revisions");

    public Task<IReadOnlyList<ConfigurationRevision>> ListRevisionsAsync(ConfigurationDocumentKey document, CancellationToken cancellationToken)
        => database.ExecuteAsync<IReadOnlyList<ConfigurationRevision>>(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"""
                SELECT {RevisionColumns} FROM {Revisions}
                WHERE kind = @kind AND key = @key ORDER BY revision_id DESC LIMIT {RevisionsKept}
                """, connection);
            AddDocumentParameters(command, document);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var revisions = new List<ConfigurationRevision>();
            while (await reader.ReadAsync(token).ConfigureAwait(false)) { revisions.Add(ReadRevision(reader)); }
            return revisions;
        }, StatementRepeat.SafeToRepeat, cancellationToken);

    // An undo never discards a change its author did not see. Undoing an undo re-applies the change.
    public Task<ConfigurationUndoResult> UndoAsync(ConfigurationUndoRequest request, CancellationToken cancellationToken)
        => database.ExecuteAsync<ConfigurationUndoResult>(async (connection, _, token) =>
        {
            await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            await LockGenerationAsync(connection, token).ConfigureAwait(false);
            var pair = await ReadUndoPairAsync(connection, request.Scope.Document, token).ConfigureAwait(false);
            if (Refusal(pair, request) is { } refused)
            {
                return new ConfigurationUndoResult(refused);
            }
            await using (var restore = CreateRestoreCommand(connection, request.Scope.Document, pair))
            {
                if (await restore.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                {
                    return new ConfigurationUndoResult(ConfigurationUndoOutcome.Superseded);
                }
            }
            var restored = await MarkRestoredAsync(connection, request, token).ConfigureAwait(false);
            await transaction.CommitSafelyAsync(token).ConfigureAwait(false);
            return new ConfigurationUndoResult(ConfigurationUndoOutcome.Undone, restored);
        }, StatementRepeat.NotAfterSend, cancellationToken);

    private static ConfigurationUndoOutcome? Refusal(UndoPair pair, ConfigurationUndoRequest request)
    {
        var latest = pair.Latest;
        if (latest is null || latest.Change == "baseline" || (pair.Previous is null && latest.Change != "created"))
        {
            return ConfigurationUndoOutcome.NothingToUndo;
        }
        if (latest.RevisionId != request.ExpectedRevisionId)
        {
            return ConfigurationUndoOutcome.Superseded;
        }
        return latest.Change == "deleted" && request.Scope.Deleted == DeletedDocumentUndo.Refuse
            ? ConfigurationUndoOutcome.RecreateRefused
            : null;
    }

    private async Task<UndoPair> ReadUndoPairAsync(NpgsqlConnection connection, ConfigurationDocumentKey document, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT revision_id, change, payload, document_revision FROM {Revisions}
            WHERE kind = @kind AND key = @key ORDER BY revision_id DESC LIMIT 2
            """, connection);
        AddDocumentParameters(command, document);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var rows = new List<StoredRevision>(2);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            rows.Add(new StoredRevision
            {
                RevisionId = reader.GetInt64(0),
                Change = reader.GetString(1),
                Payload = reader.IsDBNull(2) ? null : reader.GetFieldValue<byte[]>(2),
                DocumentRevision = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
            });
        }
        return new UndoPair(rows.ElementAtOrDefault(0), rows.ElementAtOrDefault(1));
    }

    // The state before the newest change is the previous revision's payload; none (or a deletion) means the
    // document did not exist, so the restore deletes it. A re-created document continues after its last revision
    // number, so a writer still holding a revision from before the deletion cannot overwrite it.
    private NpgsqlCommand CreateRestoreCommand(NpgsqlConnection connection, ConfigurationDocumentKey document, UndoPair pair)
    {
        var payload = pair.Previous?.Payload;
        var recreate = payload is not null && pair.Latest!.Change == "deleted";
        var statement = payload is null
            ? $"DELETE FROM {_documents} WHERE kind = @kind AND key = @key"
            : recreate
                ? $"INSERT INTO {_documents} (kind, key, payload, revision) VALUES (@kind, @key, @payload, @revision) ON CONFLICT DO NOTHING"
                : $"UPDATE {_documents} SET payload = @payload, revision = revision + 1 WHERE kind = @kind AND key = @key";
        var command = new NpgsqlCommand(statement, connection);
        AddDocumentParameters(command, document);
        if (recreate)
        {
            command.Parameters.AddWithValue("revision", pair.Previous!.DocumentRevision + 1);
        }
        if (payload is not null) { command.Parameters.AddWithValue("payload", NpgsqlDbType.Bytea, payload); }
        return command;
    }

    // At this point the undone revision is the expected one, so the request names it.
    private async Task<ConfigurationRevision> MarkRestoredAsync(
        NpgsqlConnection connection, ConfigurationUndoRequest request, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"""
            UPDATE {Revisions} SET restored_from = @undone
            WHERE revision_id = (SELECT max(revision_id) FROM {Revisions} WHERE kind = @kind AND key = @key)
            RETURNING {RevisionColumns}
            """, connection);
        AddDocumentParameters(command, request.Scope.Document);
        command.Parameters.AddWithValue("undone", request.ExpectedRevisionId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The restored settings revision was not recorded.");
        }
        return ReadRevision(reader);
    }

    private static void AddDocumentParameters(NpgsqlCommand command, ConfigurationDocumentKey document)
    {
        command.Parameters.AddWithValue("kind", document.Kind);
        command.Parameters.AddWithValue("key", document.Key);
    }

    private static ConfigurationRevision ReadRevision(NpgsqlDataReader reader) => new()
    {
        RevisionId = reader.GetInt64(0),
        Change = reader.GetString(1),
        DocumentRevision = reader.IsDBNull(2) ? null : reader.GetInt64(2),
        Generation = reader.GetInt64(3),
        RestoredFrom = reader.IsDBNull(4) ? null : reader.GetInt64(4),
        ChangedAt = reader.GetFieldValue<DateTimeOffset>(5),
    };

    private sealed record StoredRevision
    {
        public required long RevisionId { get; init; }

        public required string Change { get; init; }

        public byte[]? Payload { get; init; }

        public long DocumentRevision { get; init; }
    }

    private sealed record UndoPair(StoredRevision? Latest, StoredRevision? Previous);
}
