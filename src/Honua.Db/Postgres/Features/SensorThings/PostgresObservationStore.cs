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
internal sealed partial class PostgresObservationStore : IObservationStore, ISensorThingsEntityStore
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
        query = WithDatastreamRelationships(query);
        await VerifySchemaFloorAsync(cancellationToken).ConfigureAwait(false);

        var sql = new System.Text.StringBuilder($"""
SELECT d.id, d.name, d.description, d.observation_type, d.unit_name, d.unit_symbol,
       d.unit_definition, d.thing_id, d.sensor_id, d.observed_property_id,
       MIN(o.phenomenon_time) AS pt_start, MAX(COALESCE(o.phenomenon_time_end,o.phenomenon_time)) AS pt_end
FROM {_datastreamTable} d
LEFT JOIN {_observationTable} o ON o.datastream_reference_id = d.id
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
        await using var reader = await ExecuteCatalogReaderAsync(command, cancellationToken).ConfigureAwait(false);
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
       MIN(o.phenomenon_time) AS pt_start, MAX(COALESCE(o.phenomenon_time_end,o.phenomenon_time)) AS pt_end
FROM {_datastreamTable} d
LEFT JOIN {_observationTable} o ON o.datastream_reference_id = d.id
WHERE d.id = @id
GROUP BY d.id, d.name, d.description, d.observation_type, d.unit_name, d.unit_symbol,
         d.unit_definition, d.thing_id, d.sensor_id, d.observed_property_id
""";

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);

        await using var reader = await ExecuteCatalogReaderAsync(command, cancellationToken).ConfigureAwait(false);
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
        await using var reader = await ExecuteCatalogReaderAsync(command, cancellationToken).ConfigureAwait(false);
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

        await using var reader = await ExecuteCatalogReaderAsync(command, cancellationToken).ConfigureAwait(false);
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
            $"SELECT id, name, description, encoding_type, COALESCE(metadata_json,to_jsonb(metadata))::text FROM {_sensorTable}");
        AppendWhere(sql, query.WhereSql);
        AppendOrderByAndPaging(sql, query.OrderBySql ?? "id ASC");
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql.ToString(), lease);
        AddCatalogParameters(command, query);

        var results = new List<SensorThingsSensor>();
        await using var reader = await ExecuteCatalogReaderAsync(command, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadSensor(reader));
        }

        return results;
    }

    public async Task<SensorThingsSensor?> GetSensorAsync(long id, CancellationToken cancellationToken)
    {
        await VerifySchemaFloorAsync(cancellationToken).ConfigureAwait(false);

        var sql = $"SELECT id, name, description, encoding_type, COALESCE(metadata_json,to_jsonb(metadata))::text FROM {_sensorTable} WHERE id = @id";
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);

        await using var reader = await ExecuteCatalogReaderAsync(command, cancellationToken).ConfigureAwait(false);
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
        await using var reader = await ExecuteCatalogReaderAsync(command, cancellationToken).ConfigureAwait(false);
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

        await using var reader = await ExecuteCatalogReaderAsync(command, cancellationToken).ConfigureAwait(false);
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
        CountCatalogAsync(_datastreamTable, "d", WithDatastreamRelationships(query), cancellationToken);

    private static CatalogQuery WithDatastreamRelationships(CatalogQuery query)
    {
        if (query.DatastreamThingId is null && query.DatastreamSensorId is null && query.DatastreamObservedPropertyId is null)
        {
            return query;
        }

        var predicates = new List<string>();
        var parameters = query.WhereParameters.ToList();
        if (!string.IsNullOrWhiteSpace(query.WhereSql))
        {
            predicates.Add($"({query.WhereSql})");
        }

        AddRelationship("d.thing_id", query.DatastreamThingId);
        AddRelationship("d.sensor_id", query.DatastreamSensorId);
        AddRelationship("d.observed_property_id", query.DatastreamObservedPropertyId);
        return query with { WhereSql = string.Join(" AND ", predicates), WhereParameters = parameters };

        void AddRelationship(string column, long? id)
        {
            if (id is not { } value)
            {
                return;
            }

            // Columns are provider-owned literals; values follow the client filter's
            // parameters so navigation can never overwrite a filter parameter.
            predicates.Add($"{column} = @p{parameters.Count.ToString(CultureInfo.InvariantCulture)}");
            parameters.Add(value);
        }
    }

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
        return (long)(await ExecuteCatalogScalarAsync(command, cancellationToken).ConfigureAwait(false) ?? 0L);
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
        return (long)(await ExecuteCatalogScalarAsync(command, cancellationToken).ConfigureAwait(false) ?? 0L);
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
            $"SELECT id, datastream_id, phenomenon_time, result_time, result, feature_of_interest_reference_id, result_json, phenomenon_time_end FROM {_observationTable}");

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
        await using var reader = await ExecuteCatalogReaderAsync(command, cancellationToken).ConfigureAwait(false);
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
            $"SELECT id, datastream_id, phenomenon_time, result_time, result, feature_of_interest_reference_id, result_json, phenomenon_time_end FROM {_observationTable} WHERE id = @id";
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);

        await using var reader = await ExecuteCatalogReaderAsync(command, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadObservation(reader) : null;
    }

    public async Task<IReadOnlyList<SensorThingsObservation>> IngestObservationsAsync(
        IReadOnlyList<ObservationIngestRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) return [];
        var bodies = rows.Select(row =>
        {
            var body = new System.Text.Json.Nodes.JsonObject
            {
                ["Datastream"] = new System.Text.Json.Nodes.JsonObject { ["@iot.id"] = row.DatastreamId },
                ["phenomenonTime"] = row.PhenomenonTime.ToString("O", CultureInfo.InvariantCulture),
                ["resultTime"] = row.ResultTime?.ToString("O", CultureInfo.InvariantCulture),
                ["result"] = row.Result
            };
            if (row.FeatureOfInterestId is { } feature) body["FeatureOfInterest"] = new System.Text.Json.Nodes.JsonObject { ["@iot.id"] = feature };
            return System.Text.Json.JsonSerializer.SerializeToElement(body, EntityJsonContext.Default.JsonObject);
        }).ToArray();
        var ids = await CreateEntityBatchAsync("Observations", bodies, cancellationToken).ConfigureAwait(false);
        var persisted = new List<SensorThingsObservation>(ids.Count);
        foreach (var id in ids) persisted.Add((await GetObservationAsync(id, cancellationToken).ConfigureAwait(false))!);
        return persisted;
    }

    public async Task<SensorThingsDatastream> CreateDatastreamAsync(
        CreateDatastreamRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = new System.Text.Json.Nodes.JsonObject
        {
            ["name"] = request.Name, ["description"] = request.Description, ["observationType"] = request.ObservationType,
            ["unitOfMeasurement"] = new System.Text.Json.Nodes.JsonObject
            { ["name"] = request.UnitName, ["symbol"] = request.UnitSymbol, ["definition"] = request.UnitDefinition },
            ["Thing"] = Reference(request.Thing), ["Sensor"] = Reference(request.Sensor), ["ObservedProperty"] = Reference(request.ObservedProperty)
        };
        var id = await CreateEntityAsync("Datastreams", System.Text.Json.JsonSerializer.SerializeToElement(body, EntityJsonContext.Default.JsonObject), cancellationToken).ConfigureAwait(false);
        return (await GetDatastreamAsync(id, cancellationToken).ConfigureAwait(false))!;

        static System.Text.Json.Nodes.JsonObject Reference(RelatedEntityRef entity)
        {
            if (entity.Id > 0 && entity.Name is null) return new() { ["@iot.id"] = entity.Id };
            var value = new System.Text.Json.Nodes.JsonObject { ["name"] = entity.Name, ["description"] = entity.Description };
            if (entity.EncodingType is not null) value["encodingType"] = entity.EncodingType;
            if (entity.Metadata is { } metadata) value["metadata"] = System.Text.Json.Nodes.JsonNode.Parse(metadata.GetRawText());
            if (entity.Definition is not null) value["definition"] = entity.Definition;
            return value;
        }
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
        return await ExecuteCatalogScalarAsync(command, cancellationToken).ConfigureAwait(false) is not null;
    }

    private static Task<NpgsqlDataReader> ExecuteCatalogReaderAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        // codeql[cs/sql-injection]: schema identifiers are allow-listed
        // (\A[A-Za-z_][A-Za-z0-9_]{0,62}\z) and quoted by NpgsqlCommandBuilder before
        // interpolation. X-Honua-Test-Schema is rejected by that same pattern before it
        // is stored. Table and sequence names are compile-time catalog identifiers.
        // $filter and $orderby values are bound as parameters.
        return command.ExecuteReaderAsync(cancellationToken);
    }

    private static Task<object?> ExecuteCatalogScalarAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        // codeql[cs/sql-injection]: see ExecuteCatalogReaderAsync. The command text is
        // the same allow-listed identifier shape; values stay in parameters.
        return command.ExecuteScalarAsync(cancellationToken);
    }

    private static Task<int> ExecuteCatalogNonQueryAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        // codeql[cs/sql-injection]: see ExecuteCatalogReaderAsync. The command text is
        // the same allow-listed identifier shape; values stay in parameters.
        return command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SensorThingsDatastream ReadDatastream(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Name = reader.GetString(1),
        Description = reader.GetString(2),
        ObservationType = reader.GetString(3),
        UnitName = reader.IsDBNull(4) ? null : reader.GetString(4),
        UnitSymbol = reader.IsDBNull(5) ? null : reader.GetString(5),
        UnitDefinition = reader.IsDBNull(6) ? null : reader.GetString(6),
        ThingId = reader.GetInt64(7),
        SensorId = reader.GetInt64(8),
        ObservedPropertyId = reader.GetInt64(9),
        PhenomenonTimeStart = reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
        PhenomenonTimeEnd = reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11)
    };

    private static SensorThingsSensor ReadSensor(NpgsqlDataReader reader)
    {
        var metadata = ParseJsonValue(reader.GetString(4));
        return new()
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1),
            Description = reader.GetString(2),
            EncodingType = reader.GetString(3),
            Metadata = metadata.ValueKind == System.Text.Json.JsonValueKind.String ? metadata.GetString()! : metadata.GetRawText(),
            JsonMetadata = metadata
        };
    }

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
        Result = reader.IsDBNull(4) ? null : reader.GetDouble(4),
        FeatureOfInterestId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
        JsonResult = reader.IsDBNull(6) ? null : ParseJsonValue(reader.GetString(6)),
        PhenomenonTimeEnd = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)
    };

    private static System.Text.Json.JsonElement ParseJsonValue(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
