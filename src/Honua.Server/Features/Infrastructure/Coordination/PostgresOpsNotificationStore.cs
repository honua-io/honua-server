// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Core.Features.Observability.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.ControlPlane;

internal sealed record OpsNotificationSchedule(
    string Source, string SubjectId, Guid EpisodeId, DateTimeOffset FirstSeenAt,
    OpsFindingSeverity Severity, int NoticeNumber, long Revision);

internal sealed class PostgresOpsNotificationStore(JobStoreDatabase database)
{
    private readonly string _schedules = database.Table("ops_notification_schedules");
    private readonly string _proposals = database.Table("operation_proposals");

    public Task<int> ExpireApprovalsAsync(int limit, CancellationToken cancellationToken = default)
        => database.ExecuteAsync(async (connection, _, token) =>
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
            await using var command = new NpgsqlCommand($"""
                WITH candidates AS (
                    SELECT proposal_id FROM {_proposals}
                    WHERE active AND record->>'status' = '1'
                        AND now() >= COALESCE((record->>'approvalExpiresAt')::timestamptz,
                            (record->>'createdAt')::timestamptz +
                            CASE WHEN record->'autonomyMetadata'->>'severity' = '2'
                                OR (record->'autonomyMetadata'->>'severity' IS NULL AND record->'plan'->>'riskLevel' = '2')
                                THEN interval '72 hours' ELSE interval '24 hours' END)
                    ORDER BY updated_at, proposal_id LIMIT @limit FOR UPDATE SKIP LOCKED
                ), expired AS (
                    UPDATE {_proposals} AS proposal
                    SET version = version + 1, updated_at = now(), active = FALSE,
                        record = jsonb_set(record::jsonb, ARRAY['status'], '10'::jsonb)::json
                    FROM candidates WHERE proposal.proposal_id = candidates.proposal_id
                    RETURNING proposal.proposal_id, proposal.record
                )
                INSERT INTO {_schedules} AS schedule
                    (source, subject_id, episode_id, first_seen_at, severity, next_notice_at, last_seen_at)
                SELECT 'proposal', proposal_id, @episode, now(),
                    CASE WHEN record->'autonomyMetadata'->>'severity' = '2'
                        OR (record->'autonomyMetadata'->>'severity' IS NULL AND record->'plan'->>'riskLevel' = '2')
                        THEN 2 ELSE 1 END, now(), now()
                FROM expired
                ON CONFLICT (source, subject_id) DO UPDATE SET
                    next_notice_at = now(), last_seen_at = now(), closed_at = NULL,
                    notice_number = schedule.notice_number + 1, revision = schedule.revision + 1
                """, connection);
            command.Parameters.AddWithValue("limit", Math.Min(limit, 100));
            command.Parameters.AddWithValue("episode", Guid.NewGuid());
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, StatementRepeat.SafeToRepeat, cancellationToken);

    public Task<int> ObserveAwaitingAsync(int limit, CancellationToken cancellationToken = default)
        => database.ExecuteAsync(async (connection, _, token) =>
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
            await using var command = new NpgsqlCommand($"""
                INSERT INTO {_schedules} AS schedule
                    (source, subject_id, episode_id, first_seen_at, severity, next_notice_at, last_seen_at)
                SELECT 'proposal', proposal.proposal_id, @episode,
                    (proposal.record->>'createdAt')::timestamptz,
                    CASE WHEN proposal.record->'autonomyMetadata'->>'severity' = '2'
                        OR (proposal.record->'autonomyMetadata'->>'severity' IS NULL
                            AND proposal.record->'plan'->>'riskLevel' = '2') THEN 2 ELSE 1 END,
                    now(), now()
                FROM {_proposals} AS proposal
                WHERE proposal.active AND proposal.record->>'status' = '1'
                    AND NOT EXISTS (SELECT 1 FROM {_schedules} AS existing
                        WHERE existing.source = 'proposal' AND existing.subject_id = proposal.proposal_id)
                ORDER BY proposal.updated_at, proposal.proposal_id LIMIT @limit
                ON CONFLICT (source, subject_id) DO NOTHING
                """, connection);
            command.Parameters.AddWithValue("limit", Math.Min(limit, 100));
            command.Parameters.AddWithValue("episode", Guid.NewGuid());
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, StatementRepeat.SafeToRepeat, cancellationToken);

