// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Projects opaque audit detail into the bounded allow-listed metadata exposed by Operate.
/// </summary>
internal static class AuditDetailsProjection
{
    private const int MaxInputCharacters = 16 * 1024;
    private const int MaxOutputBytes = 2048;
    private const int MaxEvidenceReferences = 6;

    private static readonly string[] SensitiveMarkers =
    [
        "password",
        "passwd",
        "secret",
        "token",
        "authorization",
        "api-key",
        "apikey",
        "connectionstring",
        "connection string",
        "private key",
        "bearer ",
        "postgres://",
        "mysql://",
        "server=",
        "user id=",
        "select ",
        "insert ",
        "update ",
        "delete ",
        "stacktrace",
        "stack trace",
        "npgsql",
        "sqlstate",
        "provider error",
    ];

    /// <summary>Returns sanitized JSON, or null when the source is absent, malformed, or unsafe.</summary>
    public static string? Project(AuditEventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (string.IsNullOrWhiteSpace(record.Details) || record.Details.Length > MaxInputCharacters)
        {
            return BuildDerivedOnly(record);
        }

        try
        {
            using var document = JsonDocument.Parse(record.Details, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                var wrote = false;
                wrote |= WriteString(document.RootElement, writer, "findingId", 192);
                wrote |= WriteString(document.RootElement, writer, "rule", 128);
                wrote |= WriteString(document.RootElement, writer, "kind", 64);
                wrote |= WriteString(document.RootElement, writer, "actionDiscriminator", 128);
                wrote |= WriteString(document.RootElement, writer, "operationId", 192);
                var wroteMode = WriteString(document.RootElement, writer, "mode", 32);
                var wroteStatus = WriteString(document.RootElement, writer, "status", 32);
                wrote |= wroteMode || wroteStatus;

                if (!wroteMode &&
                    string.Equals(record.ResourceType, "operation_autonomy", StringComparison.Ordinal))
                {
                    writer.WriteString("mode", "AutoApply");
                    wrote = true;
                }

                if (!wroteStatus &&
                    TryDeriveStatus(record, out var derivedStatus))
                {
                    writer.WriteString("status", derivedStatus);
                    wrote = true;
                }

                if (document.RootElement.TryGetProperty("killSwitchEnabled", out var killSwitch) &&
                    killSwitch.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    writer.WriteBoolean("killSwitchEnabled", killSwitch.GetBoolean());
                    wrote = true;
                }

                wrote |= WriteEvidenceReferences(document.RootElement, writer);
                if (IsKnownOperationAudit(record) &&
                    document.RootElement.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String &&
                    IsSafeText(message.GetString(), 384))
                {
                    writer.WriteString("message", message.GetString());
                    wrote = true;
                }

                if (IsKnownDatabaseMigrationAudit(record))
                {
                    wrote |= WriteString(document.RootElement, writer, "outcome", 32);
                    wrote |= WriteInt64(document.RootElement, writer, "durationMilliseconds", 0, 86_400_000);
                    wrote |= WriteString(document.RootElement, writer, "stderr", 512);
                }

                writer.WriteEndObject();
                writer.Flush();
                if (!wrote || buffer.WrittenCount > MaxOutputBytes)
                {
                    return null;
                }
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? BuildDerivedOnly(AuditEventRecord record)
    {
        if (!TryDeriveStatus(record, out var status))
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        if (string.Equals(record.ResourceType, "operation_autonomy", StringComparison.Ordinal))
        {
            writer.WriteString("mode", "AutoApply");
        }

        writer.WriteString("status", status);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static bool WriteString(
        JsonElement root,
        Utf8JsonWriter writer,
        string propertyName,
        int maxLength)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            !IsSafeText(property.GetString(), maxLength))
        {
            return false;
        }

        writer.WriteString(propertyName, property.GetString());
        return true;
    }

    private static bool WriteEvidenceReferences(JsonElement root, Utf8JsonWriter writer)
    {
        if (!root.TryGetProperty("evidenceRefs", out var evidenceRefs) ||
            evidenceRefs.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var values = evidenceRefs
            .EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String && IsSafeText(item.GetString(), 96))
            .Take(MaxEvidenceReferences)
            .Select(static item => item.GetString()!)
            .ToArray();
        if (values.Length == 0)
        {
            return false;
        }

        writer.WritePropertyName("evidenceRefs");
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
        return true;
    }

    private static bool WriteInt64(
        JsonElement root,
        Utf8JsonWriter writer,
        string propertyName,
        long minimum,
        long maximum)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt64(out var value) ||
            value < minimum ||
            value > maximum)
        {
            return false;
        }

        writer.WriteNumber(propertyName, value);
        return true;
    }

    private static bool IsKnownOperationAudit(AuditEventRecord record)
        => record.ResourceType is "operation_autonomy" or "operation_proposal"
            && record.Action.StartsWith("operation.", StringComparison.Ordinal);

    private static bool IsKnownDatabaseMigrationAudit(AuditEventRecord record)
        => string.Equals(record.ResourceType, "database_migration", StringComparison.Ordinal)
            && record.Action.StartsWith("migration.", StringComparison.Ordinal);

    private static bool IsSafeText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength ||
            value.Any(static ch => char.IsControl(ch)))
        {
            return false;
        }

        return !SensitiveMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryDeriveStatus(AuditEventRecord record, out string status)
    {
        status = record.Action switch
        {
            "operation.proposed" => "AwaitingApproval",
            "operation.rejected" => "Rejected",
            "operation.auto_executed" => "Executed",
            "operation.auto_verified" when record.Outcome == AuditOutcome.Success => "Converged",
            "operation.auto_verified" => "NotConverged",
            "operation.auto_compensated" when record.Outcome == AuditOutcome.Success => "RolledBack",
            "operation.auto_compensated" => "CompensationFailed",
            "operation.auto_applied" => "Succeeded",
            "operation.auto_failed" => "Failed",
            "operation.auto_rolled_back" => "RolledBack",
            "operation.auto_indeterminate" => "Indeterminate",
            "operation.auto_canceled" => "Canceled",
            _ => string.Empty,
        };
        return status.Length > 0;
    }
}
