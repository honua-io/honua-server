// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Infrastructure.Events;
using Npgsql;

namespace Honua.Server.Features.Streaming;

// One honua.feature_changes row joined to its outbox row, in the column order
// PostgresFeatureChangeLog selects. A change with no outbox row was made directly in the database.
internal sealed record FeatureChangeLogRow(
    long Generation,
    int LayerId,
    long ObjectId,
    short OperationCode,
    DateTimeOffset ChangedAt,
    FeatureChangeLogAttribution Attribution,
    string? PreAttributesJson,
    string? CommittedAttributesJson,
    byte[]? Geometry,
    bool UpdateChangedGeometry)
{
    private const short InsertCode = 1;
    private const short UpdateCode = 2;

    public static FeatureChangeLogRow Read(NpgsqlDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetInt64(2),
            reader.GetInt16(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            new FeatureChangeLogAttribution(
                NullableString(reader, 5),
                NullableString(reader, 6),
                NullableString(reader, 7),
                NullableString(reader, 8),
                NullableString(reader, 9),
                NullableString(reader, 10),
                NullableString(reader, 11),
                NullableString(reader, 12),
                NullableString(reader, 13)),
            NullableString(reader, 14),
            NullableString(reader, 15),
            reader.IsDBNull(16) ? null : reader.GetFieldValue<byte[]>(16),
            !reader.IsDBNull(17) && reader.GetBoolean(17));

    public string Operation => OperationCode switch
    {
        InsertCode => "insert",
        UpdateCode => "update",
        _ => "delete"
    };

    // A delete is described by the image it removed; inserts and updates by the committed image.
    public FeatureChangeEvent ToEvent()
    {
        var (envelope, geometryJson, srid) = FeatureChangeEventEnrichment.FromWkb(Geometry);
        var isDelete = Operation == "delete";
        return new FeatureChangeEvent
        {
            EventId = Attribution.EventId ?? PostgresFeatureChangeLog.DatabaseEventId(Generation),
            Cursor = Generation,
            Timestamp = ChangedAt,
            SourceId = Attribution.SourceId,
            ServiceId = Attribution.ServiceId ?? string.Empty,
            LayerId = LayerId,
            ObjectId = ObjectId,
            Operation = Operation,
            Protocol = Attribution.Protocol ?? PostgresFeatureChangeLog.DatabaseProtocol,
            RequestId = Attribution.RequestId ?? string.Empty,
            OperationInstanceId = Attribution.OperationInstanceId,
            CorrelationId = Attribution.CorrelationId,
            AuditId = Attribution.AuditId,
            ProposalId = Attribution.ProposalId,
            ChangedAttributes = isDelete ? null : ChangedAttributes(),
            GeometryChanged = Operation == "update" ? UpdateChangedGeometry : Geometry is not null,
            GeometryEnvelope = envelope,
            PropertiesJson = isDelete ? PreAttributesJson : CommittedAttributesJson,
            GeometryJson = geometryJson,
            GeometrySrid = geometryJson is null ? null : srid
        };
    }

    // Every committed attribute of an insert; the attributes whose value differs from the previous
    // image for an update. Null when the committed image was not captured.
    private Dictionary<string, object?>? ChangedAttributes()
    {
        if (CommittedAttributesJson is null)
        {
            return null;
        }

        using var committed = JsonDocument.Parse(CommittedAttributesJson);
        if (committed.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        using var previous = PreAttributesJson is null || Operation == "insert" ? null : JsonDocument.Parse(PreAttributesJson);
        var changed = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var attribute in committed.RootElement.EnumerateObject())
        {
            if (previous is null
                || previous.RootElement.ValueKind != JsonValueKind.Object
                || !previous.RootElement.TryGetProperty(attribute.Name, out var before)
                || !JsonElement.DeepEquals(before, attribute.Value))
            {
                changed[attribute.Name] = attribute.Value.Clone();
            }
        }

        return changed;
    }

    private static string? NullableString(NpgsqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}

// Who made a change and through what: the change row's ids and its outbox row's service, protocol
// and request. Every value is null for a change made directly in the database.
internal sealed record FeatureChangeLogAttribution(
    string? EventId,
    string? OperationInstanceId,
    string? CorrelationId,
    string? AuditId,
    string? ProposalId,
    string? SourceId,
    string? ServiceId,
    string? Protocol,
    string? RequestId);
