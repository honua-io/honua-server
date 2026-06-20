// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.SensorThings.Abstractions;
using Honua.Core.Features.SensorThings.Domain;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.SensorThings;

/// <summary>
/// Postgres implementation of <see cref="IObservationStore"/> over the
/// <c>honua.sta_*</c> catalog tables and the range-partitioned
/// <c>honua.sta_observation</c> time-series table (migration 059).
/// </summary>
internal sealed class PostgresObservationStore : IObservationStore
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private bool _schemaEnsured;

    public PostgresObservationStore(IAdoNetDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    // Self-heal the SensorThings schema (mirrors migration 059) so reads work on a
    // fresh-DB container where migrations are skipped (the integration-test harness
    // runs with HONUA_SKIP_MIGRATIONS=true). Idempotent: a no-op once the migration
    // has run. Includes the synthetic demo datastream + 48-point series.
    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaEnsured)
        {
            return;
        }

        const string sql = @"
CREATE TABLE IF NOT EXISTS honua.sta_thing (
    id bigint NOT NULL, name text NOT NULL, description text NOT NULL DEFAULT '',
    CONSTRAINT sta_thing_pkey PRIMARY KEY (id));
CREATE TABLE IF NOT EXISTS honua.sta_sensor (
    id bigint NOT NULL, name text NOT NULL, description text NOT NULL DEFAULT '',
    encoding_type text NOT NULL DEFAULT 'application/pdf', metadata text NOT NULL DEFAULT '',
    CONSTRAINT sta_sensor_pkey PRIMARY KEY (id));
CREATE TABLE IF NOT EXISTS honua.sta_observed_property (
    id bigint NOT NULL, name text NOT NULL, definition text NOT NULL DEFAULT '', description text NOT NULL DEFAULT '',
    CONSTRAINT sta_observed_property_pkey PRIMARY KEY (id));
CREATE TABLE IF NOT EXISTS honua.sta_datastream (
    id bigint NOT NULL, name text NOT NULL, description text NOT NULL DEFAULT '',
    observation_type text NOT NULL DEFAULT 'http://www.opengis.net/def/observationType/OGC-OM/2.0/OM_Measurement',
    unit_name text NOT NULL DEFAULT '', unit_symbol text NOT NULL DEFAULT '', unit_definition text NOT NULL DEFAULT '',
    thing_id bigint NOT NULL, sensor_id bigint NOT NULL, observed_property_id bigint NOT NULL,
    CONSTRAINT sta_datastream_pkey PRIMARY KEY (id));
CREATE TABLE IF NOT EXISTS honua.sta_observation (
    id bigint NOT NULL, datastream_id bigint NOT NULL, phenomenon_time timestamptz NOT NULL,
    result_time timestamptz, result double precision NOT NULL, feature_of_interest_id bigint,
    CONSTRAINT sta_observation_pkey PRIMARY KEY (id, phenomenon_time)) PARTITION BY RANGE (phenomenon_time);
CREATE TABLE IF NOT EXISTS honua.sta_observation_default PARTITION OF honua.sta_observation DEFAULT;
CREATE INDEX IF NOT EXISTS ix_sta_observation_time ON honua.sta_observation USING BRIN (phenomenon_time);
CREATE INDEX IF NOT EXISTS ix_sta_observation_datastream_time ON honua.sta_observation (datastream_id, phenomenon_time);

INSERT INTO honua.sta_thing (id, name, description)
VALUES (1, 'Demo Air Quality Station', 'Synthetic air-quality monitoring station for the SensorThings Phase 1 demo')
ON CONFLICT (id) DO NOTHING;
INSERT INTO honua.sta_sensor (id, name, description, encoding_type, metadata)
VALUES (1, 'Demo Thermometer', 'Synthetic temperature sensor', 'application/pdf', 'https://example.org/sensors/demo-thermometer')
ON CONFLICT (id) DO NOTHING;
INSERT INTO honua.sta_observed_property (id, name, definition, description)
VALUES (1, 'Air Temperature', 'http://mmisw.org/ont/cf/parameter/air_temperature', 'Ambient air temperature')
ON CONFLICT (id) DO NOTHING;
INSERT INTO honua.sta_datastream (id, name, description, observation_type, unit_name, unit_symbol, unit_definition, thing_id, sensor_id, observed_property_id)
VALUES (1, 'Demo Air Temperature', 'Synthetic air-temperature datastream for the SensorThings Phase 1 demo',
    'http://www.opengis.net/def/observationType/OGC-OM/2.0/OM_Measurement', 'degree Celsius', 'C',
    'http://unitsofmeasure.org/ucum.html#para-30', 1, 1, 1)
