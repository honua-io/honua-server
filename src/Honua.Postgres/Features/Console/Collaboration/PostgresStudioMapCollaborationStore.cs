// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using Honua.Core.Features.Console.Collaboration.Abstractions;
using Honua.Core.Features.Console.Collaboration.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Postgres.Features.Console.Collaboration;

/// <summary>
/// Durable Postgres-backed Studio map collaboration store (#1278, slice 1):
/// feature-pinned comment threads + activity feed. Thread mutations append a
/// matching activity event in the same transaction so the feed stays a faithful
/// projection of collaboration history.
/// </summary>
internal sealed class PostgresStudioMapCollaborationStore : IStudioMapCollaborationStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly TimeProvider _timeProvider;
    private readonly string _threadsTable;
    private readonly string _messagesTable;
    private readonly string _activityTable;

    public PostgresStudioMapCollaborationStore(
        IDatabaseConnectionProvider connectionProvider,
        TimeProvider? timeProvider = null,
        string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        _connectionProvider = connectionProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _threadsTable = SchemaSearchPath.QualifyTable("studio_map_comment_threads", schemaName);
        _messagesTable = SchemaSearchPath.QualifyTable("studio_map_comment_messages", schemaName);
        _activityTable = SchemaSearchPath.QualifyTable("studio_map_activity_events", schemaName);
    }

    public async Task<IReadOnlyList<StudioMapCommentThread>> ListThreadsAsync(
        string mapId,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var sql = $"""
            SELECT thread_id, map_id, feature_label, layer_ref, anchor_x, anchor_y,
                   resolved, created_by, created_at, updated_at
            FROM {_threadsTable}
            WHERE map_id = @map_id
            ORDER BY updated_at DESC
            """;
        var threads = new List<(StudioMapCommentThread Thread, Guid Id)>();
        await using (var command = new NpgsqlCommand(sql, lease.Connection))
        {
            command.Parameters.AddWithValue("@map_id", mapId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                threads.Add((ReadThread(reader, []), reader.GetGuid(0)));
            }
        }

        var result = new List<StudioMapCommentThread>(threads.Count);
        foreach (var (thread, id) in threads)
        {
            var messages = await LoadMessagesAsync(lease.Connection, transaction: null, id, cancellationToken).ConfigureAwait(false);
            result.Add(thread with { Messages = messages });
        }

        return result;
    }

    public async Task<StudioMapCommentThread?> GetThreadAsync(
        string mapId,
        Guid threadId,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadThreadAsync(lease.Connection, transaction: null, mapId, threadId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StudioMapCommentThread> CreateThreadAsync(
        string mapId,
        string featureLabel,
        string layerRef,
        double anchorX,
        double anchorY,
        string authorName,
        string? authorId,
        string body,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var threadId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var committed = false;
        try
        {
            var insertThread = $"""
                INSERT INTO {_threadsTable}
                    (thread_id, map_id, feature_label, layer_ref, anchor_x, anchor_y, resolved, created_by, created_at, updated_at)
                VALUES
                    (@thread_id, @map_id, @feature_label, @layer_ref, @anchor_x, @anchor_y, FALSE, @created_by, @created_at, @updated_at)
                """;
            await using (var command = new NpgsqlCommand(insertThread, connection, transaction))
            {
                command.Parameters.AddWithValue("@thread_id", threadId);
                command.Parameters.AddWithValue("@map_id", mapId);
                command.Parameters.AddWithValue("@feature_label", featureLabel);
                command.Parameters.AddWithValue("@layer_ref", layerRef);
                command.Parameters.AddWithValue("@anchor_x", anchorX);
                command.Parameters.AddWithValue("@anchor_y", anchorY);
                command.Parameters.AddWithValue("@created_by", (object?)authorId ?? DBNull.Value);
                command.Parameters.AddWithValue("@created_at", now);
                command.Parameters.AddWithValue("@updated_at", now);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertMessageAsync(connection, transaction, messageId, threadId, authorName, authorId, body, now, cancellationToken).ConfigureAwait(false);
            await InsertActivityAsync(connection, transaction, mapId, "thread-opened", authorName, authorId,
                $"opened a comment on {featureLabel}", threadId, now, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
        }
        catch
        {
            if (!committed)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            throw;
        }

        return (await GetThreadAsync(mapId, threadId, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<StudioMapCommentThread?> AddReplyAsync(
        string mapId,
        Guid threadId,
        string authorName,
        string? authorId,
        string body,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var committed = false;
        try
        {
            var featureLabel = await TouchThreadAsync(connection, transaction, mapId, threadId, now, cancellationToken).ConfigureAwait(false);
            if (featureLabel is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                committed = true;
                return null;
            }

            await InsertMessageAsync(connection, transaction, Guid.NewGuid(), threadId, authorName, authorId, body, now, cancellationToken).ConfigureAwait(false);
            await InsertActivityAsync(connection, transaction, mapId, "comment-posted", authorName, authorId,
                $"replied on {featureLabel}", threadId, now, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
        }
        catch
        {
            if (!committed)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            throw;
        }

        return await GetThreadAsync(mapId, threadId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StudioMapCommentThread?> SetResolvedAsync(
        string mapId,
        Guid threadId,
        bool resolved,
        string actorName,
        string? actorId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var committed = false;
        try
        {
            string? featureLabel;
            var updateSql = $"""
                UPDATE {_threadsTable}
                SET resolved = @resolved, updated_at = @updated_at
                WHERE thread_id = @thread_id AND map_id = @map_id
                RETURNING feature_label
                """;
            await using (var command = new NpgsqlCommand(updateSql, connection, transaction))
            {
                command.Parameters.AddWithValue("@resolved", resolved);
                command.Parameters.AddWithValue("@updated_at", now);
                command.Parameters.AddWithValue("@thread_id", threadId);
                command.Parameters.AddWithValue("@map_id", mapId);
                var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                featureLabel = scalar as string;
            }

            if (featureLabel is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                committed = true;
                return null;
            }

            var kind = resolved ? "thread-resolved" : "thread-reopened";
            var verb = resolved ? "resolved" : "reopened";
            await InsertActivityAsync(connection, transaction, mapId, kind, actorName, actorId,
                $"{verb} the comment on {featureLabel}", threadId, now, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
        }
        catch
        {
            if (!committed)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            throw;
        }

        return await GetThreadAsync(mapId, threadId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StudioMapActivityEvent>> ListActivityAsync(
        string mapId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var sql = $"""
            SELECT event_id, map_id, kind, actor_name, actor_id, action, thread_id, occurred_at
            FROM {_activityTable}
            WHERE map_id = @map_id
            ORDER BY occurred_at DESC
            LIMIT @limit
            """;
        await using var command = new NpgsqlCommand(sql, lease.Connection);
        command.Parameters.AddWithValue("@map_id", mapId);
        command.Parameters.AddWithValue("@limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var events = new List<StudioMapActivityEvent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new StudioMapActivityEvent
            {
                EventId = reader.GetGuid(0),
                MapId = reader.GetString(1),
                Kind = FromDbKind(reader.GetString(2)),
                ActorName = reader.GetString(3),
                ActorId = reader.IsDBNull(4) ? null : reader.GetString(4),
                Action = reader.GetString(5),
                ThreadId = reader.IsDBNull(6) ? null : reader.GetGuid(6),
                OccurredAt = reader.GetFieldValue<DateTimeOffset>(7),
            });
        }

        return events;
    }

    private async Task<StudioMapCommentThread?> LoadThreadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string mapId,
        Guid threadId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT thread_id, map_id, feature_label, layer_ref, anchor_x, anchor_y,
                   resolved, created_by, created_at, updated_at
            FROM {_threadsTable}
            WHERE thread_id = @thread_id AND map_id = @map_id
            """;
        StudioMapCommentThread? header = null;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("@thread_id", threadId);
            command.Parameters.AddWithValue("@map_id", mapId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                header = ReadThread(reader, []);
            }
        }

        if (header is null)
        {
            return null;
        }

        var messages = await LoadMessagesAsync(connection, transaction, threadId, cancellationToken).ConfigureAwait(false);
        return header with { Messages = messages };
    }

    private async Task<IReadOnlyList<StudioMapCommentMessage>> LoadMessagesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid threadId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT message_id, author_name, author_id, body, created_at
            FROM {_messagesTable}
            WHERE thread_id = @thread_id
            ORDER BY created_at
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@thread_id", threadId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var messages = new List<StudioMapCommentMessage>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(new StudioMapCommentMessage
            {
                MessageId = reader.GetGuid(0),
                AuthorName = reader.GetString(1),
                AuthorId = reader.IsDBNull(2) ? null : reader.GetString(2),
                Body = reader.GetString(3),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(4),
            });
        }

        return messages;
    }

    private async Task<string?> TouchThreadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string mapId,
        Guid threadId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE {_threadsTable}
            SET updated_at = @updated_at
            WHERE thread_id = @thread_id AND map_id = @map_id
            RETURNING feature_label
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@updated_at", now);
        command.Parameters.AddWithValue("@thread_id", threadId);
        command.Parameters.AddWithValue("@map_id", mapId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private async Task InsertMessageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid messageId,
        Guid threadId,
        string authorName,
        string? authorId,
        string body,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_messagesTable}
                (message_id, thread_id, author_name, author_id, body, created_at)
            VALUES
                (@message_id, @thread_id, @author_name, @author_id, @body, @created_at)
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@message_id", messageId);
        command.Parameters.AddWithValue("@thread_id", threadId);
        command.Parameters.AddWithValue("@author_name", authorName);
        command.Parameters.AddWithValue("@author_id", (object?)authorId ?? DBNull.Value);
        command.Parameters.AddWithValue("@body", body);
        command.Parameters.AddWithValue("@created_at", createdAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertActivityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string mapId,
        string kind,
        string actorName,
        string? actorId,
        string action,
        Guid threadId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_activityTable}
                (event_id, map_id, kind, actor_name, actor_id, action, thread_id, occurred_at)
            VALUES
                (@event_id, @map_id, @kind, @actor_name, @actor_id, @action, @thread_id, @occurred_at)
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@event_id", Guid.NewGuid());
        command.Parameters.AddWithValue("@map_id", mapId);
        command.Parameters.AddWithValue("@kind", kind);
        command.Parameters.AddWithValue("@actor_name", actorName);
        command.Parameters.AddWithValue("@actor_id", (object?)actorId ?? DBNull.Value);
        command.Parameters.AddWithValue("@action", action);
        command.Parameters.AddWithValue("@thread_id", threadId);
        command.Parameters.AddWithValue("@occurred_at", occurredAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static StudioMapCommentThread ReadThread(NpgsqlDataReader reader, IReadOnlyList<StudioMapCommentMessage> messages)
        => new()
        {
            ThreadId = reader.GetGuid(0),
            MapId = reader.GetString(1),
            FeatureLabel = reader.GetString(2),
            LayerRef = reader.GetString(3),
            AnchorX = reader.GetDouble(4),
            AnchorY = reader.GetDouble(5),
            Resolved = reader.GetBoolean(6),
            CreatedBy = reader.IsDBNull(7) ? null : reader.GetString(7),
            Messages = messages,
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(8),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(9),
        };

    private static StudioMapActivityKind FromDbKind(string kind) => kind switch
    {
        "thread-opened" => StudioMapActivityKind.ThreadOpened,
        "comment-posted" => StudioMapActivityKind.CommentPosted,
        "thread-resolved" => StudioMapActivityKind.ThreadResolved,
        "thread-reopened" => StudioMapActivityKind.ThreadReopened,
        _ => throw new InvalidDataException($"Unsupported Studio map activity kind '{kind}' in storage."),
    };
}
