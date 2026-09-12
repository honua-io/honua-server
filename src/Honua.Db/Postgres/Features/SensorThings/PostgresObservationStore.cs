// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.SensorThings.Abstractions;
using Honua.Core.Features.SensorThings.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.SensorThings;

/// <summary>
/// Postgres implementation of <see cref="IObservationStore"/> over the
/// configured-schema <c>sta_*</c> catalog tables and the range-partitioned
/// <c>sta_observation</c> time-series table (migration 059).
/// </summary>
internal sealed class PostgresObservationStore : IObservationStore
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly IDatabaseSchemaGuard _schemaGuard;
    private readonly ISchemaContext? _schemaContext;
    private readonly string? _configuredSchema;
    private string _thingTable => SchemaSearchPath.QualifyTable("sta_thing", _schemaContext?.CurrentSchema ?? _configuredSchema);
    private string _sensorTable => SchemaSearchPath.QualifyTable("sta_sensor", _schemaContext?.CurrentSchema ?? _configuredSchema);
    private string _observedPropertyTable => SchemaSearchPath.QualifyTable("sta_observed_property", _schemaContext?.CurrentSchema ?? _configuredSchema);
    private string _datastreamTable => SchemaSearchPath.QualifyTable("sta_datastream", _schemaContext?.CurrentSchema ?? _configuredSchema);
    private string _observationTable => SchemaSearchPath.QualifyTable("sta_observation", _schemaContext?.CurrentSchema ?? _configuredSchema);

    // Identifier sequences created by server migration 116. Allocating from a sequence
    // instead of SELECT MAX(id) + 1 is what makes concurrent ingest safe: nextval is
    // non-transactional and never returns the same value to two sessions, so overlapping
    // writers cannot mint the same @iot.id (#4199).
    private string _thingIdSequence => SchemaSearchPath.QualifyTable("sta_thing_id_seq", _schemaContext?.CurrentSchema ?? _configuredSchema);
    private string _sensorIdSequence => SchemaSearchPath.QualifyTable("sta_sensor_id_seq", _schemaContext?.CurrentSchema ?? _configuredSchema);
    private string _observedPropertyIdSequence => SchemaSearchPath.QualifyTable("sta_observed_property_id_seq", _schemaContext?.CurrentSchema ?? _configuredSchema);
    // sta_datastream_id_seq and sta_observation_id_seq are reached through the column
    // default alone: neither entity accepts a client-supplied id, so nothing has to
    // reposition them.

    public PostgresObservationStore(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        IDatabaseSchemaGuard schemaGuard,
        string? schemaName = null,
        ISchemaContext? schemaContext = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _schemaGuard = schemaGuard ?? throw new ArgumentNullException(nameof(schemaGuard));
        _configuredSchema = schemaName;
        _schemaContext = schemaContext;
    }

    private async Task VerifySchemaFloorAsync(CancellationToken cancellationToken)
    {
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await _schemaGuard.VerifyRequirementAsync(
            lease,
            DatabaseSchemaRequirement.SensorThings,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SensorThingsDatastream>> ListDatastreamsAsync(
        CatalogQuery query,
        CancellationToken cancellationToken)
    {
        await VerifySchemaFloorAsync(cancellationToken).ConfigureAwait(false);

        var sql = new System.Text.StringBuilder($"""
SELECT d.id, d.name, d.description, d.observation_type, d.unit_name, d.unit_symbol,
       d.unit_definition, d.thing_id, d.sensor_id, d.observed_property_id,
       MIN(o.phenomenon_time) AS pt_start, MAX(o.phenomenon_time) AS pt_end
FROM {_datastreamTable} d
LEFT JOIN {_observationTable} o ON o.datastream_id = d.id
""");
        AppendWhere(sql, query.WhereSql);
        sql.Append("""

GROUP BY d.id, d.name, d.description, d.observation_type, d.unit_name, d.unit_symbol,
         d.unit_definition, d.thing_id, d.sensor_id, d.observed_property_id
""");
        AppendOrderByAndPaging(sql, query.OrderBySql ?? "d.id ASC");

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql.ToString(), lease);
        AddCatalogParameters(command, query);

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
        await VerifySchemaFloorAsync(cancellationToken).ConfigureAwait(false);

        var sql = $"""
SELECT d.id, d.name, d.description, d.observation_type, d.unit_name, d.unit_symbol,
       d.unit_definition, d.thing_id, d.sensor_id, d.observed_property_id,
       MIN(o.phenomenon_time) AS pt_start, MAX(o.phenomenon_time) AS pt_end
FROM {_datastreamTable} d
LEFT JOIN {_observationTable} o ON o.datastream_id = d.id
WHERE d.id = @id
GROUP BY d.id, d.name, d.description, d.observation_type, d.unit_name, d.unit_symbol,
         d.unit_definition, d.thing_id, d.sensor_id, d.observed_property_id
""";

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDatastream(reader) : null;
    }

    public async Task<IReadOnlyList<SensorThingsThing>> ListThingsAsync(CatalogQuery query, CancellationToken cancellationToken)
    {
        await VerifySchemaFloorAsync(cancellationToken).ConfigureAwait(false);

        var sql = new System.Text.StringBuilder($"SELECT id, name, description FROM {_thingTable}");
        AppendWhere(sql, query.WhereSql);
        AppendOrderByAndPaging(sql, query.OrderBySql ?? "id ASC");
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql.ToString(), lease);
        AddCatalogParameters(command, query);

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
        await VerifySchemaFloorAsync(cancellationToken).ConfigureAwait(false);

        var sql = $"SELECT id, name, description FROM {_thingTable} WHERE id = @id";
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

    public async Task<IReadOnlyList<SensorThingsSensor>> ListSensorsAsync(CatalogQuery query, CancellationToken cancellationToken)
    {
        await VerifySchemaFloorAsync(cancellationToken).ConfigureAwait(false);

        var sql = new System.Text.StringBuilder(
            $"SELECT id, name, description, encoding_type, metadata FROM {_sensorTable}");
        AppendWhere(sql, query.WhereSql);
        AppendOrderByAndPaging(sql, query.OrderBySql ?? "id ASC");
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql.ToString(), lease);
        AddCatalogParameters(command, query);

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
        await VerifySchemaFloorAsync(cancellationToken).ConfigureAwait(false);

        var sql = $"SELECT id, name, description, encoding_type, metadata FROM {_sensorTable} WHERE id = @id";
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSensor(reader) : null;
    }

    public async Task<IReadOnlyList<SensorThingsObservedProperty>> ListObservedPropertiesAsync(
        CatalogQuery query,
        CancellationToken cancellationToken)
    {
        await VerifySchemaFloorAsync(cancellationToken).ConfigureAwait(false);

        var sql = new System.Text.StringBuilder(
            $"SELECT id, name, definition, description FROM {_observedPropertyTable}");
        AppendWhere(sql, query.WhereSql);
        AppendOrderByAndPaging(sql, query.OrderBySql ?? "id ASC");
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql.ToString(), lease);
        AddCatalogParameters(command, query);

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
        await VerifySchemaFloorAsync(cancellationToken).ConfigureAwait(false);

        var sql = $"SELECT id, name, definition, description FROM {_observedPropertyTable} WHERE id = @id";
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadObservedProperty(reader) : null;
    }

    public Task<long> CountThingsAsync(CatalogQuery query, CancellationToken cancellationToken) =>
        CountCatalogAsync(_thingTable, null, query, cancellationToken);

    public Task<long> CountSensorsAsync(CatalogQuery query, CancellationToken cancellationToken) =>
        CountCatalogAsync(_sensorTable, null, query, cancellationToken);

    public Task<long> CountObservedPropertiesAsync(CatalogQuery query, CancellationToken cancellationToken) =>
        CountCatalogAsync(_observedPropertyTable, null, query, cancellationToken);

    // The datastream filter is written against the `d` alias the list query uses, so the
    // count has to introduce the same alias.
    public Task<long> CountDatastreamsAsync(CatalogQuery query, CancellationToken cancellationToken) =>
        CountCatalogAsync(_datastreamTable, "d", query, cancellationToken);

    private async Task<long> CountCatalogAsync(
        string table,
        string? alias,
        CatalogQuery query,
        CancellationToken cancellationToken)
    {
        await VerifySchemaFloorAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        // The table and alias are internal catalog identifiers, never request text; the
        // WHERE fragment is built by the protocol adapter over whitelisted column names and
        // carries its values as @p0..@pN parameters.
        var sql = new System.Text.StringBuilder(
            $"SELECT COUNT(*) FROM {table}{(alias is null ? string.Empty : " " + alias)}");
        AppendWhere(sql, query.WhereSql);
        await using var command = new NpgsqlCommand(sql.ToString(), lease);
        AddFilterParameters(command, query.WhereParameters);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    private static void AppendWhere(System.Text.StringBuilder sql, string? whereSql)
    {
        if (!string.IsNullOrWhiteSpace(whereSql))
        {
            sql.Append(" WHERE (").Append(whereSql).Append(')');
        }
    }

    private static void AppendOrderByAndPaging(System.Text.StringBuilder sql, string orderBySql)
    {
        sql.Append(" ORDER BY ").Append(orderBySql).Append(" OFFSET @skip LIMIT @top");
    }

    private static void AddCatalogParameters(NpgsqlCommand command, CatalogQuery query)
    {
        AddFilterParameters(command, query.WhereParameters);
        command.Parameters.AddWithValue("skip", NpgsqlDbType.Integer, Math.Max(0, query.Skip));
        command.Parameters.AddWithValue("top", NpgsqlDbType.Integer, Math.Max(0, query.Top));
    }

    /// <summary>
    /// Binds the translated <c>$filter</c> values with the PostgreSQL type their column
    /// expects. The translator already refused any literal that does not match the column,
    /// so the CLR type here is authoritative; binding it explicitly keeps an inferred
    /// parameter type from reintroducing a text-versus-double comparison (#4203).
    /// </summary>
    private static void AddFilterParameters(NpgsqlCommand command, IReadOnlyList<object?> parameters)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            var name = "p" + i.ToString(CultureInfo.InvariantCulture);
            switch (parameters[i])
            {
                case null:
                    command.Parameters.AddWithValue(name, DBNull.Value);
                    break;
                case long integer:
                    command.Parameters.AddWithValue(name, NpgsqlDbType.Bigint, integer);
                    break;
                case double number:
                    command.Parameters.AddWithValue(name, NpgsqlDbType.Double, number);
                    break;
                case string text:
                    command.Parameters.AddWithValue(name, NpgsqlDbType.Text, text);
                    break;
                case DateTimeOffset instant:
                    command.Parameters.AddWithValue(name, NpgsqlDbType.TimestampTz, instant);
                    break;
                default:
                    command.Parameters.AddWithValue(name, parameters[i]!);
                    break;
            }
        }
    }

    public async Task<long> CountObservationsAsync(ObservationQuery query, CancellationToken cancellationToken)
    {
        await VerifySchemaFloorAsync(cancellationToken).ConfigureAwait(false);
        var sql = new System.Text.StringBuilder($"SELECT COUNT(*) FROM {_observationTable}");
        AppendObservationFilter(sql, query);
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql.ToString(), lease);
        AddObservationFilterParameters(command, query);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    private static void AppendObservationFilter(System.Text.StringBuilder sql, ObservationQuery query)
    {
        var conditions = new List<string>();
        if (query.DatastreamId.HasValue)
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
    }

    private static void AddObservationFilterParameters(NpgsqlCommand command, ObservationQuery query)
    {
        if (query.DatastreamId is { } id)
        {
            command.Parameters.AddWithValue("datastream_id", NpgsqlDbType.Bigint, id);
        }

        AddFilterParameters(command, query.WhereParameters);
    }

    public async Task<IReadOnlyList<SensorThingsObservation>> QueryObservationsAsync(
        ObservationQuery query,
        CancellationToken cancellationToken)
    {
        await VerifySchemaFloorAsync(cancellationToken).ConfigureAwait(false);

        var sql = new System.Text.StringBuilder(
            $"SELECT id, datastream_id, phenomenon_time, result_time, result, feature_of_interest_id FROM {_observationTable}");

        AppendObservationFilter(sql, query);
        // The ORDER BY body is translated from $orderby against the observation column
        // whitelist; absent it, observations page in phenomenon-time order.
        AppendOrderByAndPaging(sql, query.OrderBySql ?? "phenomenon_time ASC, id ASC");

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql.ToString(), lease);
        AddObservationFilterParameters(command, query);
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
        await VerifySchemaFloorAsync(cancellationToken).ConfigureAwait(false);

        var sql =
            $"SELECT id, datastream_id, phenomenon_time, result_time, result, feature_of_interest_id FROM {_observationTable} WHERE id = @id";
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadObservation(reader) : null;
    }

    public async Task<IReadOnlyList<SensorThingsObservation>> IngestObservationsAsync(
        IReadOnlyList<ObservationIngestRow> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        await VerifySchemaFloorAsync(cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return Array.Empty<SensorThingsObservation>();
        }

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // The id column defaults to nextval(sta_observation_id_seq) (migration 116), so the
        // identifier is allocated by the INSERT itself and returned. There is no read-then-write
        // window for a concurrent writer to observe: reserving the block with MAX(id) + 1 under
        // READ COMMITTED handed overlapping ingests the same ids, and because the observation PK
        // is (id, phenomenon_time) the duplicate rows were accepted silently (#4199).
        var results = new List<SensorThingsObservation>(rows.Count);
        var insertSql = $"""
INSERT INTO {_observationTable} (datastream_id, phenomenon_time, result_time, result, feature_of_interest_id)
VALUES (@datastream_id, @phenomenon_time, @result_time, @result, @feature_of_interest_id)
RETURNING id
""";

        foreach (var row in rows)
        {
            await using var command = new NpgsqlCommand(insertSql, connection, transaction);
            command.Parameters.AddWithValue("datastream_id", NpgsqlDbType.Bigint, row.DatastreamId);
            command.Parameters.AddWithValue("phenomenon_time", NpgsqlDbType.TimestampTz, row.PhenomenonTime);
            command.Parameters.AddWithValue(
                "result_time",
                NpgsqlDbType.TimestampTz,
                (object?)row.ResultTime ?? DBNull.Value);
            command.Parameters.AddWithValue("result", NpgsqlDbType.Double, row.Result);
            command.Parameters.AddWithValue(
                "feature_of_interest_id",
                NpgsqlDbType.Bigint,
                (object?)row.FeatureOfInterestId ?? DBNull.Value);
            var id = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Observation insert did not return a server-allocated id."));

            results.Add(new SensorThingsObservation
            {
                Id = id,
                DatastreamId = row.DatastreamId,
                PhenomenonTime = row.PhenomenonTime,
                ResultTime = row.ResultTime,
                Result = row.Result,
                FeatureOfInterestId = row.FeatureOfInterestId
            });
        }

        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async Task<SensorThingsDatastream> CreateDatastreamAsync(
        CreateDatastreamRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await VerifySchemaFloorAsync(cancellationToken).ConfigureAwait(false);

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var thingId = await UpsertRelatedAsync(
            connection, transaction, _thingTable, _thingIdSequence, request.Thing, cancellationToken).ConfigureAwait(false);
        var sensorId = await UpsertSensorAsync(
            connection, transaction, request.Sensor, cancellationToken).ConfigureAwait(false);
        var observedPropertyId = await UpsertObservedPropertyAsync(
            connection, transaction, request.ObservedProperty, cancellationToken).ConfigureAwait(false);

        var insertSql = $"""
INSERT INTO {_datastreamTable}
    (name, description, observation_type, unit_name, unit_symbol, unit_definition, thing_id, sensor_id, observed_property_id)
VALUES (@name, @description, @observation_type, @unit_name, @unit_symbol, @unit_definition, @thing_id, @sensor_id, @observed_property_id)
RETURNING id
""";

        long datastreamId;
        await using (var command = new NpgsqlCommand(insertSql, connection, transaction))
        {
            command.Parameters.AddWithValue("name", NpgsqlDbType.Text, request.Name);
            command.Parameters.AddWithValue("description", NpgsqlDbType.Text, request.Description);
            command.Parameters.AddWithValue("observation_type", NpgsqlDbType.Text, request.ObservationType);
            command.Parameters.AddWithValue("unit_name", NpgsqlDbType.Text, request.UnitName);
            command.Parameters.AddWithValue("unit_symbol", NpgsqlDbType.Text, request.UnitSymbol);
            command.Parameters.AddWithValue("unit_definition", NpgsqlDbType.Text, request.UnitDefinition);
            command.Parameters.AddWithValue("thing_id", NpgsqlDbType.Bigint, thingId);
            command.Parameters.AddWithValue("sensor_id", NpgsqlDbType.Bigint, sensorId);
            command.Parameters.AddWithValue("observed_property_id", NpgsqlDbType.Bigint, observedPropertyId);
            datastreamId = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Datastream insert did not return a server-allocated id."));
        }

        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);

        return new SensorThingsDatastream
        {
            Id = datastreamId,
            Name = request.Name,
            Description = request.Description,
            ObservationType = request.ObservationType,
            UnitName = request.UnitName,
            UnitSymbol = request.UnitSymbol,
            UnitDefinition = request.UnitDefinition,
            ThingId = thingId,
            SensorId = sensorId,
            ObservedPropertyId = observedPropertyId
        };
    }

    /// <summary>
    /// Advances <paramref name="sequence"/> past a client-supplied identifier so a later
    /// server allocation cannot collide with the row just written. Catalog entities may be
    /// deep-inserted with an explicit <c>@iot.id</c>, which bypasses the column default;
    /// without this the sequence would still be sitting behind that id.
    /// </summary>
    private static async Task AdvanceSequencePastAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sequence,
        long id,
        CancellationToken cancellationToken)
    {
        // The sequence name is an internal schema-qualified identifier built from the
        // validated schema, never request text. GREATEST keeps the sequence monotonic: an
        // explicit id below the current position leaves it untouched.
        await using var command = new NpgsqlCommand(
            $"SELECT setval('{sequence}', GREATEST(last_value, @id), true) FROM {sequence}",
            connection,
            transaction);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        long id,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT 1 FROM {table} WHERE id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<long> UpsertRelatedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        string sequence,
        RelatedEntityRef entity,
        CancellationToken cancellationToken)
    {
        if (entity.Id > 0 && await ExistsAsync(connection, transaction, table, entity.Id, cancellationToken).ConfigureAwait(false))
        {
            return entity.Id;
        }

        if (entity.Id > 0)
        {
            await using var explicitCommand = new NpgsqlCommand(
                $"INSERT INTO {table} (id, name, description) VALUES (@id, @name, @description)", connection, transaction);
            explicitCommand.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, entity.Id);
            explicitCommand.Parameters.AddWithValue("name", NpgsqlDbType.Text, entity.Name ?? $"Thing {entity.Id}");
            explicitCommand.Parameters.AddWithValue("description", NpgsqlDbType.Text, entity.Description ?? string.Empty);
            await explicitCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await AdvanceSequencePastAsync(connection, transaction, sequence, entity.Id, cancellationToken).ConfigureAwait(false);
            return entity.Id;
        }

        await using var command = new NpgsqlCommand(
            $"INSERT INTO {table} (name, description) VALUES (@name, @description) RETURNING id", connection, transaction);
        command.Parameters.AddWithValue("name", NpgsqlDbType.Text, entity.Name ?? "Thing");
        command.Parameters.AddWithValue("description", NpgsqlDbType.Text, entity.Description ?? string.Empty);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Related-entity insert did not return a server-allocated id."));
    }

    private async Task<long> UpsertSensorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RelatedEntityRef entity,
        CancellationToken cancellationToken)
    {
        if (entity.Id > 0 && await ExistsAsync(connection, transaction, _sensorTable, entity.Id, cancellationToken).ConfigureAwait(false))
        {
            return entity.Id;
        }

        if (entity.Id > 0)
        {
            await using var explicitCommand = new NpgsqlCommand(
                $"INSERT INTO {_sensorTable} (id, name, description, encoding_type, metadata) VALUES (@id, @name, @description, 'application/pdf', '')",
                connection,
                transaction);
            explicitCommand.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, entity.Id);
            explicitCommand.Parameters.AddWithValue("name", NpgsqlDbType.Text, entity.Name ?? $"Sensor {entity.Id}");
            explicitCommand.Parameters.AddWithValue("description", NpgsqlDbType.Text, entity.Description ?? string.Empty);
            await explicitCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await AdvanceSequencePastAsync(connection, transaction, _sensorIdSequence, entity.Id, cancellationToken).ConfigureAwait(false);
            return entity.Id;
        }

        await using var command = new NpgsqlCommand(
            $"INSERT INTO {_sensorTable} (name, description, encoding_type, metadata) VALUES (@name, @description, 'application/pdf', '') RETURNING id",
            connection,
            transaction);
        command.Parameters.AddWithValue("name", NpgsqlDbType.Text, entity.Name ?? "Sensor");
        command.Parameters.AddWithValue("description", NpgsqlDbType.Text, entity.Description ?? string.Empty);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Sensor insert did not return a server-allocated id."));
    }

    private async Task<long> UpsertObservedPropertyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RelatedEntityRef entity,
        CancellationToken cancellationToken)
    {
        if (entity.Id > 0 && await ExistsAsync(connection, transaction, _observedPropertyTable, entity.Id, cancellationToken).ConfigureAwait(false))
        {
            return entity.Id;
        }

        if (entity.Id > 0)
        {
            await using var explicitCommand = new NpgsqlCommand(
                $"INSERT INTO {_observedPropertyTable} (id, name, definition, description) VALUES (@id, @name, '', @description)",
                connection,
                transaction);
            explicitCommand.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, entity.Id);
            explicitCommand.Parameters.AddWithValue("name", NpgsqlDbType.Text, entity.Name ?? $"ObservedProperty {entity.Id}");
            explicitCommand.Parameters.AddWithValue("description", NpgsqlDbType.Text, entity.Description ?? string.Empty);
            await explicitCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await AdvanceSequencePastAsync(connection, transaction, _observedPropertyIdSequence, entity.Id, cancellationToken).ConfigureAwait(false);
            return entity.Id;
        }

        await using var command = new NpgsqlCommand(
            $"INSERT INTO {_observedPropertyTable} (name, definition, description) VALUES (@name, '', @description) RETURNING id",
            connection,
            transaction);
        command.Parameters.AddWithValue("name", NpgsqlDbType.Text, entity.Name ?? "ObservedProperty");
        command.Parameters.AddWithValue("description", NpgsqlDbType.Text, entity.Description ?? string.Empty);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("ObservedProperty insert did not return a server-allocated id."));
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