ON CONFLICT (id) DO NOTHING;
INSERT INTO honua.sta_observation (id, datastream_id, phenomenon_time, result_time, result, feature_of_interest_id)
SELECT gs, 1, (now() - ((48 - gs) || ' hours')::interval), (now() - ((48 - gs) || ' hours')::interval),
    15.0 + 10.0 * sin(gs::double precision), NULL::bigint
FROM generate_series(1, 48) AS gs
ON CONFLICT (id, phenomenon_time) DO NOTHING;";

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _schemaEnsured = true;
    }

    public async Task<IReadOnlyList<SensorThingsDatastream>> ListDatastreamsAsync(
        int skip,
        int top,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        const string sql = @"
SELECT d.id, d.name, d.description, d.observation_type, d.unit_name, d.unit_symbol,
       d.unit_definition, d.thing_id, d.sensor_id, d.observed_property_id,
       MIN(o.phenomenon_time) AS pt_start, MAX(o.phenomenon_time) AS pt_end
FROM honua.sta_datastream d
LEFT JOIN honua.sta_observation o ON o.datastream_id = d.id
GROUP BY d.id, d.name, d.description, d.observation_type, d.unit_name, d.unit_symbol,
         d.unit_definition, d.thing_id, d.sensor_id, d.observed_property_id
ORDER BY d.id
OFFSET @skip LIMIT @top";

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease);
        command.Parameters.AddWithValue("skip", NpgsqlDbType.Integer, skip);
        command.Parameters.AddWithValue("top", NpgsqlDbType.Integer, top);

        var results = new List<SensorThingsDatastream>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadDatastream(reader));
        }

        return results;
    }

    public async Task<SensorThingsDatastream?> GetDatastreamAsync(long id, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        const string sql = @"
SELECT d.id, d.name, d.description, d.observation_type, d.unit_name, d.unit_symbol,
       d.unit_definition, d.thing_id, d.sensor_id, d.observed_property_id,
       MIN(o.phenomenon_time) AS pt_start, MAX(o.phenomenon_time) AS pt_end
FROM honua.sta_datastream d
LEFT JOIN honua.sta_observation o ON o.datastream_id = d.id
WHERE d.id = @id
GROUP BY d.id, d.name, d.description, d.observation_type, d.unit_name, d.unit_symbol,
         d.unit_definition, d.thing_id, d.sensor_id, d.observed_property_id";

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDatastream(reader) : null;
    }

    public async Task<IReadOnlyList<SensorThingsThing>> ListThingsAsync(int skip, int top, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        const string sql = "SELECT id, name, description FROM honua.sta_thing ORDER BY id OFFSET @skip LIMIT @top";
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease);
        command.Parameters.AddWithValue("skip", NpgsqlDbType.Integer, skip);
        command.Parameters.AddWithValue("top", NpgsqlDbType.Integer, top);

        var results = new List<SensorThingsThing>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new SensorThingsThing
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                Description = reader.GetString(2)
            });
        }

        return results;
    }

    public async Task<SensorThingsThing?> GetThingAsync(long id, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        const string sql = "SELECT id, name, description FROM honua.sta_thing WHERE id = @id";
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new SensorThingsThing
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1),
            Description = reader.GetString(2)
        };
    }

    public async Task<IReadOnlyList<SensorThingsSensor>> ListSensorsAsync(int skip, int top, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        const string sql = "SELECT id, name, description, encoding_type, metadata FROM honua.sta_sensor ORDER BY id OFFSET @skip LIMIT @top";
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease);
        command.Parameters.AddWithValue("skip", NpgsqlDbType.Integer, skip);
        command.Parameters.AddWithValue("top", NpgsqlDbType.Integer, top);

        var results = new List<SensorThingsSensor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadSensor(reader));
        }

        return results;
    }

    public async Task<SensorThingsSensor?> GetSensorAsync(long id, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        const string sql = "SELECT id, name, description, encoding_type, metadata FROM honua.sta_sensor WHERE id = @id";
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSensor(reader) : null;
    }

    public async Task<IReadOnlyList<SensorThingsObservedProperty>> ListObservedPropertiesAsync(
        int skip,
        int top,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        const string sql = "SELECT id, name, definition, description FROM honua.sta_observed_property ORDER BY id OFFSET @skip LIMIT @top";
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease);
        command.Parameters.AddWithValue("skip", NpgsqlDbType.Integer, skip);
        command.Parameters.AddWithValue("top", NpgsqlDbType.Integer, top);

        var results = new List<SensorThingsObservedProperty>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadObservedProperty(reader));
        }

        return results;
    }

    public async Task<SensorThingsObservedProperty?> GetObservedPropertyAsync(long id, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        const string sql = "SELECT id, name, definition, description FROM honua.sta_observed_property WHERE id = @id";
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadObservedProperty(reader) : null;
    }

    public async Task<IReadOnlyList<SensorThingsObservation>> QueryObservationsAsync(
        ObservationQuery query,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        var sql = new System.Text.StringBuilder(
            "SELECT id, datastream_id, phenomenon_time, result_time, result, feature_of_interest_id FROM honua.sta_observation");

        var conditions = new List<string>();
        if (query.DatastreamId is { } datastreamId)
        {
            conditions.Add("datastream_id = @datastream_id");
        }

        if (!string.IsNullOrWhiteSpace(query.WhereSql))
        {
            conditions.Add($"({query.WhereSql})");
        }

        if (conditions.Count > 0)
        {
            sql.Append(" WHERE ").Append(string.Join(" AND ", conditions));
        }

        sql.Append(" ORDER BY phenomenon_time ").Append(query.OrderByDescending ? "DESC" : "ASC");
        sql.Append(", id ").Append(query.OrderByDescending ? "DESC" : "ASC");
        sql.Append(" OFFSET @skip LIMIT @top");

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql.ToString(), lease);
        if (query.DatastreamId is { } id)
        {
            command.Parameters.AddWithValue("datastream_id", NpgsqlDbType.Bigint, id);
        }

        for (var i = 0; i < query.WhereParameters.Count; i++)
        {
            command.Parameters.AddWithValue(
                "p" + i.ToString(CultureInfo.InvariantCulture),
                query.WhereParameters[i] ?? DBNull.Value);
        }

        command.Parameters.AddWithValue("skip", NpgsqlDbType.Integer, Math.Max(0, query.Skip));
        command.Parameters.AddWithValue("top", NpgsqlDbType.Integer, Math.Max(0, query.Top));

        var results = new List<SensorThingsObservation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadObservation(reader));
        }

        return results;
    }

    public async Task<SensorThingsObservation?> GetObservationAsync(long id, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        const string sql =
            "SELECT id, datastream_id, phenomenon_time, result_time, result, feature_of_interest_id FROM honua.sta_observation WHERE id = @id";
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadObservation(reader) : null;
    }

    private static SensorThingsDatastream ReadDatastream(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Name = reader.GetString(1),
        Description = reader.GetString(2),
        ObservationType = reader.GetString(3),
        UnitName = reader.GetString(4),
        UnitSymbol = reader.GetString(5),
        UnitDefinition = reader.GetString(6),
        ThingId = reader.GetInt64(7),
        SensorId = reader.GetInt64(8),
        ObservedPropertyId = reader.GetInt64(9),
        PhenomenonTimeStart = reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
        PhenomenonTimeEnd = reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11)
    };

    private static SensorThingsSensor ReadSensor(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Name = reader.GetString(1),
        Description = reader.GetString(2),
        EncodingType = reader.GetString(3),
        Metadata = reader.GetString(4)
    };

    private static SensorThingsObservedProperty ReadObservedProperty(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Name = reader.GetString(1),
        Definition = reader.GetString(2),
        Description = reader.GetString(3)
    };

    private static SensorThingsObservation ReadObservation(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        DatastreamId = reader.GetInt64(1),
        PhenomenonTime = reader.GetFieldValue<DateTimeOffset>(2),
        ResultTime = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
        Result = reader.GetDouble(4),
        FeatureOfInterestId = reader.IsDBNull(5) ? null : reader.GetInt64(5)
    };
}