    public Task<bool> TryCloseAsync(OpsNotificationSchedule schedule, DateTimeOffset now,
        CancellationToken cancellationToken = default)
        => database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"""
                UPDATE {_schedules} SET closed_at = @now, revision = revision + 1
                WHERE source = @source AND subject_id = @subject AND episode_id = @episode
                    AND revision = @revision AND closed_at IS NULL
                """, connection);
            command.Parameters.AddWithValue("source", schedule.Source);
            command.Parameters.AddWithValue("subject", schedule.SubjectId);
            command.Parameters.AddWithValue("episode", schedule.EpisodeId);
            command.Parameters.AddWithValue("revision", schedule.Revision);
            command.Parameters.AddWithValue("now", now);
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
        }, StatementRepeat.NotAfterSend, cancellationToken);

    public Task ObserveAsync(string source, string subjectId, OpsFindingSeverity severity,
        DateTimeOffset now, CancellationToken cancellationToken = default)
        => database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"""
                INSERT INTO {_schedules} AS schedule
                    (source, subject_id, episode_id, first_seen_at, severity, next_notice_at, last_seen_at)
                VALUES (@source, @subject, @episode, @now, @severity, @now, @now)
                ON CONFLICT (source, subject_id) DO UPDATE SET
                    episode_id = CASE WHEN schedule.closed_at IS NOT NULL THEN EXCLUDED.episode_id ELSE schedule.episode_id END,
                    first_seen_at = CASE WHEN schedule.closed_at IS NOT NULL THEN @now ELSE schedule.first_seen_at END,
                    notice_number = CASE WHEN schedule.closed_at IS NOT NULL THEN 0 ELSE schedule.notice_number END,
                    next_notice_at = CASE WHEN schedule.closed_at IS NOT NULL OR @severity > schedule.severity
                        THEN @now ELSE schedule.next_notice_at END,
                    severity = CASE WHEN schedule.closed_at IS NOT NULL THEN @severity ELSE greatest(schedule.severity, @severity) END,
                    last_seen_at = @now, closed_at = NULL, revision = schedule.revision + 1
                """, connection);
            command.Parameters.AddWithValue("source", source);
            command.Parameters.AddWithValue("subject", subjectId);
            command.Parameters.AddWithValue("episode", Guid.NewGuid());
            command.Parameters.AddWithValue("severity", (int)severity);
            command.Parameters.AddWithValue("now", now);
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, StatementRepeat.SafeToRepeat, cancellationToken);

    public Task<IReadOnlyList<OpsNotificationSchedule>> ListDueAsync(DateTimeOffset now, int limit,
        CancellationToken cancellationToken = default)
        => database.ExecuteAsync<IReadOnlyList<OpsNotificationSchedule>>(async (connection, _, token) =>
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
            await using var command = new NpgsqlCommand($"""
                SELECT source, subject_id, episode_id, first_seen_at, severity, notice_number, revision
                FROM {_schedules} WHERE closed_at IS NULL AND next_notice_at <= @now
                ORDER BY next_notice_at, source, subject_id LIMIT @limit
                """, connection);
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("limit", Math.Min(limit, 100));
            var schedules = new List<OpsNotificationSchedule>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                schedules.Add(new(reader.GetString(0), reader.GetString(1), reader.GetGuid(2),
                    reader.GetFieldValue<DateTimeOffset>(3), (OpsFindingSeverity)reader.GetInt32(4),
                    reader.GetInt32(5), reader.GetInt64(6)));
            }
            return schedules;
        }, StatementRepeat.SafeToRepeat, cancellationToken);

    public Task<bool> TryDeferAsync(OpsNotificationSchedule schedule, DateTimeOffset retryAt,
        CancellationToken cancellationToken = default)
        => database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"""
                UPDATE {_schedules} SET next_notice_at = @retry, revision = revision + 1
                WHERE source = @source AND subject_id = @subject AND episode_id = @episode
                    AND revision = @revision AND closed_at IS NULL
                """, connection);
            command.Parameters.AddWithValue("source", schedule.Source);
            command.Parameters.AddWithValue("subject", schedule.SubjectId);
            command.Parameters.AddWithValue("episode", schedule.EpisodeId);
            command.Parameters.AddWithValue("revision", schedule.Revision);
            command.Parameters.AddWithValue("retry", retryAt);
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
        }, StatementRepeat.NotAfterSend, cancellationToken);

    public Task<bool> TryAdvanceAsync(OpsNotificationSchedule schedule, DateTimeOffset nextNoticeAt,
        CancellationToken cancellationToken = default)
        => database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"""
                UPDATE {_schedules} SET notice_number = notice_number + 1, next_notice_at = @next,
                    revision = revision + 1
                WHERE source = @source AND subject_id = @subject AND episode_id = @episode
                    AND revision = @revision AND closed_at IS NULL
                """, connection);
            command.Parameters.AddWithValue("source", schedule.Source);
            command.Parameters.AddWithValue("subject", schedule.SubjectId);
            command.Parameters.AddWithValue("episode", schedule.EpisodeId);
            command.Parameters.AddWithValue("revision", schedule.Revision);
            command.Parameters.AddWithValue("next", nextNoticeAt);
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
        }, StatementRepeat.NotAfterSend, cancellationToken);

    public Task CloseMissingAsync(string source, IReadOnlyList<string> activeSubjects, DateTimeOffset now,
        CancellationToken cancellationToken = default)
        => database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"""
                UPDATE {_schedules} SET closed_at = @now, revision = revision + 1
                WHERE source = @source AND closed_at IS NULL AND NOT (subject_id = ANY(@active))
                """, connection);
            command.Parameters.AddWithValue("source", source);
            command.Parameters.AddWithValue("now", now);
            command.Parameters.Add(new NpgsqlParameter("active", NpgsqlDbType.Array | NpgsqlDbType.Text)
            { Value = activeSubjects.ToArray() });
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, StatementRepeat.SafeToRepeat, cancellationToken);
}
