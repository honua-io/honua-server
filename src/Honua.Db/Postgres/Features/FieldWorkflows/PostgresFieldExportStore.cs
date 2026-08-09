// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.FieldWorkflows.Export;
using Honua.Core.Features.FieldWorkflows.Review;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.FieldWorkflows;

/// <summary>
/// PostgreSQL-backed store for back-office field export packages (#1160). Selects
/// the reviewed submission record set via the same projection the review surface
/// uses (<c>honua.form_submissions</c> joined with
/// <c>honua.field_submission_reviews</c>) and persists an audited export record in
/// <c>honua.field_export_records</c>.
/// </summary>
internal sealed class PostgresFieldExportStore : IFieldExportStore
{
    private const int MaxExportRows = 50_000;
    private const int MaxListLimit = 500;

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly string _submissionsTable;
    private readonly string _attachmentsTable;
    private readonly string _reviewsTable;
    private readonly string _exportsTable;

    public PostgresFieldExportStore(IAdoNetDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _submissionsTable = SchemaSearchPath.QualifyTable("form_submissions", "honua");
        _attachmentsTable = SchemaSearchPath.QualifyTable("form_submission_attachments", "honua");
        _reviewsTable = SchemaSearchPath.QualifyTable("field_submission_reviews", "honua");
        _exportsTable = SchemaSearchPath.QualifyTable("field_export_records", "honua");
    }

    public async Task<FieldExportPackage> CreateExportAsync(
        FieldExportRequest request,
        string requestedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);

