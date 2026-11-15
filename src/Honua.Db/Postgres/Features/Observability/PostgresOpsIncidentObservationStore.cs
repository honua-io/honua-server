// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Observability;

internal sealed class PostgresOpsIncidentObservationStore : IOpsIncidentObservationStore
{
    private readonly IAdoNetDatabaseConnectionProvider _connections;
    private readonly string _table;
    private const string Columns = "authority_scope, source_id, observation_id, finding_id, observed_at, state, evidence";

    public PostgresOpsIncidentObservationStore(IAdoNetDatabaseConnectionProvider connections, string? schemaName = null)
    {
        _connections = connections;
        _table = SchemaSearchPath.QualifyTable("ops_incident_observations", schemaName);
    }

    public async Task<OpsIncidentObservation> AppendAsync(OpsIncidentObservation observation, CancellationToken cancellationToken = default)
    {
        ValidateObservation(observation);
        await using var connection = await _connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_table} ({Columns})
            VALUES (@authority, @source, @id, @finding, @observed, @state, @evidence)
            ON CONFLICT (authority_scope, source_id, observation_id)
            DO UPDATE SET observation_id = EXCLUDED.observation_id
            RETURNING {Columns};
            """, connection);
        command.Parameters.AddWithValue("authority", NpgsqlDbType.Text, observation.AuthorityScope);
        command.Parameters.AddWithValue("source", NpgsqlDbType.Text, observation.SourceId);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Text, observation.ObservationId);
        command.Parameters.AddWithValue("finding", NpgsqlDbType.Text, observation.FindingId);
        command.Parameters.AddWithValue("observed", NpgsqlDbType.TimestampTz, observation.ObservedAt.ToUniversalTime());
        command.Parameters.AddWithValue("state", NpgsqlDbType.Integer, (int)observation.State);
        command.Parameters.AddWithValue("evidence", NpgsqlDbType.Text, observation.EvidenceJson);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return ReadObservation(reader);
    }

    public async Task<OpsIncidentObservationPage> ReadAsync(OpsIncidentObservationQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateIdentity(query.AuthorityScope);
        if (query.Limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Observation limit must be between 1 and 1000.");
        }
        await using var connection = await _connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT {Columns} FROM {_table}
            WHERE authority_scope = @authority AND observed_at >= @since
            ORDER BY observed_at DESC, source_id, observation_id LIMIT @limit;
            """, connection);
        command.Parameters.AddWithValue("authority", NpgsqlDbType.Text, query.AuthorityScope);
        command.Parameters.AddWithValue("since", NpgsqlDbType.TimestampTz, query.Since.ToUniversalTime());
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, query.Limit + 1);
        var observations = new List<OpsIncidentObservation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            observations.Add(ReadObservation(reader));
        }
        var truncated = observations.Count > query.Limit;
        if (truncated)
        {
            observations.RemoveAt(observations.Count - 1);
        }
        return new OpsIncidentObservationPage(observations) { Truncated = truncated };
    }

    private static OpsIncidentObservation ReadObservation(NpgsqlDataReader reader) => new()
    {
        AuthorityScope = reader.GetString(0),
        SourceId = reader.GetString(1),
        ObservationId = reader.GetString(2),
        FindingId = reader.GetString(3),
        ObservedAt = reader.GetFieldValue<DateTimeOffset>(4),
        State = (OpsIncidentObservationState)reader.GetInt32(5),
        EvidenceJson = reader.GetString(6)
    };

    private static void ValidateObservation(OpsIncidentObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ValidateIdentity(observation.AuthorityScope);
        ValidateIdentity(observation.SourceId);
        ValidateIdentity(observation.ObservationId);
        ValidateIdentity(observation.FindingId);
        if (!Enum.IsDefined(observation.State))
        {
            throw new ArgumentException("Observation state must be Active, Recovered or Unknown.", nameof(observation));
        }
        if (observation.EvidenceJson == null || Encoding.UTF8.GetByteCount(observation.EvidenceJson) > 65536)
        {
            throw new ArgumentException("Observation evidence must contain at most 65536 UTF-8 bytes.", nameof(observation));
        }
        try
        {
            using var document = JsonDocument.Parse(observation.EvidenceJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Observation evidence must be valid JSON.", nameof(observation), exception);
        }
    }

    private static void ValidateIdentity(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Encoding.UTF8.GetByteCount(value) > 256)
        {
            throw new ArgumentException("Observation identities must contain at most 256 UTF-8 bytes.", nameof(value));
        }
    }
}
