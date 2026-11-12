// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Assets;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Assets;

// Writes the record of work a completion produces and the asset summary it may advance.
internal sealed class PostgresWorkTaskRecords
{
    private readonly PostgresWorkTaskRows _rows = new();
    private readonly string _inspections = SchemaSearchPath.QualifyTable("asset_inspections", "honua");
    private readonly string _serviceRecords = SchemaSearchPath.QualifyTable("asset_service_records", "honua");
    private readonly string _summaries = SchemaSearchPath.QualifyTable("asset_summaries", "honua");

    public async Task InsertRecordAsync(NpgsqlTransaction transaction, WorkTaskCompletionRecord record, CancellationToken cancellationToken)
    {
        var sql = record.Task.Type == WorkTaskType.Inspection
            ? $"""
              INSERT INTO {_inspections} (inspection_id, task_id, profile_id, asset_id, form_id, form_version, condition,
                  answers, notes, attachments, inspector, observed_at, recorded_at)
              VALUES (@record_id, @task_id, @profile_id, @asset_id, @form_id, @form_version, @condition,
                  @answers::jsonb, @notes, '[]', @actor, @observed_at, now())
              """
            : $"""
              INSERT INTO {_serviceRecords} (service_record_id, task_id, profile_id, asset_id, location, service_type,
                  form_id, form_version, answers, notes, attachments, actor, observed_at, recorded_at)
              SELECT @record_id, task_id, profile_id, asset_id, geometry, task_type, @form_id, @form_version,
                  @answers::jsonb, @notes, '[]', @actor, @observed_at, now()
              FROM {_rows.TasksTable} WHERE task_id = @task_id
              """;
        await using var command = new NpgsqlCommand(sql, transaction.Connection, transaction);
        var parameters = command.Parameters;
        parameters.AddWithValue("record_id", NpgsqlDbType.Uuid, record.RecordId);
        parameters.AddWithValue("task_id", NpgsqlDbType.Uuid, record.Task.TaskId);
        parameters.AddWithValue("profile_id", NpgsqlDbType.Uuid, (object?)record.Task.ProfileId ?? DBNull.Value);
        parameters.AddWithValue("asset_id", NpgsqlDbType.Text, (object?)record.Task.AssetId ?? DBNull.Value);
        parameters.AddWithValue("form_id", NpgsqlDbType.Text, record.Task.Form.FormId);
        parameters.AddWithValue("form_version", NpgsqlDbType.Integer, record.Task.Form.Version);
        parameters.AddWithValue("condition", NpgsqlDbType.Smallint, (object?)(short?)record.Request.Condition ?? DBNull.Value);
        parameters.AddWithValue("answers", NpgsqlDbType.Text, record.Request.Answers?.GetRawText() ?? "{}");
        parameters.AddWithValue("notes", NpgsqlDbType.Text, (object?)record.Request.Notes ?? DBNull.Value);
        parameters.AddWithValue("actor", NpgsqlDbType.Text, record.Actor);
        parameters.AddWithValue("observed_at", NpgsqlDbType.TimestampTz, record.Request.ObservedAt!.Value.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // Each field group advances only for a newer observation in that group, so late uploads never regress the summary.
    // The upsert serializes concurrent completions on the (profile, asset) key. Closing a resident request is recorded
    // but is not maintenance, so it leaves the serviced date alone.
    public async Task AdvanceSummaryAsync(NpgsqlTransaction transaction, WorkTaskCompletionRecord record, CancellationToken cancellationToken)
    {
        if (record.Task.ProfileId is null || record.Task.Type == WorkTaskType.Request)
        {
            return;
        }

        var inspection = record.Task.Type == WorkTaskType.Inspection;
        var (value, observed) = inspection ? ("last_inspected_at", "inspection_observed_at") : ("last_serviced_at", "service_observed_at");
        var newer = $"EXCLUDED.{observed} > COALESCE(s.{observed}, '-infinity')";
        var condition = inspection
            ? $"condition = CASE WHEN {newer} AND EXCLUDED.condition IS NOT NULL THEN EXCLUDED.condition ELSE s.condition END,"
            : string.Empty;
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_summaries} AS s (profile_id, asset_id, revision, condition, {value}, {observed}, updated_at)
            VALUES (@profile_id, @asset_id, 1, @condition, @observed_at, @observed_at, now())
            ON CONFLICT (profile_id, asset_id) DO UPDATE SET
                {condition}
                {value} = GREATEST(s.{value}, EXCLUDED.{value}),
                {observed} = GREATEST(s.{observed}, EXCLUDED.{observed}),
                revision = s.revision + CASE WHEN {newer} THEN 1 ELSE 0 END,
                updated_at = now()
            """,
            transaction.Connection,
            transaction);
        command.Parameters.AddWithValue("profile_id", NpgsqlDbType.Uuid, record.Task.ProfileId.Value);
        command.Parameters.AddWithValue("asset_id", NpgsqlDbType.Text, record.Task.AssetId!);
        command.Parameters.AddWithValue("condition", NpgsqlDbType.Smallint, inspection ? (object?)(short?)record.Request.Condition ?? DBNull.Value : DBNull.Value);
        command.Parameters.AddWithValue("observed_at", NpgsqlDbType.TimestampTz, record.Request.ObservedAt!.Value.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkTask> CompleteTaskAsync(NpgsqlTransaction transaction, WorkTaskCompletionRecord record, CancellationToken cancellationToken)
    {
        var inspection = record.Task.Type == WorkTaskType.Inspection;
        var completion = new Dictionary<string, string>
        {
            [inspection ? "inspectionId" : "serviceRecordId"] = record.RecordId.ToString("D"),
            ["observedAt"] = record.Request.ObservedAt!.Value.UtcDateTime.ToString("O"),
        };
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {_rows.TasksTable} SET status = 'completed', version = version + 1, completed_at = now(),
                updated_at = now(), completion = @completion::jsonb
            WHERE task_id = @task_id AND version = @expected_version
            """,
            transaction.Connection,
            transaction);
        command.Parameters.AddWithValue("task_id", NpgsqlDbType.Uuid, record.Task.TaskId);
        command.Parameters.AddWithValue("expected_version", NpgsqlDbType.Bigint, record.Task.Version);
        command.Parameters.AddWithValue("completion", NpgsqlDbType.Text, JsonSerializer.Serialize(completion, AssetsJsonContext.Default.DictionaryStringString));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return (await _rows.LockAsync(transaction, record.Task.TaskId, cancellationToken).ConfigureAwait(false))!;
    }
}

internal sealed record WorkTaskCompletionRecord(Guid RecordId, WorkTask Task, WorkTaskCompletionRequest Request, string Actor);
