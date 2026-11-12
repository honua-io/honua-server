// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using System.Text.Json;
using Honua.Core.Features.Assets;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Assets;

// SQL for the work_tasks and work_task_history tables; transaction control stays with the caller.
internal sealed class PostgresWorkTaskRows
{
    private const string Columns = """
        task_id, object_id, service_id, task_type, status, version, profile_id, asset_id,
        ST_AsGeoJSON(ST_Transform(geometry, 4326))::text, geometry_source, title, assignee, priority, due_at,
        form_id, form_version, created_by, created_at, updated_at, started_at, completed_at, cancelled_at, cancel_reason
        """;

    private readonly string _tasks = SchemaSearchPath.QualifyTable("work_tasks", "honua");
    private readonly string _history = SchemaSearchPath.QualifyTable("work_task_history", "honua");

    public string TasksTable => _tasks;

    public NpgsqlCommand SelectCommand(NpgsqlConnection connection, NpgsqlTransaction? transaction, string where)
        => new($"SELECT {Columns} FROM {_tasks} WHERE {where}", connection, transaction);

    public Task<WorkTask?> GetAsync(NpgsqlConnection connection, Guid taskId, CancellationToken cancellationToken)
        => SelectOneAsync(SelectCommand(connection, null, "task_id = @task_id"), taskId, cancellationToken);

    // Takes the row lock every task command serializes on.
    public Task<WorkTask?> LockAsync(NpgsqlTransaction transaction, Guid taskId, CancellationToken cancellationToken)
        => SelectOneAsync(SelectCommand(transaction.Connection!, transaction, "task_id = @task_id FOR NO KEY UPDATE"), taskId, cancellationToken);

