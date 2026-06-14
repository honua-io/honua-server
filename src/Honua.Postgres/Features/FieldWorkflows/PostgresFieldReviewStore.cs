// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.FieldWorkflows.Review;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.FieldWorkflows;

/// <summary>
/// PostgreSQL-backed store for back-office field review and QA. Read access
/// projects <c>honua.form_submissions</c>; write access manages the server-owned
/// <c>honua.field_submission_reviews</c> and <c>honua.field_review_comments</c>
/// tables. A submission with no review row is reported with a default
/// <c>pending</c> review state.
/// </summary>
internal sealed class PostgresFieldReviewStore : IFieldReviewStore
{
    private const int MaxLimit = 500;

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _submissionsTable;
    private readonly string _attachmentsTable;
    private readonly string _reviewsTable;
    private readonly string _commentsTable;

    public PostgresFieldReviewStore(IDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _submissionsTable = SchemaSearchPath.QualifyTable("form_submissions", "honua");
        _attachmentsTable = SchemaSearchPath.QualifyTable("form_submission_attachments", "honua");
        _reviewsTable = SchemaSearchPath.QualifyTable("field_submission_reviews", "honua");
        _commentsTable = SchemaSearchPath.QualifyTable("field_review_comments", "honua");
    }

    public async Task<FieldSubmissionListResult> ListSubmissionsAsync(
        FieldReviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var limit = Math.Clamp(query.Limit <= 0 ? 50 : query.Limit, 1, MaxLimit);
        var offset = Math.Max(0, query.Offset);

        var where = BuildWhereClause(query);

        var countSql = $"""
            SELECT COUNT(*)
            FROM {_submissionsTable} s
            LEFT JOIN {_reviewsTable} r ON r.submission_id = s.submission_id
            {where}
            """;

        var listSql = $"""
            SELECT
                s.submission_id, s.form_id, s.form_version, s.service_id, s.layer_id,
                s.target_feature_id, s.operation, s.status, s.actor_hash,
                s.request_summary::text, s.validation_json::text, s.created_at,
                COALESCE(r.status, 'pending') AS review_status,
                r.assigned_to, r.decided_by, r.decided_at, r.decision_note,
                COALESCE(r.updated_at, s.created_at) AS review_updated_at,
                r.etag AS review_etag, COALESCE(r.has_conflict, false) AS has_conflict,
                (SELECT COUNT(*) FROM {_attachmentsTable} a
                    WHERE a.submission_id = s.submission_id AND a.status = 'accepted') AS attachment_count
            FROM {_submissionsTable} s
            LEFT JOIN {_reviewsTable} r ON r.submission_id = s.submission_id
            {where}
            ORDER BY s.created_at DESC, s.submission_id
            LIMIT @limit OFFSET @offset
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);

        long total;
        await using (var countCommand = new NpgsqlCommand(countSql, connection))
        {
            AddFilterParameters(countCommand, query);
            total = (long)(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        }

        var items = new List<FieldSubmissionRecord>();
        await using (var listCommand = new NpgsqlCommand(listSql, connection))
        {
            AddFilterParameters(listCommand, query);
            listCommand.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit);
            listCommand.Parameters.AddWithValue("offset", NpgsqlDbType.Integer, offset);

            await using var reader = await listCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(ReadRecord(reader));
            }
        }

        return new FieldSubmissionListResult
        {
            Items = items.ToArray(),
            Total = total,
            Limit = limit,
            Offset = offset
        };
    }

    public async Task<FieldSubmissionDetail?> GetSubmissionAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT
                s.submission_id, s.form_id, s.form_version, s.service_id, s.layer_id,
                s.target_feature_id, s.operation, s.status, s.actor_hash,
                s.request_summary::text, s.validation_json::text, s.created_at,
                COALESCE(r.status, 'pending') AS review_status,
                r.assigned_to, r.decided_by, r.decided_at, r.decision_note,
                COALESCE(r.updated_at, s.created_at) AS review_updated_at,
                r.etag AS review_etag, COALESCE(r.has_conflict, false) AS has_conflict,
                (SELECT COUNT(*) FROM {_attachmentsTable} a
                    WHERE a.submission_id = s.submission_id AND a.status = 'accepted') AS attachment_count
            FROM {_submissionsTable} s
            LEFT JOIN {_reviewsTable} r ON r.submission_id = s.submission_id
            WHERE s.submission_id = @submission_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);

        FieldSubmissionRecord record;
        await using (var command = new NpgsqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Uuid, submissionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            record = ReadRecord(reader);
        }

        var comments = await ReadCommentsAsync(connection, submissionId, cancellationToken).ConfigureAwait(false);
        var attachments = await ReadAttachmentsAsync(connection, submissionId, cancellationToken).ConfigureAwait(false);

        return new FieldSubmissionDetail
        {
            Record = record,
            Comments = comments,
            Attachments = attachments
        };
    }

    public async Task<FieldReviewState?> AssignAsync(
        Guid submissionId,
        string? assignedTo,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        if (!await SubmissionExistsAsync(connection, transaction, submissionId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var normalizedAssignee = string.IsNullOrWhiteSpace(assignedTo) ? null : assignedTo.Trim();
        var etag = NewETag();

        // Assigning a pending record moves it to in_review; unassigning leaves the
        // existing status untouched (the COALESCE preserves the prior status).
        var sql = $"""
            INSERT INTO {_reviewsTable} (submission_id, status, assigned_to, etag, created_at, updated_at)
            VALUES (@submission_id, @initial_status, @assigned_to, @etag, now(), now())
            ON CONFLICT (submission_id) DO UPDATE SET
                assigned_to = EXCLUDED.assigned_to,
                status = CASE
                    WHEN {_reviewsTable}.status = 'pending' AND EXCLUDED.assigned_to IS NOT NULL
                        THEN 'in_review'
                    ELSE {_reviewsTable}.status
                END,
                etag = EXCLUDED.etag,
                updated_at = now()
            RETURNING status, assigned_to, decided_by, decided_at, decision_note, updated_at, etag
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Uuid, submissionId);
        command.Parameters.AddWithValue(
            "initial_status",
            NpgsqlDbType.Text,
            normalizedAssignee is null ? FieldReviewStatus.Pending : FieldReviewStatus.InReview);
        command.Parameters.AddWithValue("assigned_to", NpgsqlDbType.Text, (object?)normalizedAssignee ?? DBNull.Value);
        command.Parameters.AddWithValue("etag", NpgsqlDbType.Text, etag);

