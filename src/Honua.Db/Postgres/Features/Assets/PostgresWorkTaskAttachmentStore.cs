// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Assets;

internal sealed class PostgresWorkTaskAttachmentStore(IAdoNetDatabaseConnectionProvider connections, string? schemaName = null) : IWorkTaskAttachmentStore
{
    private readonly string _generationTable = SchemaSearchPath.QualifyTable("configuration_generation", schemaName);
    private const string Columns = "attachment_id, task_id, owner_subject, field_id, filename, content_type, size_bytes, blob_key, expires_at, status";

    public async Task<bool> ReserveAsync(WorkTaskAttachment attachment, WorkTaskAttachmentLimits limits, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await ConfigurationMatchesAsync(transaction, limits.ConfigurationGeneration, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        await using var task = new NpgsqlCommand("SELECT status FROM honua.work_tasks WHERE task_id = @task AND (assignee = @owner OR @planner) FOR UPDATE", connection, transaction);
        task.Parameters.AddWithValue("task", attachment.TaskId);
        task.Parameters.AddWithValue("owner", attachment.OwnerSubject);
        task.Parameters.AddWithValue("planner", limits.IsPlanner);
        if (await task.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string status || status != "in_progress")
        {
            return false;
        }

        await using var command = new NpgsqlCommand("""
            INSERT INTO honua.work_task_attachments
                (attachment_id, task_id, owner_subject, field_id, filename, content_type, size_bytes, blob_key, expires_at, status)
            SELECT @id, @task, @owner, @field, @filename, @type, @size, @key, @expires, 'pending'
            WHERE (SELECT count(*) < @max_count AND coalesce(sum(size_bytes), 0) + @size <= @max_total
                       AND count(*) FILTER (WHERE lower(field_id) = lower(@field)) < @max_field_count
                   FROM honua.work_task_attachments
                   WHERE task_id = @task AND (status = 'claimed' OR
                       (status IN ('pending', 'ready') AND expires_at > clock_timestamp())))
            """, connection, transaction);
        AddReservationParameters(command, attachment, limits);
        var reserved = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return reserved;
    }

    private async Task<bool> ConfigurationMatchesAsync(NpgsqlTransaction transaction, long expected, CancellationToken cancellationToken)
    {
        // Hold the configuration fence before task locks, through the reservation commit.
        await using var fence = new NpgsqlCommand(
            $"SELECT generation FROM {_generationTable} WHERE singleton FOR SHARE", transaction.Connection, transaction);
        return await fence.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long generation && generation == expected;
    }

    private static void AddReservationParameters(NpgsqlCommand command, WorkTaskAttachment attachment, WorkTaskAttachmentLimits limits)
    {
        command.Parameters.AddWithValue("id", attachment.AttachmentId);
        command.Parameters.AddWithValue("task", attachment.TaskId);
        command.Parameters.AddWithValue("owner", attachment.OwnerSubject);
        command.Parameters.AddWithValue("field", attachment.FieldId);
        command.Parameters.AddWithValue("filename", attachment.Filename);
        command.Parameters.AddWithValue("type", attachment.ContentType);
        command.Parameters.AddWithValue("size", attachment.SizeBytes);
        command.Parameters.AddWithValue("key", attachment.BlobKey);
        command.Parameters.AddWithValue("expires", attachment.ExpiresAt.UtcDateTime);
        command.Parameters.AddWithValue("max_count", limits.MaxCount);
        command.Parameters.AddWithValue("max_field_count", limits.MaxFieldCount);
        command.Parameters.AddWithValue("max_total", limits.MaxTotalBytes);
    }

    public async Task<bool> MarkReadyAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            UPDATE honua.work_task_attachments SET status = 'ready'
            WHERE attachment_id = @id AND status = 'pending' AND expires_at > clock_timestamp()
            """, connection);
        command.Parameters.AddWithValue("id", attachmentId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<WorkTaskAttachment?> GetAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"SELECT {Columns} FROM honua.work_task_attachments WHERE attachment_id = @id", connection);
        command.Parameters.AddWithValue("id", attachmentId);
        var rows = await ReadAsync(command, cancellationToken).ConfigureAwait(false);
        return rows.FirstOrDefault();
    }

    public async Task<WorkTaskAttachment[]> FindReadyAsync(
        (Guid TaskId, string Owner, Guid[] Ids) lookup, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT {Columns} FROM honua.work_task_attachments
            WHERE task_id = @task AND owner_subject = @owner AND attachment_id = ANY(@ids)
                AND status = 'ready' AND expires_at > clock_timestamp()
            """, connection);
        command.Parameters.AddWithValue("task", lookup.TaskId);
        command.Parameters.AddWithValue("owner", lookup.Owner);
        command.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, lookup.Ids);
        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkTaskAttachment[]> ScheduleCleanupAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            WITH due AS (
                SELECT attachment_id FROM honua.work_task_attachments
                WHERE status <> 'claimed' AND expires_at <= clock_timestamp() AND cleanup_after <= clock_timestamp()
                ORDER BY cleanup_after LIMIT 100 FOR UPDATE SKIP LOCKED
            )
            UPDATE honua.work_task_attachments SET status = 'expired', cleanup_after = clock_timestamp() + interval '15 minutes'
            WHERE attachment_id IN (SELECT attachment_id FROM due)
            RETURNING {Columns}
            """, connection);
        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WorkTaskAttachment[]> ReadAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var results = new List<WorkTaskAttachment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new WorkTaskAttachment
            {
                AttachmentId = reader.GetGuid(0), TaskId = reader.GetGuid(1), OwnerSubject = reader.GetString(2),
                FieldId = reader.GetString(3), Filename = reader.GetString(4), ContentType = reader.GetString(5),
                SizeBytes = reader.GetInt64(6), BlobKey = reader.GetString(7), ExpiresAt = reader.GetDateTime(8),
                Status = reader.GetString(9),
            });
        }
        return results.ToArray();
    }
}
