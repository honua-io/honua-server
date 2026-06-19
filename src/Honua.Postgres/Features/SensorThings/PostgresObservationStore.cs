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

    public PostgresObservationStore(IAdoNetDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    public async Task<IReadOnlyList<SensorThingsDatastream>> ListDatastreamsAsync(
        int skip,
        int top,
        CancellationToken cancellationToken)
    {
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
