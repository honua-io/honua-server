// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.FieldWorkflows.Review;
using Npgsql;

namespace Honua.Db.Postgres.Features.FieldWorkflows;

internal static class FieldSubmissionRecordReader
{
    public static FieldSubmissionRecord ReadRecord(NpgsqlDataReader reader)
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

    public static JsonElement? ParseJson(string? json)
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

    public static string? ExtractStringMember(string? json, string member)
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

    public static string MapSubmissionStatusToSyncStatus(string submissionStatus)
        => submissionStatus switch
        {
            "accepted" => FieldSyncStatus.Synced,
            "rejected" => FieldSyncStatus.Rejected,
            "failed" => FieldSyncStatus.Failed,
            _ => FieldSyncStatus.Pending
        };

    public static string MapSyncStatusToSubmissionStatus(string syncStatus)
        => syncStatus switch
        {
            FieldSyncStatus.Synced => "accepted",
            FieldSyncStatus.Rejected => "rejected",
            FieldSyncStatus.Failed => "failed",
            _ => "pending"
        };
}