    private static async Task<WorkTask?> SelectOneAsync(NpgsqlCommand command, Guid taskId, CancellationToken cancellationToken)
    {
        await using (command)
        {
            command.Parameters.AddWithValue("task_id", NpgsqlDbType.Uuid, taskId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
        }
    }

    // Returns false when the client-chosen task ID already exists.
    public async Task<bool> InsertAsync(NpgsqlTransaction transaction, WorkTaskDraft draft, CancellationToken cancellationToken)
    {
        var task = draft.Task;
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_tasks} (task_id, service_id, task_type, status, version, profile_id, asset_id, geometry,
                geometry_source, title, assignee, priority, due_at, form_id, form_version, created_by, created_at, updated_at)
            VALUES (@task_id, @service_id, @task_type, @status, 1, @profile_id, @asset_id,
                ST_SetSRID(ST_GeomFromWKB(@geometry), @srid), @geometry_source, @title, @assignee, @priority, @due_at,
                @form_id, @form_version, @created_by, now(), now())
            ON CONFLICT (task_id) DO NOTHING
            """,
            transaction.Connection,
            transaction);
        var parameters = command.Parameters;
        parameters.AddWithValue("task_id", NpgsqlDbType.Uuid, task.TaskId);
        parameters.AddWithValue("service_id", NpgsqlDbType.Text, task.ServiceId);
        parameters.AddWithValue("task_type", NpgsqlDbType.Text, task.Type);
        parameters.AddWithValue("status", NpgsqlDbType.Text, task.Status);
        parameters.AddWithValue("profile_id", NpgsqlDbType.Uuid, (object?)task.ProfileId ?? DBNull.Value);
        parameters.AddWithValue("asset_id", NpgsqlDbType.Text, (object?)task.AssetId ?? DBNull.Value);
        parameters.AddWithValue("geometry", NpgsqlDbType.Bytea, draft.GeometryWkb);
        parameters.AddWithValue("srid", NpgsqlDbType.Integer, draft.GeometrySrid);
        parameters.AddWithValue("geometry_source", NpgsqlDbType.Text, task.GeometrySource);
        parameters.AddWithValue("title", NpgsqlDbType.Text, task.Title);
        parameters.AddWithValue("assignee", NpgsqlDbType.Text, (object?)task.Assignee ?? DBNull.Value);
        parameters.AddWithValue("priority", NpgsqlDbType.Text, (object?)task.Priority ?? DBNull.Value);
        parameters.AddWithValue("due_at", NpgsqlDbType.TimestampTz, (object?)task.DueAt?.UtcDateTime ?? DBNull.Value);
        parameters.AddWithValue("form_id", NpgsqlDbType.Text, task.Form.FormId);
        parameters.AddWithValue("form_version", NpgsqlDbType.Integer, task.Form.Version);
        parameters.AddWithValue("created_by", NpgsqlDbType.Text, task.CreatedBy);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<WorkTask> ApplyTransitionAsync(NpgsqlTransaction transaction, WorkTaskTransitionRow row, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {_tasks} SET status = @status, assignee = @assignee, version = version + 1, updated_at = now(),
                started_at = CASE WHEN @status = 'in_progress' THEN COALESCE(started_at, now()) ELSE started_at END,
                cancelled_at = CASE WHEN @status = 'cancelled' THEN now() ELSE cancelled_at END,
                cancel_reason = CASE WHEN @status = 'cancelled' THEN @reason ELSE cancel_reason END
            WHERE task_id = @task_id AND version = @expected_version
            """,
            transaction.Connection,
            transaction);
        command.Parameters.AddWithValue("task_id", NpgsqlDbType.Uuid, row.TaskId);
        command.Parameters.AddWithValue("expected_version", NpgsqlDbType.Bigint, row.ExpectedVersion);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Text, row.Status);
        command.Parameters.AddWithValue("assignee", NpgsqlDbType.Text, (object?)row.Assignee ?? DBNull.Value);
        command.Parameters.AddWithValue("reason", NpgsqlDbType.Text, (object?)row.Reason ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return (await LockAsync(transaction, row.TaskId, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task AppendHistoryAsync(NpgsqlTransaction transaction, Guid taskId, WorkTaskHistoryEvent historyEvent, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_history} (event_id, task_id, command_id, action, old_version, new_version, old_status,
                new_status, actor, recorded_at, observed_at, changed_fields, reason)
            VALUES (@event_id, @task_id, @command_id, @action, @old_version, @new_version, @old_status, @new_status,
                @actor, now(), @observed_at, @changed_fields::jsonb, @reason)
            """,
            transaction.Connection,
            transaction);
        var parameters = command.Parameters;
        parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, historyEvent.EventId);
        parameters.AddWithValue("task_id", NpgsqlDbType.Uuid, taskId);
        parameters.AddWithValue("command_id", NpgsqlDbType.Uuid, historyEvent.CommandId);
        parameters.AddWithValue("action", NpgsqlDbType.Text, historyEvent.Action);
        parameters.AddWithValue("old_version", NpgsqlDbType.Bigint, (object?)historyEvent.OldVersion ?? DBNull.Value);
        parameters.AddWithValue("new_version", NpgsqlDbType.Bigint, historyEvent.NewVersion);
        parameters.AddWithValue("old_status", NpgsqlDbType.Text, (object?)historyEvent.OldStatus ?? DBNull.Value);
        parameters.AddWithValue("new_status", NpgsqlDbType.Text, historyEvent.NewStatus);
        parameters.AddWithValue("actor", NpgsqlDbType.Text, historyEvent.Actor);
        parameters.AddWithValue("observed_at", NpgsqlDbType.TimestampTz, (object?)historyEvent.ObservedAt?.UtcDateTime ?? DBNull.Value);
        parameters.AddWithValue("changed_fields", NpgsqlDbType.Text, JsonSerializer.Serialize(historyEvent.ChangedFields, AssetsJsonContext.Default.IReadOnlyListString));
        parameters.AddWithValue("reason", NpgsqlDbType.Text, (object?)historyEvent.Reason ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkTaskHistoryResult> ListHistoryAsync(NpgsqlConnection connection, WorkTaskHistoryQuery query, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT event_id, command_id, action, old_version, new_version, old_status, new_status, actor, recorded_at,
                observed_at, changed_fields::text, reason
            FROM {_history} WHERE task_id = @task_id AND new_version > @after
            ORDER BY new_version LIMIT @limit
            """,
            connection);
        command.Parameters.AddWithValue("task_id", NpgsqlDbType.Uuid, query.TaskId);
        command.Parameters.AddWithValue("after", NpgsqlDbType.Bigint, query.AfterVersion);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, query.Limit + 1);
        var items = new List<WorkTaskHistoryEvent>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(ReadHistory(reader));
            }
        }

        var hasMore = PostgresAssetReading.TrimToPage(items, query.Limit);

        return new WorkTaskHistoryResult { Items = items, NextCursor = hasMore ? items[^1].NewVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) : null };
    }

    public static WorkTask Read(DbDataReader reader) => new()
    {
        TaskId = reader.GetGuid(0),
        ObjectId = reader.GetInt64(1),
        ServiceId = reader.GetString(2),
        Type = reader.GetString(3),
        Status = reader.GetString(4),
        Version = reader.GetInt64(5),
        ProfileId = reader.IsDBNull(6) ? null : reader.GetGuid(6),
        AssetId = PostgresAssetReading.OptionalString(reader, 7),
        Geometry = JsonElement.Parse(reader.GetString(8)),
        GeometrySource = reader.GetString(9),
        Title = reader.GetString(10),
        Assignee = PostgresAssetReading.OptionalString(reader, 11),
        Priority = PostgresAssetReading.OptionalString(reader, 12),
        DueAt = PostgresAssetReading.OptionalTime(reader, 13),
        Form = new WorkTaskFormReference { FormId = reader.GetString(14), Version = reader.GetInt32(15) },
        CreatedBy = reader.GetString(16),
        CreatedAt = PostgresAssetReading.RequiredTime(reader, 17),
        UpdatedAt = PostgresAssetReading.RequiredTime(reader, 18),
        StartedAt = PostgresAssetReading.OptionalTime(reader, 19),
        CompletedAt = PostgresAssetReading.OptionalTime(reader, 20),
        CancelledAt = PostgresAssetReading.OptionalTime(reader, 21),
        CancelReason = PostgresAssetReading.OptionalString(reader, 22),
    };

    private static WorkTaskHistoryEvent ReadHistory(DbDataReader reader) => new()
    {
        EventId = reader.GetGuid(0),
        CommandId = reader.GetGuid(1),
        Action = reader.GetString(2),
        OldVersion = reader.IsDBNull(3) ? null : reader.GetInt64(3),
        NewVersion = reader.GetInt64(4),
        OldStatus = PostgresAssetReading.OptionalString(reader, 5),
        NewStatus = reader.GetString(6),
        Actor = reader.GetString(7),
        RecordedAt = PostgresAssetReading.RequiredTime(reader, 8),
        ObservedAt = PostgresAssetReading.OptionalTime(reader, 9),
        ChangedFields = JsonSerializer.Deserialize(reader.GetString(10), AssetsJsonContext.Default.IReadOnlyListString) ?? [],
        Reason = PostgresAssetReading.OptionalString(reader, 11),
    };
}

internal sealed record WorkTaskTransitionRow(Guid TaskId, long ExpectedVersion, string Status, string? Assignee, string? Reason);