        var where = BuildWhereClause(request);
        var selectSql = $"""
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
            LIMIT @max_rows
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);

        var items = new List<FieldSubmissionRecord>();
        await using (var command = new NpgsqlCommand(selectSql, connection))
        {
            AddFilterParameters(command, request);
            command.Parameters.AddWithValue("max_rows", NpgsqlDbType.Integer, MaxExportRows);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(ReadRecord(reader));
            }
        }

        var exportId = Guid.NewGuid();
        var filterJson = BuildFilterJson(request);
        var insertSql = $"""
            INSERT INTO {_exportsTable}
                (export_id, format, record_count, filter, requested_by, created_at)
            VALUES (@export_id, @format, @record_count, @filter, @requested_by, now())
            RETURNING export_id, format, record_count, filter::text, requested_by, created_at
            """;

        FieldExportRecord record;
        await using (var command = new NpgsqlCommand(insertSql, connection))
        {
            command.Parameters.AddWithValue("export_id", NpgsqlDbType.Uuid, exportId);
            command.Parameters.AddWithValue("format", NpgsqlDbType.Text, request.Format);
            command.Parameters.AddWithValue("record_count", NpgsqlDbType.Bigint, (long)items.Count);
            command.Parameters.AddWithValue("filter", NpgsqlDbType.Jsonb, filterJson);
            command.Parameters.AddWithValue("requested_by", NpgsqlDbType.Text, requestedBy);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            record = ReadExportRecord(reader);
        }

        return new FieldExportPackage
        {
            Record = record,
            Items = items.ToArray()
        };
    }

    public async Task<FieldExportRecordListResult> ListExportsAsync(
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        var clampedLimit = Math.Clamp(limit <= 0 ? 50 : limit, 1, MaxListLimit);
        var clampedOffset = Math.Max(0, offset);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);

        long total;
        await using (var countCommand = new NpgsqlCommand($"SELECT COUNT(*) FROM {_exportsTable}", connection))
        {
            total = (long)(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        }

        var listSql = $"""
            SELECT export_id, format, record_count, filter::text, requested_by, created_at
            FROM {_exportsTable}
            ORDER BY created_at DESC, export_id
            LIMIT @limit OFFSET @offset
            """;

        var items = new List<FieldExportRecord>();
        await using (var command = new NpgsqlCommand(listSql, connection))
        {
            command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, clampedLimit);
            command.Parameters.AddWithValue("offset", NpgsqlDbType.Integer, clampedOffset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(ReadExportRecord(reader));
            }
        }

        return new FieldExportRecordListResult
        {
            Items = items.ToArray(),
            Total = total,
            Limit = clampedLimit,
            Offset = clampedOffset
        };
    }

    private static string BuildWhereClause(FieldExportRequest request)
    {
        var clauses = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.FormId))
        {
            clauses.Add("s.form_id = @form_id");
        }

        if (!string.IsNullOrWhiteSpace(request.ServiceId))
        {
            clauses.Add("s.service_id = @service_id");
        }

        if (request.LayerId is not null)
        {
            clauses.Add("s.layer_id = @layer_id");
        }

        if (!string.IsNullOrWhiteSpace(request.ReviewStatus))
        {
            clauses.Add("COALESCE(r.status, 'pending') = @review_status");
        }

        if (!string.IsNullOrWhiteSpace(request.AssignedTo))
        {
            clauses.Add("r.assigned_to = @assigned_to");
        }

        if (!string.IsNullOrWhiteSpace(request.SyncStatus))
        {
            clauses.Add("s.status = @sync_status");
        }

        if (request.HasConflict is not null)
        {
            clauses.Add("COALESCE(r.has_conflict, false) = @has_conflict");
        }

        if (request.SubmittedFrom is not null)
        {
            clauses.Add("s.created_at >= @submitted_from");
        }

        if (request.SubmittedTo is not null)
        {
            clauses.Add("s.created_at <= @submitted_to");
        }

        return clauses.Count > 0 ? "WHERE " + string.Join(" AND ", clauses) : string.Empty;
    }

    private static void AddFilterParameters(NpgsqlCommand command, FieldExportRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.FormId))
        {
            command.Parameters.AddWithValue("form_id", NpgsqlDbType.Text, request.FormId);
        }

        if (!string.IsNullOrWhiteSpace(request.ServiceId))
        {
            command.Parameters.AddWithValue("service_id", NpgsqlDbType.Text, request.ServiceId);
        }

        if (request.LayerId is int layerId)
        {
            command.Parameters.AddWithValue("layer_id", NpgsqlDbType.Integer, layerId);
        }

        if (!string.IsNullOrWhiteSpace(request.ReviewStatus))
        {
            command.Parameters.AddWithValue("review_status", NpgsqlDbType.Text, request.ReviewStatus);
        }

        if (!string.IsNullOrWhiteSpace(request.AssignedTo))
        {
            command.Parameters.AddWithValue("assigned_to", NpgsqlDbType.Text, request.AssignedTo);
        }

        if (!string.IsNullOrWhiteSpace(request.SyncStatus))
        {
            command.Parameters.AddWithValue("sync_status", NpgsqlDbType.Text, MapSyncStatusToSubmissionStatus(request.SyncStatus));
        }

        if (request.HasConflict is bool hasConflict)
        {
            command.Parameters.AddWithValue("has_conflict", NpgsqlDbType.Boolean, hasConflict);
        }

        if (request.SubmittedFrom is DateTimeOffset from)
        {
            command.Parameters.AddWithValue("submitted_from", NpgsqlDbType.TimestampTz, from);
        }

        if (request.SubmittedTo is DateTimeOffset to)
        {
            command.Parameters.AddWithValue("submitted_to", NpgsqlDbType.TimestampTz, to);
        }
    }

    private static string BuildFilterJson(FieldExportRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("format", request.Format);
            WriteOptionalString(writer, "formId", request.FormId);
            WriteOptionalString(writer, "serviceId", request.ServiceId);
            if (request.LayerId is int layerId)
            {
                writer.WriteNumber("layerId", layerId);
            }

            WriteOptionalString(writer, "reviewStatus", request.ReviewStatus);
            WriteOptionalString(writer, "assignedTo", request.AssignedTo);
            WriteOptionalString(writer, "syncStatus", request.SyncStatus);
            if (request.HasConflict is bool hasConflict)
            {
                writer.WriteBoolean("hasConflict", hasConflict);
            }

            if (request.SubmittedFrom is DateTimeOffset from)
            {
                writer.WriteString("submittedFrom", from);
            }

            if (request.SubmittedTo is DateTimeOffset to)
            {
                writer.WriteString("submittedTo", to);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());

        static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                writer.WriteString(name, value);
            }
        }
    }

    private static FieldExportRecord ReadExportRecord(NpgsqlDataReader reader)
        => new()
        {
            ExportId = reader.GetGuid(0),
            Format = reader.GetString(1),
            RecordCount = reader.GetInt64(2),
            Filter = reader.IsDBNull(3) ? "{}" : reader.GetString(3),
            RequestedBy = reader.GetString(4),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(5)
        };

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
}
