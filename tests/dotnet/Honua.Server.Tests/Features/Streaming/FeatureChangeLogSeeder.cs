// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.ControlPlane;
using Honua.Infrastructure.Events;
using Honua.Server.Features.Streaming;
using Honua.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Tests.Features.Streaming;

// Writes a change the way a committed edit does: a honua.feature_changes row, whose insert notifies
// every task's stream tail, plus a dispatched outbox row that attributes it to a service, protocol
// and request. Streams, replay and the admin change feed then read it back like any other edit.
internal static class FeatureChangeLogSeeder
{
    private const short InsertCode = 1;
    private const short UpdateCode = 2;
    private const short DeleteCode = 3;

    public static async Task<FeatureChangeEvent> AppendChangeAsync(
        this WebAppFixture fixture,
        FeatureChangeEventRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(request);
        var database = fixture.GetService<JobStoreDatabase>();
        var eventId = string.IsNullOrWhiteSpace(request.EventId) ? Guid.NewGuid().ToString("N") : request.EventId;
        await database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var transaction = await connection.BeginTransactionAsync(token);
                await using (var outbox = OutboxInsert(database, connection, request, eventId))
                {
                    await outbox.ExecuteNonQueryAsync(token);
                }

                await using (var change = ChangeInsert(database, connection, request, eventId))
                {
                    await change.ExecuteNonQueryAsync(token);
                }

                token.ThrowIfCancellationRequested();
                await transaction.CommitAsync(CancellationToken.None);
                return true;
            },
            StatementRepeat.NotAfterSend,
            cancellationToken);

        return await fixture.GetService<PostgresFeatureChangeLog>().ReadByEventIdAsync(eventId, cancellationToken)
            ?? throw new InvalidOperationException($"The change {eventId} was written but cannot be read back.");
    }

    // Without the relay, a change reaches sessions only through the durable-store poll.
    public static WebAppFixture WithoutRealtimeRelay(this WebAppFixture fixture)
        => fixture.ConfigureServices(services =>
        {
            foreach (var relay in services.Where(d => d.ImplementationType == typeof(FeatureStreamRealtimeRelay) ||
                         d.ImplementationFactory?.Method.DeclaringType == typeof(FeatureStreamRealtimeRelay)).ToArray())
            {
                services.Remove(relay);
            }
        });

    private static NpgsqlCommand OutboxInsert(
        JobStoreDatabase database,
        NpgsqlConnection connection,
        FeatureChangeEventRequest request,
        string eventId)
    {
        var command = new NpgsqlCommand(
            $"""
            INSERT INTO {database.Table("feature_change_outbox")}
                (service_id, layer_id, object_id, operation, protocol, source_id, request_id, event_id,
                 event_payload, status, dispatched_at)
            VALUES (@service, @layer, @object, @operation, @protocol, @source, @request, @event_id,
                    jsonb_build_object(), 'dispatched', now())
            """,
            connection);
        command.Parameters.AddWithValue("service", request.ServiceId);
        command.Parameters.AddWithValue("layer", request.LayerId);
        command.Parameters.AddWithValue("object", request.ObjectId);
        command.Parameters.AddWithValue("operation", request.Operation);
        command.Parameters.AddWithValue("protocol", request.Protocol);
        command.Parameters.Add(Text("source", request.SourceId));
        command.Parameters.AddWithValue("request", request.RequestId);
        command.Parameters.AddWithValue("event_id", eventId);
        return command;
    }

    private static NpgsqlCommand ChangeInsert(
        JobStoreDatabase database,
        NpgsqlConnection connection,
        FeatureChangeEventRequest request,
        string eventId)
    {
        var operation = OperationCode(request.Operation);
        var image = request.PropertiesJson ?? SerializeChangedAttributes(request.ChangedAttributes);
        var geometry = request.GeometryJson ?? GeometryFromEnvelope(request.GeometryEnvelope);
        var command = new NpgsqlCommand(
            $"""
            INSERT INTO {database.Table("feature_changes")}
                (generation, layer_id, objectid, operation, changed_at, event_id, operation_instance_id,
                 correlation_id, audit_id, proposal_id, pre_attributes, committed_attributes, pre_geometry,
                 committed_geometry)
            VALUES (nextval(@sequence::regclass), @layer, @object, @operation, COALESCE(@changed_at, now()),
                    @event_id, @operation_instance_id, @correlation_id, @audit_id, @proposal_id,
                    CASE WHEN @operation = {DeleteCode} THEN @image::jsonb END,
                    CASE WHEN @operation <> {DeleteCode} THEN @image::jsonb END,
                    CASE WHEN @operation = {DeleteCode} THEN ST_SetSRID(ST_GeomFromGeoJSON(@geometry), @srid) END,
                    CASE WHEN @operation <> {DeleteCode} THEN ST_SetSRID(ST_GeomFromGeoJSON(@geometry), @srid) END)
            """,
            connection);
        command.Parameters.AddWithValue("sequence", database.Table("sync_generation"));
        command.Parameters.AddWithValue("layer", request.LayerId);
        command.Parameters.AddWithValue("object", request.ObjectId);
        command.Parameters.Add(new NpgsqlParameter("operation", NpgsqlDbType.Smallint) { Value = operation });
        command.Parameters.Add(new NpgsqlParameter("changed_at", NpgsqlDbType.TimestampTz) { Value = (object?)request.Timestamp ?? DBNull.Value });
        command.Parameters.AddWithValue("event_id", eventId);
        command.Parameters.Add(Text("operation_instance_id", request.OperationInstanceId));
        command.Parameters.Add(Text("correlation_id", request.CorrelationId));
        command.Parameters.Add(Text("audit_id", request.AuditId));
        command.Parameters.Add(Text("proposal_id", request.ProposalId));
        command.Parameters.Add(Text("image", image));
        command.Parameters.Add(Text("geometry", geometry));
        command.Parameters.Add(new NpgsqlParameter("srid", NpgsqlDbType.Integer) { Value = request.GeometrySrid ?? 4326 });
        return command;
    }

    private static short OperationCode(string operation)
        => operation.Trim().ToLowerInvariant() switch
        {
            "create" or "insert" => InsertCode,
            "delete" => DeleteCode,
            _ => UpdateCode
        };

    private static string? SerializeChangedAttributes(Dictionary<string, object?>? changedAttributes)
        => changedAttributes is null
            ? null
            : JsonSerializer.Serialize(changedAttributes.ToDictionary(
                pair => pair.Key,
                pair => pair.Value is null ? null : pair.Value.ToString()));

    private static string? GeometryFromEnvelope(double[]? envelope)
    {
        if (envelope is not { Length: 4 })
        {
            return null;
        }

        string Coordinate(double x, double y)
            => string.Create(CultureInfo.InvariantCulture, $"[{x},{y}]");

        if (envelope[0] == envelope[2] && envelope[1] == envelope[3])
        {
            return $$"""{"type":"Point","coordinates":{{Coordinate(envelope[0], envelope[1])}}}""";
        }

        var ring = string.Join(
            ",",
            Coordinate(envelope[0], envelope[1]),
            Coordinate(envelope[2], envelope[1]),
            Coordinate(envelope[2], envelope[3]),
            Coordinate(envelope[0], envelope[3]),
            Coordinate(envelope[0], envelope[1]));
        return $$"""{"type":"Polygon","coordinates":[[{{ring}}]]}""";
    }

    private static NpgsqlParameter Text(string name, string? value)
        => new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };
}