        FieldReviewState state;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            state = ReadReviewState(reader);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return state;
    }

    public async Task<FieldReviewState?> RecordDecisionAsync(
        Guid submissionId,
        string status,
        string? note,
        string? expectedETag,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        if (!await SubmissionExistsAsync(connection, transaction, submissionId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // When an ETag is supplied and a review row already exists, enforce
        // optimistic concurrency before applying the decision.
        if (!string.IsNullOrWhiteSpace(expectedETag))
        {
            var currentETag = await ReadCurrentETagAsync(connection, transaction, submissionId, cancellationToken).ConfigureAwait(false);
            if (currentETag is not null && !string.Equals(currentETag, expectedETag, StringComparison.Ordinal))
            {
                throw new FieldReviewConcurrencyException();
            }
        }

        var etag = NewETag();
        var sql = $"""
            INSERT INTO {_reviewsTable}
                (submission_id, status, decided_by, decided_at, decision_note, etag, created_at, updated_at)
            VALUES (@submission_id, @status, @actor, now(), @note, @etag, now(), now())
            ON CONFLICT (submission_id) DO UPDATE SET
                status = EXCLUDED.status,
                decided_by = EXCLUDED.decided_by,
                decided_at = EXCLUDED.decided_at,
                decision_note = EXCLUDED.decision_note,
                etag = EXCLUDED.etag,
                updated_at = now()
            RETURNING status, assigned_to, decided_by, decided_at, decision_note, updated_at, etag
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Uuid, submissionId);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Text, status);
        command.Parameters.AddWithValue("actor", NpgsqlDbType.Text, actor);
        command.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)note ?? DBNull.Value);
        command.Parameters.AddWithValue("etag", NpgsqlDbType.Text, etag);

        FieldReviewState state;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            state = ReadReviewState(reader);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return state;
    }

    public async Task<FieldReviewComment?> AddCommentAsync(
        Guid submissionId,
        string body,
        bool correctionRequest,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        if (!await SubmissionExistsAsync(connection, transaction, submissionId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var commentId = Guid.NewGuid();
        var insertSql = $"""
            INSERT INTO {_commentsTable}
                (comment_id, submission_id, author, body, correction_request, created_at)
            VALUES (@comment_id, @submission_id, @author, @body, @correction_request, now())
            RETURNING comment_id, submission_id, author, body, correction_request, created_at
            """;

        FieldReviewComment comment;
        await using (var command = new NpgsqlCommand(insertSql, connection, transaction))
        {
            command.Parameters.AddWithValue("comment_id", NpgsqlDbType.Uuid, commentId);
            command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Uuid, submissionId);
            command.Parameters.AddWithValue("author", NpgsqlDbType.Text, actor);
            command.Parameters.AddWithValue("body", NpgsqlDbType.Text, body);
            command.Parameters.AddWithValue("correction_request", NpgsqlDbType.Boolean, correctionRequest);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            comment = ReadComment(reader);
        }

        // A correction request transitions the record to changes_requested so it
        // surfaces in the corresponding review queue.
        if (correctionRequest)
        {
            var etag = NewETag();
            var statusSql = $"""
                INSERT INTO {_reviewsTable}
                    (submission_id, status, etag, created_at, updated_at)
                VALUES (@submission_id, 'changes_requested', @etag, now(), now())
                ON CONFLICT (submission_id) DO UPDATE SET
                    status = 'changes_requested',
                    etag = EXCLUDED.etag,
                    updated_at = now()
                """;
            await using var statusCommand = new NpgsqlCommand(statusSql, connection, transaction);
            statusCommand.Parameters.AddWithValue("submission_id", NpgsqlDbType.Uuid, submissionId);
            statusCommand.Parameters.AddWithValue("etag", NpgsqlDbType.Text, etag);
            _ = await statusCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return comment;
    }

    private static string BuildWhereClause(FieldReviewQuery query)
    {
        var clauses = new List<string>();

        if (!string.IsNullOrWhiteSpace(query.FormId))
        {
            clauses.Add("s.form_id = @form_id");
        }

        if (!string.IsNullOrWhiteSpace(query.ServiceId))
        {
            clauses.Add("s.service_id = @service_id");
        }

        if (query.LayerId is not null)
        {
            clauses.Add("s.layer_id = @layer_id");
        }

        if (!string.IsNullOrWhiteSpace(query.ReviewStatus))
        {
            clauses.Add("COALESCE(r.status, 'pending') = @review_status");
        }

        if (!string.IsNullOrWhiteSpace(query.AssignedTo))
        {
            clauses.Add("r.assigned_to = @assigned_to");
        }

        if (!string.IsNullOrWhiteSpace(query.SyncStatus))
        {
            clauses.Add("s.status = @sync_status");
        }

        if (query.HasConflict is not null)
        {
            clauses.Add("COALESCE(r.has_conflict, false) = @has_conflict");
        }

        if (query.SubmittedFrom is not null)
        {
            clauses.Add("s.created_at >= @submitted_from");
        }

        if (query.SubmittedTo is not null)
        {
            clauses.Add("s.created_at <= @submitted_to");
        }

        return clauses.Count > 0 ? "WHERE " + string.Join(" AND ", clauses) : string.Empty;
    }

    private static void AddFilterParameters(NpgsqlCommand command, FieldReviewQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.FormId))
        {
            command.Parameters.AddWithValue("form_id", NpgsqlDbType.Text, query.FormId);
        }

        if (!string.IsNullOrWhiteSpace(query.ServiceId))
        {
            command.Parameters.AddWithValue("service_id", NpgsqlDbType.Text, query.ServiceId);
        }

        if (query.LayerId is int layerId)
        {
            command.Parameters.AddWithValue("layer_id", NpgsqlDbType.Integer, layerId);
        }

        if (!string.IsNullOrWhiteSpace(query.ReviewStatus))
        {
            command.Parameters.AddWithValue("review_status", NpgsqlDbType.Text, query.ReviewStatus);
        }

        if (!string.IsNullOrWhiteSpace(query.AssignedTo))
        {
            command.Parameters.AddWithValue("assigned_to", NpgsqlDbType.Text, query.AssignedTo);
        }

        if (!string.IsNullOrWhiteSpace(query.SyncStatus))
        {
            command.Parameters.AddWithValue("sync_status", NpgsqlDbType.Text, MapSyncStatusToSubmissionStatus(query.SyncStatus));
        }

        if (query.HasConflict is bool hasConflict)
        {
            command.Parameters.AddWithValue("has_conflict", NpgsqlDbType.Boolean, hasConflict);
        }

        if (query.SubmittedFrom is DateTimeOffset from)
        {
            command.Parameters.AddWithValue("submitted_from", NpgsqlDbType.TimestampTz, from);
        }

        if (query.SubmittedTo is DateTimeOffset to)
        {
            command.Parameters.AddWithValue("submitted_to", NpgsqlDbType.TimestampTz, to);
        }
    }

    private async Task<bool> SubmissionExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid submissionId,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT 1 FROM {_submissionsTable} WHERE submission_id = @submission_id";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Uuid, submissionId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null;
    }

    private async Task<string?> ReadCurrentETagAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid submissionId,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT etag FROM {_reviewsTable} WHERE submission_id = @submission_id FOR UPDATE";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Uuid, submissionId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result as string;
    }

    private async Task<FieldReviewComment[]> ReadCommentsAsync(
        NpgsqlConnection connection,
        Guid submissionId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT comment_id, submission_id, author, body, correction_request, created_at
            FROM {_commentsTable}
            WHERE submission_id = @submission_id
            ORDER BY created_at, comment_id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Uuid, submissionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var comments = new List<FieldReviewComment>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            comments.Add(ReadComment(reader));
        }

        return comments.ToArray();
    }

    private async Task<FieldSubmissionAttachmentInfo[]> ReadAttachmentsAsync(
        NpgsqlConnection connection,
        Guid submissionId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT attachment_id, field_id, filename, content_type, size_bytes, status
            FROM {_attachmentsTable}
            WHERE submission_id = @submission_id
            ORDER BY id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Uuid, submissionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var attachments = new List<FieldSubmissionAttachmentInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            attachments.Add(new FieldSubmissionAttachmentInfo
            {
                AttachmentId = reader.IsDBNull(0) ? null : reader.GetInt64(0),
                FieldId = reader.IsDBNull(1) ? null : reader.GetString(1),
                Filename = reader.IsDBNull(2) ? null : reader.GetString(2),
                ContentType = reader.IsDBNull(3) ? null : reader.GetString(3),
                SizeBytes = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                Status = reader.GetString(5)
            });
        }

        return attachments.ToArray();
    }

    private static FieldSubmissionRecord ReadRecord(NpgsqlDataReader reader)
    {
        var requestSummaryJson = reader.IsDBNull(9) ? null : reader.GetString(9);
        var validationJson = reader.IsDBNull(10) ? null : reader.GetString(10);
        var submissionStatus = reader.GetString(7);

        var deviceId = ExtractStringMember(requestSummaryJson, "clientId");

        return new FieldSubmissionRecord
        {
            SubmissionId = reader.GetGuid(0),
            FormId = reader.GetString(1),
            FormVersion = reader.GetInt32(2),
            ServiceId = reader.GetString(3),
            LayerId = reader.GetInt32(4),
            TargetFeatureId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
            Operation = reader.GetString(6),
            SyncStatus = MapSubmissionStatusToSyncStatus(submissionStatus),
            HasValidationIssues = validationJson is not null,
            Validation = ParseJson(validationJson),
            Geometry = null,
            SubmitterHash = reader.IsDBNull(8) ? null : reader.GetString(8),
            DeviceId = deviceId,
            AttachmentCount = (int)reader.GetInt64(20),
            HasConflict = reader.GetBoolean(19),
            SubmittedAt = reader.GetFieldValue<DateTimeOffset>(11),
            Review = new FieldReviewState
            {
                Status = reader.GetString(12),
                AssignedTo = reader.IsDBNull(13) ? null : reader.GetString(13),
                DecidedBy = reader.IsDBNull(14) ? null : reader.GetString(14),
                DecidedAt = reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15),
                DecisionNote = reader.IsDBNull(16) ? null : reader.GetString(16),
                UpdatedAt = reader.GetFieldValue<DateTimeOffset>(17),
                ETag = reader.IsDBNull(18) ? string.Empty : reader.GetString(18)
            }
        };
    }

    private static FieldReviewState ReadReviewState(NpgsqlDataReader reader)
        => new()
        {
            Status = reader.GetString(0),
            AssignedTo = reader.IsDBNull(1) ? null : reader.GetString(1),
            DecidedBy = reader.IsDBNull(2) ? null : reader.GetString(2),
            DecidedAt = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            DecisionNote = reader.IsDBNull(4) ? null : reader.GetString(4),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(5),
            ETag = reader.GetString(6)
        };

    private static FieldReviewComment ReadComment(NpgsqlDataReader reader)
        => new()
        {
            CommentId = reader.GetGuid(0),
            SubmissionId = reader.GetGuid(1),
            Author = reader.GetString(2),
            Body = reader.GetString(3),
            CorrectionRequest = reader.GetBoolean(4),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(5)
        };

    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractStringMember(string? json, string member)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(member, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string MapSubmissionStatusToSyncStatus(string submissionStatus)
        => submissionStatus switch
        {
            "accepted" => FieldSyncStatus.Synced,
            "rejected" => FieldSyncStatus.Rejected,
            "failed" => FieldSyncStatus.Failed,
            _ => FieldSyncStatus.Pending
        };

    private static string MapSyncStatusToSubmissionStatus(string syncStatus)
        => syncStatus switch
        {
            FieldSyncStatus.Synced => "accepted",
            FieldSyncStatus.Rejected => "rejected",
            FieldSyncStatus.Failed => "failed",
            _ => "pending"
        };

    private static string NewETag()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return "\"" + Convert.ToHexString(bytes).ToLowerInvariant() + "\"";
    }
}
