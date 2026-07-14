// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.FieldWorkflows.Export;
using Honua.Core.Features.FieldWorkflows.Review;

namespace Honua.Server.Features.FieldWorkflows.Export;

/// <summary>
/// Serializes a back-office field export record set into the requested format
/// (GeoJSON <c>FeatureCollection</c> or CSV). Emits the review-resolved submission
/// projection (form/service/layer/operation/sync state, submitter hash, device id,
/// attachment count, review state, decision metadata, timestamps). Raw field values
/// are intentionally excluded; <c>honua.form_submissions</c> persists only sanitized
/// submission metadata for privacy, so the export carries metadata and review/audit
/// state rather than user-entered values or raw geometry.
/// </summary>
internal static class FieldExportSerializer
{
    private static readonly string[] CsvHeader =
    [
        "submissionId", "formId", "formVersion", "serviceId", "layerId",
        "targetFeatureId", "operation", "syncStatus", "hasValidationIssues",
        "hasConflict", "submitterHash", "deviceId", "attachmentCount", "submittedAt",
        "reviewStatus", "assignedTo", "decidedBy", "decidedAt", "decisionNote"
    ];

    /// <summary>
    /// Serializes <paramref name="items"/> into a UTF-8 byte payload for the
    /// supplied <paramref name="format"/>.
    /// </summary>
    public static byte[] Serialize(string format, FieldSubmissionRecord[] items)
        => format switch
        {
            FieldExportFormat.Csv => SerializeCsv(items),
            _ => SerializeGeoJson(items)
        };

    private static byte[] SerializeGeoJson(FieldSubmissionRecord[] items)
    {
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "FeatureCollection");
            writer.WriteStartArray("features");

            foreach (var item in items)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "Feature");

                // No raw geometry is persisted for field submissions (privacy); the
                // GeoJSON geometry is null and the record metadata lives in properties.
                writer.WriteNull("geometry");

                writer.WriteStartObject("properties");
                writer.WriteString("submissionId", item.SubmissionId.ToString());
                writer.WriteString("formId", item.FormId);
                writer.WriteNumber("formVersion", item.FormVersion);
                writer.WriteString("serviceId", item.ServiceId);
                writer.WriteNumber("layerId", item.LayerId);
                if (item.TargetFeatureId is long targetFeatureId)
                {
                    writer.WriteNumber("targetFeatureId", targetFeatureId);
                }

                writer.WriteString("operation", item.Operation);
                writer.WriteString("syncStatus", item.SyncStatus);
                writer.WriteBoolean("hasValidationIssues", item.HasValidationIssues);
                writer.WriteBoolean("hasConflict", item.HasConflict);
                WriteOptional(writer, "submitterHash", item.SubmitterHash);
                WriteOptional(writer, "deviceId", item.DeviceId);
                writer.WriteNumber("attachmentCount", item.AttachmentCount);
                writer.WriteString("submittedAt", item.SubmittedAt);
                writer.WriteString("reviewStatus", item.Review.Status);
                WriteOptional(writer, "assignedTo", item.Review.AssignedTo);
                WriteOptional(writer, "decidedBy", item.Review.DecidedBy);
                if (item.Review.DecidedAt is DateTimeOffset decidedAt)
                {
                    writer.WriteString("decidedAt", decidedAt);
                }

                WriteOptional(writer, "decisionNote", item.Review.DecisionNote);
                writer.WriteEndObject();

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();

        static void WriteOptional(System.Text.Json.Utf8JsonWriter writer, string name, string? value)
        {
            if (value is not null)
            {
                writer.WriteString(name, value);
            }
        }
    }

    private static byte[] SerializeCsv(FieldSubmissionRecord[] items)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', CsvHeader));

        foreach (var fields in items.Select(item => new[]
        {
            item.SubmissionId.ToString(),
            item.FormId,
            item.FormVersion.ToString(CultureInfo.InvariantCulture),
            item.ServiceId,
            item.LayerId.ToString(CultureInfo.InvariantCulture),
            item.TargetFeatureId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            item.Operation,
            item.SyncStatus,
            item.HasValidationIssues ? "true" : "false",
            item.HasConflict ? "true" : "false",
            item.SubmitterHash ?? string.Empty,
            item.DeviceId ?? string.Empty,
            item.AttachmentCount.ToString(CultureInfo.InvariantCulture),
            item.SubmittedAt.ToString("O", CultureInfo.InvariantCulture),
            item.Review.Status,
            item.Review.AssignedTo ?? string.Empty,
            item.Review.DecidedBy ?? string.Empty,
            item.Review.DecidedAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            item.Review.DecisionNote ?? string.Empty
        }))
        {
            builder.AppendLine(string.Join(',', Array.ConvertAll(fields, EscapeCsv)));
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static string EscapeCsv(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var mustQuote = value.IndexOfAny([',', '"', '\n', '\r']) >= 0;
        if (!mustQuote)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
