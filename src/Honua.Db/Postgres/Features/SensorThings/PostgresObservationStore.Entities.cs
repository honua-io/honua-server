// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.SensorThings.Abstractions;
using Honua.Core.Features.SensorThings.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;
using Honua.Core.Features.Geometry.Services;

namespace Honua.Db.Postgres.Features.SensorThings;

internal sealed partial class PostgresObservationStore
{
    private sealed record EntityColumn(string Name, string Column, NpgsqlDbType Type, bool Required = false, bool AllowNull = false);
    private static readonly EntityColumn[] NamedColumns =
        [new("name", "name", NpgsqlDbType.Text, true), new("description", "description", NpgsqlDbType.Text, true), new("properties", "properties", NpgsqlDbType.Jsonb)];

    private static EntityColumn[] Columns(string set) => set switch
    {
        "Things" => NamedColumns,
        "Sensors" => [.. NamedColumns, new("encodingType", "encoding_type", NpgsqlDbType.Text, true), new("metadata", "metadata_json", NpgsqlDbType.Jsonb, true)],
        "ObservedProperties" => [.. NamedColumns, new("definition", "definition", NpgsqlDbType.Text, true)],
        "Locations" => [.. NamedColumns, new("encodingType", "encoding_type", NpgsqlDbType.Text, true), new("location", "location", NpgsqlDbType.Jsonb, true)],
        "FeaturesOfInterest" => [.. NamedColumns, new("encodingType", "encoding_type", NpgsqlDbType.Text, true), new("feature", "feature", NpgsqlDbType.Jsonb, true)],
        "HistoricalLocations" => [new("time", "time", NpgsqlDbType.TimestampTz, true)],
        "Datastreams" => [.. NamedColumns, new("observationType", "observation_type", NpgsqlDbType.Text, true),
            new("unitOfMeasurement/name", "unit_name", NpgsqlDbType.Text, true, true), new("unitOfMeasurement/symbol", "unit_symbol", NpgsqlDbType.Text, true, true),
            new("unitOfMeasurement/definition", "unit_definition", NpgsqlDbType.Text, true, true), new("observedArea", "observed_area", NpgsqlDbType.Jsonb)],
        "Observations" => [new("phenomenonTime", "phenomenon_time", NpgsqlDbType.TimestampTz, true), new("resultTime", "result_time", NpgsqlDbType.TimestampTz),
            new("result", "result_json", NpgsqlDbType.Jsonb, true), new("validTime", "valid_time", NpgsqlDbType.Text),
            new("resultQuality", "result_quality", NpgsqlDbType.Jsonb), new("parameters", "parameters", NpgsqlDbType.Jsonb)],
        _ => throw new SensorThingsValidationException("Unknown sensing entity set.")
    };

    private string EntityTable(string set) => SchemaSearchPath.QualifyTable(set switch
    {
        "Things" => "sta_thing",
        "Sensors" => "sta_sensor",
        "ObservedProperties" => "sta_observed_property",
        "Datastreams" => "sta_datastream",
        "Observations" => "sta_observation",
        "Locations" => "sta_location",
        "HistoricalLocations" => "sta_historical_location",
        "FeaturesOfInterest" => "sta_feature_of_interest",
        _ => throw new SensorThingsValidationException("Unknown sensing entity set.")
    }, _schemaContext?.CurrentSchema ?? _configuredSchema);

    private string AssociationTable(string name) => SchemaSearchPath.QualifyTable(name, _schemaContext?.CurrentSchema ?? _configuredSchema);

    public async Task<JsonElement?> GetEntityAsync(string entitySet, long id, CancellationToken cancellationToken)
    {
        await VerifySchemaFloorAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"SELECT {EntityJsonSql(entitySet)} FROM {EntityTable(entitySet)} d WHERE id=@id", lease);
        command.Parameters.AddWithValue("id", id);
        var value = await ExecuteCatalogScalarAsync(command, cancellationToken).ConfigureAwait(false);
        return value is string json ? NormalizeEntity(entitySet, json) : null;
    }

    public async Task<SensorThingsEntityPage> QueryEntitiesAsync(string entitySet, SensorThingsEntityQuery query, CancellationToken cancellationToken)
    {
        await VerifySchemaFloorAsync(cancellationToken).ConfigureAwait(false);
        var predicates = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.WhereSql)) predicates.Add($"({query.WhereSql})");
        if (query.ParentSet is not null && query.ParentId is not null && query.Navigation is not null)
            predicates.Add(RelationshipPredicate(query.ParentSet, query.Navigation));
        var where = predicates.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", predicates);
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var count = new NpgsqlCommand($"SELECT count(*) FROM {EntityTable(entitySet)} d{where}", lease);
        BindQuery(count, query);
        var total = (long)(await ExecuteCatalogScalarAsync(count, cancellationToken).ConfigureAwait(false) ?? 0L);
        await using var command = new NpgsqlCommand($"SELECT {EntityJsonSql(entitySet)} FROM {EntityTable(entitySet)} d{where} ORDER BY {query.OrderBySql} OFFSET @skip LIMIT @top", lease);
        BindQuery(command, query);
        command.Parameters.AddWithValue("skip", query.Skip);
        command.Parameters.AddWithValue("top", query.Top);
        var result = new List<JsonElement>();
        await using var reader = await ExecuteCatalogReaderAsync(command, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(NormalizeEntity(entitySet, reader.GetString(0)));
        return new(result, total);
    }

    private static void BindQuery(NpgsqlCommand command, SensorThingsEntityQuery query)
    {
        AddFilterParameters(command, query.Parameters);
        if (query.ParentId is { } parent) command.Parameters.AddWithValue("parent", parent);
    }

    private string EntityJsonSql(string set, string alias = "d")
    {
        if (set != "Datastreams") return $"to_jsonb({alias})";
        var geometry = GeoJsonGeometrySql("f.feature", "f.encoding_type");
        return $"""
            to_jsonb({alias}) || (SELECT jsonb_build_object(
                'computed_phenomenon_start', min(o.phenomenon_time),
                'computed_phenomenon_end', max(COALESCE(o.phenomenon_time_end,o.phenomenon_time)),
                'computed_result_start', min(o.result_time), 'computed_result_end', max(o.result_time),
                'computed_observed_area', ST_AsGeoJSON(ST_MakeEnvelope(ST_XMin(max(extent.bounds::text)::box2d),ST_YMin(max(extent.bounds::text)::box2d),ST_XMax(max(extent.bounds::text)::box2d),ST_YMax(max(extent.bounds::text)::box2d),4326))::jsonb)
                FROM (SELECT ST_Extent({geometry}) AS bounds FROM {_observationTable} geometry_observation
                    LEFT JOIN {EntityTable("FeaturesOfInterest")} f ON f.id=geometry_observation.feature_of_interest_reference_id
                    WHERE geometry_observation.datastream_reference_id={alias}.id) extent
                LEFT JOIN {_observationTable} o ON o.datastream_reference_id={alias}.id
                )
            """;
    }

    private static string GeoJsonGeometrySql(string json, string? encoding = null)
    {
        var geometry = $"CASE WHEN {json}->>'type'='Feature' THEN {json}->'geometry' WHEN {json}->>'type'='FeatureCollection' THEN jsonb_build_object('type','GeometryCollection','geometries',COALESCE((SELECT jsonb_agg(f->'geometry') FROM jsonb_array_elements({json}->'features') f WHERE f->'geometry' IS NOT NULL AND f->'geometry'<>'null'::jsonb),'[]'::jsonb)) ELSE {json} END";
        if (encoding is not null) geometry = $"CASE WHEN lower({encoding}) IN ('application/vnd.geo+json','application/geo+json') THEN {geometry} END";
        return $"ST_SetSRID(ST_GeomFromGeoJSON(NULLIF({geometry},'null'::jsonb)),4326)";
    }

    private string RelationshipPredicate(string parent, string navigation, string childAlias = "d", string parentExpression = "@parent") => ((parent, navigation) switch
    {
        ("Things", "Datastreams") => "d.thing_id=@parent",
        ("Sensors", "Datastreams") => "d.sensor_id=@parent",
        ("ObservedProperties", "Datastreams") => "d.observed_property_id=@parent",
        ("Datastreams", "Observations") => "d.datastream_reference_id=@parent",
        ("FeaturesOfInterest", "Observations") => "d.feature_of_interest_reference_id=@parent",
        ("Things", "HistoricalLocations") => "d.thing_id=@parent",
        ("Datastreams", "Thing") => $"d.id=(SELECT thing_id FROM {EntityTable("Datastreams")} WHERE id=@parent)",
        ("Datastreams", "Sensor") => $"d.id=(SELECT sensor_id FROM {EntityTable("Datastreams")} WHERE id=@parent)",
        ("Datastreams", "ObservedProperty") => $"d.id=(SELECT observed_property_id FROM {EntityTable("Datastreams")} WHERE id=@parent)",
        ("Observations", "Datastream") => $"d.id=(SELECT datastream_reference_id FROM {EntityTable("Observations")} WHERE id=@parent)",
        ("Observations", "FeatureOfInterest") => $"d.id=(SELECT feature_of_interest_reference_id FROM {EntityTable("Observations")} WHERE id=@parent)",
        ("HistoricalLocations", "Thing") => $"d.id=(SELECT thing_id FROM {EntityTable("HistoricalLocations")} WHERE id=@parent)",
        ("Things", "Locations") => $"d.id IN (SELECT location_id FROM {AssociationTable("sta_thing_location")} WHERE thing_id=@parent)",
        ("Locations", "Things") => $"d.id IN (SELECT thing_id FROM {AssociationTable("sta_thing_location")} WHERE location_id=@parent)",
        ("HistoricalLocations", "Locations") => $"d.id IN (SELECT location_id FROM {AssociationTable("sta_historical_location_location")} WHERE historical_location_id=@parent)",
        ("Locations", "HistoricalLocations") => $"d.id IN (SELECT historical_location_id FROM {AssociationTable("sta_historical_location_location")} WHERE location_id=@parent)",
        _ => throw new SensorThingsValidationException("Unknown sensing relationship.")
    }).Replace("d.", childAlias + ".", StringComparison.Ordinal).Replace("@parent", parentExpression, StringComparison.Ordinal);

    private static JsonElement NormalizeEntity(string set, string json)
    {
        var row = JsonNode.Parse(json)!.AsObject();
        var body = new JsonObject { ["@iot.id"] = row["id"]!.DeepClone() };
        foreach (var column in Columns(set))
        {
            var value = row[column.Column];
            if (value is null && !column.Required && column.Name != "resultTime") continue;
            if (column.Name.Contains('/', StringComparison.Ordinal))
            {
                var segments = column.Name.Split('/');
                if (body[segments[0]] is not JsonObject nested) body[segments[0]] = nested = new JsonObject();
                nested[segments[1]] = value?.DeepClone();
            }
            else body[column.Name] = value?.DeepClone();
        }
        if (set == "Observations")
        {
            body["result"] = (row["result_json"] ?? row["result"])?.DeepClone();
            if (row["phenomenon_time_end"] is { } end) body["phenomenonTime"] = $"{row["phenomenon_time"]!.GetValue<string>()}/{end.GetValue<string>()}";
        }
        if (set == "Sensors") body["metadata"] = (row["metadata_json"] ?? row["metadata"])?.DeepClone();
        if (set == "Datastreams")
        {
            body.Remove("observedArea");
            foreach (var (property, prefix) in new[] { ("phenomenonTime", "computed_phenomenon"), ("resultTime", "computed_result") })
                if (row[prefix + "_start"] is { } start && row[prefix + "_end"] is { } end)
                    body[property] = start.GetValue<string>() + "/" + end.GetValue<string>();
            if (row["computed_observed_area"] is { } area) body["observedArea"] = area.DeepClone();
        }
        return JsonSerializer.SerializeToElement(body, EntityJsonContext.Default.JsonObject);
    }

    public async Task<long> CountUnresolvedFeaturesAsync(CancellationToken cancellationToken)
    {
        await VerifySchemaFloorAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {_observationTable} WHERE feature_of_interest_reference_id IS NULL OR datastream_reference_id IS NULL", lease);
        return (long)(await ExecuteCatalogScalarAsync(command, cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    public async Task<long> CreateEntityAsync(string entitySet, JsonElement body, CancellationToken cancellationToken)
        => (await CreateEntityBatchAsync(entitySet, [body], cancellationToken).ConfigureAwait(false))[0];

    public async Task<IReadOnlyList<long>> CreateEntityBatchAsync(string entitySet, IReadOnlyList<JsonElement> bodies, CancellationToken cancellationToken)
    {
        if (bodies.Count is 0 or > 1000) throw new SensorThingsValidationException("Entity batch must contain between 1 and 1000 entities.");
        await VerifySchemaFloorAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await lease.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (entitySet != "Observations" || bodies.Any(ContainsLocationMutation))
            await LockCatalogRelationshipsAsync(lease.Connection, transaction, cancellationToken).ConfigureAwait(false);
        var ids = new List<long>(bodies.Count);
        foreach (var body in bodies) ids.Add(await InsertEntityAsync(lease.Connection, transaction, entitySet, ParseBody(body), 0, cancellationToken).ConfigureAwait(false));
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return ids;
    }

    private static bool ContainsLocationMutation(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Any(property => property.Name is "Locations" or "Things" or "HistoricalLocations" || ContainsLocationMutation(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().Any(ContainsLocationMutation),
        _ => false
    };

    private async Task LockCatalogRelationshipsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        // Take this schema-scoped lock before entity row locks. Catalog binding and
        // encoding mutations then see each other's committed association state;
        // direct Observation ingestion retains its independent concurrency.
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@identity,0))", connection, transaction);
        command.Parameters.AddWithValue("identity", (_schemaContext?.CurrentSchema ?? _configuredSchema ?? "public") + ":honua:sensing:catalog-relationships");
        await ExecuteCatalogNonQueryAsync(command, ct).ConfigureAwait(false);
    }

    private static JsonObject ParseBody(JsonElement body) => body.ValueKind == JsonValueKind.Object
        ? JsonNode.Parse(body.GetRawText())!.AsObject() : throw new SensorThingsValidationException("An entity must be a JSON object.");

    private async Task<long> ResolveEntityAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string set, JsonNode? value, int depth, CancellationToken ct)
    {
        if (value is not JsonObject body) throw new SensorThingsValidationException($"{set} must be an entity object or an @iot.id reference.");
        if (body.Count == 1 && body["@iot.id"] is { } key)
        {
            if (!key.AsValue().TryGetValue<long>(out var id) || id <= 0) throw new SensorThingsValidationException("@iot.id must be a positive integer.");
            if (!await ExistsAsync(connection, transaction, EntityTable(set), id, ct).ConfigureAwait(false))
                throw new SensorThingsValidationException($"Referenced {set}({id}) does not exist.");
            return id;
        }
        return await InsertEntityAsync(connection, transaction, set, body, depth + 1, ct).ConfigureAwait(false);
    }

    private async Task<long> InsertEntityAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string set, JsonObject body, int depth, CancellationToken ct)
    {
        if (depth > 20) throw new SensorThingsValidationException("Deep insert exceeds the maximum relationship depth.");
        body.Remove("@iot.id");
        // Existing clients create measurement datastreams without observationType.
        if (set == "Datastreams" && !body.ContainsKey("observationType"))
            body["observationType"] = "http://www.opengis.net/def/observationType/OGC-OM/2.0/OM_Measurement";
        ValidateMembers(set, body);
        if (set == "HistoricalLocations" && body["Locations"] is not JsonArray { Count: > 0 })
            throw new SensorThingsValidationException("HistoricalLocation requires at least one Location.");
        var columns = new List<string>();
        var values = new List<(NpgsqlDbType Type, object Value)>();
        foreach (var column in Columns(set))
        {
            var value = FindValue(body, column.Name);
            if (set == "Observations" && column.Name == "phenomenonTime" && value is null) value = JsonValue.Create(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            if (set == "Observations" && column.Name == "result" && value is null && body.ContainsKey("result"))
            { columns.Add(column.Column); values.Add((column.Type, "null")); continue; }
            if (value is null && column.AllowNull && HasMember(body, column.Name))
            { columns.Add(column.Column); values.Add((column.Type, DBNull.Value)); continue; }
            if (value is null && column.Required) throw new SensorThingsValidationException($"{column.Name} is required for {set}.");
            if (value is null) continue;
            columns.Add(column.Column);
            values.Add((column.Type, ConvertValue(column, value)));
        }
        if (set == "Observations") AddObservationInterval(body, columns, values);
        if (set == "Observations" && body["result"] is JsonValue numeric && numeric.TryGetValue<double>(out var number))
        { columns.Add("result"); values.Add((NpgsqlDbType.Double, number)); }
        foreach (var (navigation, column) in ForeignKeys(set))
        {
            long related;
            if (body[navigation] is { } reference) related = await ResolveEntityAsync(connection, transaction, SensorThingsRelationships.For(set)[navigation].Target, reference, depth, ct).ConfigureAwait(false);
            else if (set == "Observations" && navigation == "FeatureOfInterest")
            {
                var dsIndex = columns.IndexOf("datastream_id");
                var inferred = await InferFeatureAsync(connection, transaction, (long)values[dsIndex].Value, ct).ConfigureAwait(false);
                // A Thing with no location cannot invent a feature. The column stays null,
                // which is the pre-preview observation shape those creates already stored.
                if (inferred is null) continue;
                related = inferred.Value;
            }
            else throw new SensorThingsValidationException($"{navigation} is required for {set}.");
            columns.Add(column);
            values.Add((NpgsqlDbType.Bigint, related));
        }
        if (set == "Observations") await ValidateObservationTypeAsync(connection, transaction, body, (long)values[columns.IndexOf("datastream_id")].Value, ct).ConfigureAwait(false);
        if (set == "HistoricalLocations") await LockThingAsync(connection, transaction, (long)values[columns.IndexOf("thing_id")].Value, ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"INSERT INTO {EntityTable(set)} ({string.Join(",", columns)}) VALUES ({string.Join(",", values.Select((_, i) => "@v" + i.ToString(CultureInfo.InvariantCulture)))}) RETURNING id", connection, transaction);
        for (var i = 0; i < values.Count; i++) command.Parameters.AddWithValue("v" + i.ToString(CultureInfo.InvariantCulture), values[i].Type, values[i].Value);
        var id = (long)(await ExecuteCatalogScalarAsync(command, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("Entity insert did not return an identifier."));
        await ApplyCollectionsAsync(connection, transaction, set, id, body, depth, ct).ConfigureAwait(false);
        if (set == "HistoricalLocations") await SynchronizeCurrentLocationAsync(connection, transaction, id, ct).ConfigureAwait(false);
        return id;
    }

    private static JsonNode? FindValue(JsonObject body, string name)
    {
        var segments = name.Split('/');
        return segments.Length == 1 ? body[name] : body[segments[0]] is JsonObject nested ? nested[segments[1]] : null;
    }

    private static bool HasMember(JsonObject body, string name)
    {
        var segments = name.Split('/');
        return segments.Length == 1 ? body.ContainsKey(name) : body[segments[0]] is JsonObject nested && nested.ContainsKey(segments[1]);
    }

    private static object ConvertValue(EntityColumn column, JsonNode value)
    {
        if (column.Type == NpgsqlDbType.Jsonb) return value.ToJsonString();
        if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text)) throw new SensorThingsValidationException($"{column.Name} must be a string.");
        if (column.Type == NpgsqlDbType.TimestampTz)
        {
            var start = text.Split('/')[0];
            if (!DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant))
                throw new SensorThingsValidationException($"{column.Name} must be an ISO 8601 time.");
            return instant;
        }
        return text;
    }

    private static void AddObservationInterval(JsonObject body, List<string> columns, List<(NpgsqlDbType Type, object Value)> values)
    {
        if (body["phenomenonTime"] is not JsonValue value || !value.TryGetValue<string>(out var text) || !text.Contains('/', StringComparison.Ordinal)) return;
        var parts = text.Split('/');
        if (parts.Length != 2 || !DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var end)
            || end < (DateTimeOffset)values[columns.IndexOf("phenomenon_time")].Value) throw new SensorThingsValidationException("phenomenonTime must be an instant or an ordered time interval.");
        columns.Add("phenomenon_time_end"); values.Add((NpgsqlDbType.TimestampTz, end));
    }

    private static IReadOnlyList<(string Navigation, string Column)> ForeignKeys(string set) => set switch
    {
        "Datastreams" => [("Thing", "thing_id"), ("Sensor", "sensor_id"), ("ObservedProperty", "observed_property_id")],
        "Observations" => [("Datastream", "datastream_id"), ("FeatureOfInterest", "feature_of_interest_id")],
        "HistoricalLocations" => [("Thing", "thing_id")],
        _ => []
    };

    private static void ValidateMembers(string set, JsonObject body)
    {
        var allowed = Columns(set).Select(c => c.Name.Split('/')[0]).Concat(SensorThingsRelationships.For(set).Keys).ToHashSet(StringComparer.Ordinal);
        if (set == "Datastreams") { allowed.Add("phenomenonTime"); allowed.Add("resultTime"); }
        foreach (var name in body.Select(p => p.Key)) if (!allowed.Contains(name)) throw new SensorThingsValidationException($"Unknown or read-only property '{name}' on {set}.");
        if (body["properties"] is { } properties && properties is not JsonObject) throw new SensorThingsValidationException("properties must be a JSON object.");
        if (body["parameters"] is { } parameters && parameters is not JsonObject) throw new SensorThingsValidationException("parameters must be a JSON object.");
        if (set == "Datastreams" && body.TryGetPropertyValue("unitOfMeasurement", out var unit) && unit is not JsonObject) throw new SensorThingsValidationException("unitOfMeasurement must be an object.");
        if (body["encodingType"] is JsonValue encoding && encoding.TryGetValue<string>(out var encodingType) && string.IsNullOrWhiteSpace(encodingType))
            throw new SensorThingsValidationException("encodingType cannot be empty.");
        if (set == "Datastreams" && body["observationType"] is JsonValue observationType)
        {
            if (!observationType.TryGetValue<string>(out var type) || !SupportedObservationTypes.Contains(type))
                throw new SensorThingsValidationException("Unsupported observationType.");
        }
        if (set is "Locations" or "FeaturesOfInterest" && body["encodingType"] is JsonValue format && format.TryGetValue<string>(out var mime)
            && IsGeoJsonEncoding(mime) && body.TryGetPropertyValue(set == "Locations" ? "location" : "feature", out var geometry))
            ValidateGeoJson(geometry);
        if (set == "Datastreams" && body["observedArea"] is { } area)
        {
            ValidateGeoJson(area);
            if (area["type"]?.GetValue<string>() != "Polygon") throw new SensorThingsValidationException("observedArea must be a GeoJSON Polygon.");
        }
        if (set == "Sensors" && body["encodingType"] is JsonValue sensorFormat && sensorFormat.TryGetValue<string>(out var sensorMime)
            && sensorMime is "application/pdf" or "text/html" or "http://www.opengis.net/doc/IS/SensorML/2.0"
            && body.TryGetPropertyValue("metadata", out var metadata) && (metadata is not JsonValue scalar || !scalar.TryGetValue<string>(out _)))
            throw new SensorThingsValidationException("This Sensor encodingType requires string metadata.");
        if (body["validTime"] is { } validTime)
        {
            if (validTime is not JsonValue intervalValue || !intervalValue.TryGetValue<string>(out var interval) || interval.Split('/') is not [var start, var end]
                || !DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var from)
                || !DateTimeOffset.TryParse(end, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var to) || to < from)
                throw new SensorThingsValidationException("validTime must be an ordered ISO 8601 interval.");
        }
        if (set == "Datastreams")
            foreach (var name in new[] { "phenomenonTime", "resultTime" })
                if (body[name] is { } supplied && (supplied is not JsonValue value || !value.TryGetValue<string>(out var interval)
                    || interval.Split('/') is not [var start, var end] || !DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var from)
                    || !DateTimeOffset.TryParse(end, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var to) || to < from))
                    throw new SensorThingsValidationException($"{name} must be an ordered ISO 8601 interval.");
    }

    private static readonly HashSet<string> SupportedObservationTypes =
    [
        "http://www.opengis.net/def/observationType/OGC-OM/2.0/OM_Measurement",
        "http://www.opengis.net/def/observationType/OGC-OM/2.0/OM_CountObservation",
        "http://www.opengis.net/def/observationType/OGC-OM/2.0/OM_TruthObservation",
        "http://www.opengis.net/def/observationType/OGC-OM/2.0/OM_CategoryObservation",
        "http://www.opengis.net/def/observationType/OGC-OM/2.0/OM_Observation",
    ];

    private static bool IsGeoJsonEncoding(string mime) => mime.Equals("application/geo+json", StringComparison.OrdinalIgnoreCase)
        || mime.Equals("application/vnd.geo+json", StringComparison.OrdinalIgnoreCase);

    private static void ValidateGeoJson(JsonNode? value)
    {
        if (value is not JsonObject body || body["type"] is not JsonValue type || !type.TryGetValue<string>(out var kind))
            throw new SensorThingsValidationException("GeoJSON must be an object with a type.");
        if (kind == "Feature")
        {
            if (!body.TryGetPropertyValue("geometry", out var geometry)) throw new SensorThingsValidationException("GeoJSON Feature requires geometry, which may be null.");
            if (geometry is not null) ValidateGeoJson(geometry);
            if (body["properties"] is { } properties && properties is not JsonObject) throw new SensorThingsValidationException("Feature properties must be an object or null.");
            return;
        }
        if (kind == "FeatureCollection")
        {
            if (body["features"] is not JsonArray features) throw new SensorThingsValidationException("FeatureCollection requires a features array.");
            foreach (var feature in features)
            {
                if (feature is not JsonObject item || item["type"]?.ToString() != "Feature") throw new SensorThingsValidationException("FeatureCollection members must be Features.");
                ValidateGeoJson(feature);
            }
            return;
        }
        using var document = JsonDocument.Parse(body.ToJsonString());
        if (!GeoJsonGeometryShapeValidator.IsKnownValidGeometry(document.RootElement)) throw new SensorThingsValidationException("GeoJSON geometry has invalid coordinates or structure.");
    }

    private async Task<long?> InferFeatureAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long datastream, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"SELECT l.id, l.name, l.description, l.encoding_type, l.location::text FROM {EntityTable("Locations")} l JOIN {AssociationTable("sta_thing_location")} tl ON tl.location_id=l.id JOIN {_datastreamTable} ds ON ds.thing_id=tl.thing_id WHERE ds.id=@ds AND lower(l.encoding_type) IN ('application/vnd.geo+json','application/geo+json') AND NOT ST_IsEmpty({GeoJsonGeometrySql("l.location", "l.encoding_type")}) ORDER BY l.id LIMIT 2 FOR UPDATE OF l", connection, transaction);
        command.Parameters.AddWithValue("ds", datastream);
        JsonObject? body = null;
        long location = 0;
        await using (var reader = await ExecuteCatalogReaderAsync(command, ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                location = reader.GetInt64(0);
                body = new JsonObject { ["name"] = reader.GetString(1), ["description"] = reader.GetString(2), ["encodingType"] = reader.GetString(3), ["feature"] = JsonNode.Parse(reader.GetString(4)) };
                if (await reader.ReadAsync(ct).ConfigureAwait(false)) throw new SensorThingsValidationException("Multiple Thing locations require an explicit FeatureOfInterest.");
            }
        }
        if (body is null) return null;
        await using (var existing = new NpgsqlCommand($"SELECT id FROM {EntityTable("FeaturesOfInterest")} WHERE source_location_id=@location AND feature=@feature::jsonb AND encoding_type=@encoding ORDER BY id LIMIT 1", connection, transaction))
        {
            existing.Parameters.AddWithValue("location", location); existing.Parameters.AddWithValue("feature", body["feature"]!.ToJsonString()); existing.Parameters.AddWithValue("encoding", body["encodingType"]!.GetValue<string>());
            if (await ExecuteCatalogScalarAsync(existing, ct).ConfigureAwait(false) is long prior) return prior;
        }
        var feature = await InsertEntityAsync(connection, transaction, "FeaturesOfInterest", body, 0, ct).ConfigureAwait(false);
        await using var source = new NpgsqlCommand($"UPDATE {EntityTable("FeaturesOfInterest")} SET source_location_id=@location WHERE id=@id", connection, transaction);
        source.Parameters.AddWithValue("location", location); source.Parameters.AddWithValue("id", feature);
        await ExecuteCatalogNonQueryAsync(source, ct).ConfigureAwait(false);
        return feature;
    }

    private async Task ValidateObservationTypeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, JsonObject body, long datastream, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"SELECT observation_type FROM {_datastreamTable} WHERE id=@id FOR SHARE", connection, transaction);
        command.Parameters.AddWithValue("id", datastream);
        var type = (string?)await ExecuteCatalogScalarAsync(command, ct).ConfigureAwait(false);
        ValidateObservationResult(body, type);
    }

    private static void ValidateObservationResult(JsonObject body, string? type)
    {
        var result = body["result"];
        var suffix = type?.Split('/').Last();
        var valid = suffix switch
        {
            "OM_Measurement" => result is JsonValue number && number.TryGetValue<double>(out var numeric) && double.IsFinite(numeric),
            "OM_CountObservation" => result is JsonValue count && count.TryGetValue<long>(out _),
            "OM_TruthObservation" => result is JsonValue truth && truth.TryGetValue<bool>(out _),
            "OM_CategoryObservation" => result is JsonValue category && category.TryGetValue<string>(out _),
            "OM_Observation" => body.ContainsKey("result"),
            _ => false
        };
        if (!valid) throw new SensorThingsValidationException("Observation result does not match the Datastream observationType.");
    }

    private async Task ApplyCollectionsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string set, long id, JsonObject body, int depth, CancellationToken ct)
    {
        foreach (var relation in SensorThingsRelationships.For(set).Where(r => r.Value.Many))
        {
            if (!body.TryGetPropertyValue(relation.Key, out var value)) continue;
            if (value is not JsonArray array) throw new SensorThingsValidationException($"{relation.Key} must be an array.");
            if (array.Count > 1000) throw new SensorThingsValidationException("Deep insert collection exceeds 1000 entities.");
            var isAssociation = (set, relation.Key) is ("Things", "Locations") or ("Locations", "Things") or ("HistoricalLocations", "Locations") or ("Locations", "HistoricalLocations");
            if (isAssociation)
            {
                var history = set == "HistoricalLocations" || relation.Key == "HistoricalLocations";
                var table = AssociationTable(history ? "sta_historical_location_location" : "sta_thing_location");
                var ownColumn = set == "Locations" ? "location_id" : history ? "historical_location_id" : "thing_id";
                var otherColumn = set == "Locations" ? history ? "historical_location_id" : "thing_id" : "location_id";
                var changed = false;
                if (!history && set == "Things") await LockThingAsync(connection, transaction, id, ct).ConfigureAwait(false);
                foreach (var item in array)
                {
                    var related = await ResolveEntityAsync(connection, transaction, relation.Value.Target, item, depth, ct).ConfigureAwait(false);
                    if (!history && set == "Locations") await LockThingAsync(connection, transaction, related, ct).ConfigureAwait(false);
                    await using var link = new NpgsqlCommand($"INSERT INTO {table} ({ownColumn},{otherColumn}) VALUES (@id,@related) ON CONFLICT DO NOTHING", connection, transaction);
                    link.Parameters.AddWithValue("id", id); link.Parameters.AddWithValue("related", related);
                    var added = await ExecuteCatalogNonQueryAsync(link, ct).ConfigureAwait(false) > 0;
                    changed |= added;
                    if (!history && set == "Locations")
                    {
                        await ValidateCurrentLocationEncodingsAsync(connection, transaction, related, ct).ConfigureAwait(false);
                        if (added) await RecordHistoryAsync(connection, transaction, related, ct).ConfigureAwait(false);
                    }
                    if (history && set == "Locations" && added) await SynchronizeCurrentLocationAsync(connection, transaction, related, ct).ConfigureAwait(false);
                }
                if (!history && set == "Things")
                {
                    await ValidateCurrentLocationEncodingsAsync(connection, transaction, id, ct).ConfigureAwait(false);
                    if (changed) await RecordHistoryAsync(connection, transaction, id, ct).ConfigureAwait(false);
                }
                continue;
            }
            var inverse = (set, relation.Key) switch { ("Things", "Datastreams") => "Thing", ("Sensors", "Datastreams") => "Sensor", ("ObservedProperties", "Datastreams") => "ObservedProperty", ("Things", "HistoricalLocations") => "Thing", ("Datastreams", "Observations") => "Datastream", ("FeaturesOfInterest", "Observations") => "FeatureOfInterest", _ => throw new SensorThingsValidationException("Unknown collection relationship.") };
            foreach (var item in array)
            {
                if (item is not JsonObject child) throw new SensorThingsValidationException("Related entities must be objects.");
                if (child.Count == 1 && child["@iot.id"] is not null)
                {
                    var related = await ResolveEntityAsync(connection, transaction, relation.Value.Target, child, depth, ct).ConfigureAwait(false);
                    var column = ForeignKeys(relation.Value.Target).Single(f => f.Navigation == inverse).Column;
                    if (relation.Value.Target == "Observations" && inverse == "Datastream")
                    {
                        await using var resultCommand = new NpgsqlCommand($"SELECT result_json::text FROM {_observationTable} WHERE id=@related FOR UPDATE", connection, transaction);
                        resultCommand.Parameters.AddWithValue("related", related);
                        var result = (string?)await ExecuteCatalogScalarAsync(resultCommand, ct).ConfigureAwait(false);
                        await ValidateObservationTypeAsync(connection, transaction, new JsonObject { ["result"] = JsonNode.Parse(result ?? "null") }, id, ct).ConfigureAwait(false);
                    }
                    var referenceAssignment = relation.Value.Target == "Observations"
                        ? "," + (inverse == "Datastream" ? "datastream_reference_id" : "feature_of_interest_reference_id") + "=@id"
                        : string.Empty;
                    await using var update = new NpgsqlCommand($"UPDATE {EntityTable(relation.Value.Target)} SET {column}=@id{referenceAssignment} WHERE id=@related", connection, transaction);
                    update.Parameters.AddWithValue("id", id); update.Parameters.AddWithValue("related", related);
                    await ExecuteCatalogNonQueryAsync(update, ct).ConfigureAwait(false);
                }
                else
                {
                    var nested = child.DeepClone().AsObject(); nested[inverse] = new JsonObject { ["@iot.id"] = id };
                    await InsertEntityAsync(connection, transaction, relation.Value.Target, nested, depth + 1, ct).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task RecordHistoryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long thing, CancellationToken ct)
    {
        await using (var locations = new NpgsqlCommand($"SELECT EXISTS(SELECT 1 FROM {AssociationTable("sta_thing_location")} WHERE thing_id=@thing)", connection, transaction))
        {
            locations.Parameters.AddWithValue("thing", thing);
            if (await ExecuteCatalogScalarAsync(locations, ct).ConfigureAwait(false) is not true) return;
        }
        await using var command = new NpgsqlCommand($"INSERT INTO {EntityTable("HistoricalLocations")} (thing_id,time) VALUES (@thing,clock_timestamp()) RETURNING id", connection, transaction);
        command.Parameters.AddWithValue("thing", thing);
        var id = (long)(await ExecuteCatalogScalarAsync(command, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("History insert did not return an identifier."));
        await using var links = new NpgsqlCommand($"INSERT INTO {AssociationTable("sta_historical_location_location")} (historical_location_id,location_id) SELECT @history,location_id FROM {AssociationTable("sta_thing_location")} WHERE thing_id=@thing", connection, transaction);
        links.Parameters.AddWithValue("history", id); links.Parameters.AddWithValue("thing", thing);
        await ExecuteCatalogNonQueryAsync(links, ct).ConfigureAwait(false);
    }

    private async Task LockThingAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long thing, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"SELECT id FROM {_thingTable} WHERE id=@thing FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("thing", thing);
        await ExecuteCatalogScalarAsync(command, ct).ConfigureAwait(false);
    }

    private async Task ValidateCurrentLocationEncodingsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long thing, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {AssociationTable("sta_thing_location")} tl JOIN {EntityTable("Locations")} l ON l.id=tl.location_id WHERE tl.thing_id=@thing GROUP BY lower(l.encoding_type) HAVING count(*)>1)", connection, transaction);
        command.Parameters.AddWithValue("thing", thing);
        if (await ExecuteCatalogScalarAsync(command, ct).ConfigureAwait(false) is true)
            throw new SensorThingsValidationException("A Thing's current Locations must have distinct encodingTypes.");
    }

    private async Task SynchronizeCurrentLocationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long history, CancellationToken ct)
    {
        await using var current = new NpgsqlCommand($"SELECT h.thing_id FROM {EntityTable("HistoricalLocations")} h WHERE h.id=@history AND NOT EXISTS (SELECT 1 FROM {EntityTable("HistoricalLocations")} newer WHERE newer.thing_id=h.thing_id AND newer.id<>h.id AND newer.time>=h.time)", connection, transaction);
        current.Parameters.AddWithValue("history", history);
        if (await ExecuteCatalogScalarAsync(current, ct).ConfigureAwait(false) is not long thing) return;
        await LockThingAsync(connection, transaction, thing, ct).ConfigureAwait(false);
        if (await ExecuteCatalogScalarAsync(current, ct).ConfigureAwait(false) is not long) return;
        await using var replace = new NpgsqlCommand($"DELETE FROM {AssociationTable("sta_thing_location")} WHERE thing_id=@thing; INSERT INTO {AssociationTable("sta_thing_location")} (thing_id,location_id) SELECT @thing,location_id FROM {AssociationTable("sta_historical_location_location")} WHERE historical_location_id=@history", connection, transaction);
        replace.Parameters.AddWithValue("thing", thing); replace.Parameters.AddWithValue("history", history);
        await ExecuteCatalogNonQueryAsync(replace, ct).ConfigureAwait(false);
        await ValidateCurrentLocationEncodingsAsync(connection, transaction, thing, ct).ConfigureAwait(false);
    }

    public async Task<bool> PatchEntityAsync(string entitySet, long id, JsonElement body, CancellationToken cancellationToken)
    {
        await VerifySchemaFloorAsync(cancellationToken).ConfigureAwait(false);
        var patch = ParseBody(body); patch.Remove("@iot.id"); ValidateMembers(entitySet, patch);
        foreach (var relation in SensorThingsRelationships.For(entitySet))
        {
            if (!patch.TryGetPropertyValue(relation.Key, out var reference)) continue;
            var references = relation.Value.Many ? reference is JsonArray collection ? collection.ToArray() : throw new SensorThingsValidationException("Collection navigation binding must be an array.") : new[] { reference };
            foreach (var item in references) if (item is not JsonObject related || related.Count != 1 || !related.ContainsKey("@iot.id"))
                throw new SensorThingsValidationException("PATCH navigation properties must contain existing @iot.id references; inline entities are not allowed.");
        }
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await lease.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockCatalogRelationshipsAsync(lease.Connection, transaction, cancellationToken).ConfigureAwait(false);
        if (!await ExistsAsync(lease.Connection, transaction, EntityTable(entitySet), id, cancellationToken).ConfigureAwait(false)) return false;
        await using (var current = new NpgsqlCommand($"SELECT to_jsonb(d)::text FROM {EntityTable(entitySet)} d WHERE id=@id FOR UPDATE", lease.Connection, transaction))
        {
            current.Parameters.AddWithValue("id", id);
            var json = (string?)await ExecuteCatalogScalarAsync(current, cancellationToken).ConfigureAwait(false);
            var existing = json is null ? null : JsonNode.Parse(NormalizeEntity(entitySet, json).GetRawText())?.AsObject();
            foreach (var column in Columns(entitySet).Where(column => column.Type == NpgsqlDbType.Jsonb))
            {
                if (patch[column.Name] is JsonObject incoming && existing?[column.Name] is JsonObject previous)
                {
                    var merged = (JsonObject)previous.DeepClone();
                    MergeComplexMembers(merged, incoming);
                    patch[column.Name] = merged;
                }
            }
            if (existing is not null)
            {
                existing.Remove("@iot.id");
                MergeComplexMembers(existing, patch);
                ValidateMembers(entitySet, existing);
            }
        }
        var assignments = new List<string>();
        await using var command = new NpgsqlCommand { Connection = lease.Connection, Transaction = transaction };
        foreach (var column in Columns(entitySet))
        {
            if (!patch.ContainsKey(column.Name.Split('/')[0])) continue;
            if (column.Name.Contains('/', StringComparison.Ordinal) && !HasMember(patch, column.Name)) continue;
            var value = FindValue(patch, column.Name);
            if (value is null && column.Required && !column.AllowNull && !(entitySet == "Observations" && column.Name == "result")) throw new SensorThingsValidationException($"{column.Name} cannot be null.");
            var parameter = "v" + assignments.Count.ToString(CultureInfo.InvariantCulture);
            assignments.Add(column.Column + "=@" + parameter);
            command.Parameters.AddWithValue(parameter, column.Type, value is null ? entitySet == "Observations" && column.Name == "result" ? "null" : DBNull.Value : ConvertValue(column, value));
        }
        if (entitySet == "Observations" && patch.TryGetPropertyValue("phenomenonTime", out var phenomenonTime))
        {
            var text = phenomenonTime!.GetValue<string>();
            object end = DBNull.Value;
            if (text.Contains('/', StringComparison.Ordinal))
            {
                var parts = text.Split('/');
                if (parts.Length != 2 || !DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedEnd)
                    || parsedEnd < (DateTimeOffset)ConvertValue(new("phenomenonTime", "phenomenon_time", NpgsqlDbType.TimestampTz), phenomenonTime))
                    throw new SensorThingsValidationException("phenomenonTime must be an ordered time interval.");
                end = parsedEnd;
            }
            assignments.Add("phenomenon_time_end=@end"); command.Parameters.AddWithValue("end", NpgsqlDbType.TimestampTz, end);
        }
        if (entitySet == "Observations" && patch.TryGetPropertyValue("result", out var result))
        {
            object numericResult = result is JsonValue numeric && numeric.TryGetValue<double>(out var number) ? number : DBNull.Value;
            assignments.Add("result=@numericResult"); command.Parameters.AddWithValue("numericResult", NpgsqlDbType.Double, numericResult);
        }
        foreach (var (navigation, column) in ForeignKeys(entitySet))
        {
            if (!patch.TryGetPropertyValue(navigation, out var navigationValue)) continue;
            var related = await ResolveEntityAsync(lease.Connection, transaction, SensorThingsRelationships.For(entitySet)[navigation].Target, navigationValue, 0, cancellationToken).ConfigureAwait(false);
            var parameter = "v" + assignments.Count.ToString(CultureInfo.InvariantCulture);
            assignments.Add(column + "=@" + parameter); command.Parameters.AddWithValue(parameter, related);
            if (entitySet == "Observations") assignments.Add((navigation == "Datastream" ? "datastream_reference_id" : "feature_of_interest_reference_id") + "=@" + parameter);
        }
        if (assignments.Count > 0)
        {
            command.CommandText = $"UPDATE {EntityTable(entitySet)} SET {string.Join(",", assignments)} WHERE id=@id";
            command.Parameters.AddWithValue("id", id); await ExecuteCatalogNonQueryAsync(command, cancellationToken).ConfigureAwait(false);
        }
        if (entitySet == "Observations")
        {
            await using var check = new NpgsqlCommand($"SELECT result_json::text,datastream_id FROM {_observationTable} WHERE id=@id", lease.Connection, transaction);
            check.Parameters.AddWithValue("id", id);
            JsonObject? observation = null; long datastream = 0;
            await using (var reader = await ExecuteCatalogReaderAsync(check, cancellationToken).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { observation = new JsonObject { ["result"] = JsonNode.Parse(reader.GetString(0)) }; datastream = reader.GetInt64(1); }
            }
            if (observation is not null) await ValidateObservationTypeAsync(lease.Connection, transaction, observation, datastream, cancellationToken).ConfigureAwait(false);
        }
        if (entitySet == "Datastreams" && patch.TryGetPropertyValue("observationType", out var observationType))
        {
            await using var results = new NpgsqlCommand($"SELECT result_json::text FROM {_observationTable} WHERE datastream_reference_id=@id", lease.Connection, transaction);
            results.Parameters.AddWithValue("id", id);
            await using var reader = await ExecuteCatalogReaderAsync(results, cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                ValidateObservationResult(new JsonObject { ["result"] = JsonNode.Parse(reader.GetString(0)) }, observationType!.GetValue<string>());
        }
        await ApplyCollectionsAsync(lease.Connection, transaction, entitySet, id, patch, 0, cancellationToken).ConfigureAwait(false);
        if (entitySet == "Locations")
        {
            var things = new List<long>();
            await using (var linked = new NpgsqlCommand($"SELECT t.id FROM {_thingTable} t JOIN {AssociationTable("sta_thing_location")} tl ON tl.thing_id=t.id WHERE tl.location_id=@location ORDER BY t.id FOR UPDATE OF t", lease.Connection, transaction))
            {
                linked.Parameters.AddWithValue("location", id);
                await using var reader = await ExecuteCatalogReaderAsync(linked, cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) things.Add(reader.GetInt64(0));
            }
            foreach (var thing in things) await ValidateCurrentLocationEncodingsAsync(lease.Connection, transaction, thing, cancellationToken).ConfigureAwait(false);
        }
        if (entitySet == "HistoricalLocations") await SynchronizeCurrentLocationAsync(lease.Connection, transaction, id, cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static void MergeComplexMembers(JsonObject target, JsonObject patch)
    {
        foreach (var (name, value) in patch)
        {
            if (value is JsonObject incoming && target[name] is JsonObject previous) MergeComplexMembers(previous, incoming);
            else target[name] = value?.DeepClone();
        }
    }

    public async Task<bool> DeleteEntityAsync(string entitySet, long id, CancellationToken cancellationToken)
    {
        await VerifySchemaFloorAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await lease.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (entitySet != "Observations") await LockCatalogRelationshipsAsync(lease.Connection, transaction, cancellationToken).ConfigureAwait(false);
        if (entitySet == "Locations")
        {
            await using var history = new NpgsqlCommand($"DELETE FROM {EntityTable("HistoricalLocations")} WHERE id IN (SELECT historical_location_id FROM {AssociationTable("sta_historical_location_location")} WHERE location_id=@id)", lease.Connection, transaction);
            history.Parameters.AddWithValue("id", id); await ExecuteCatalogNonQueryAsync(history, cancellationToken).ConfigureAwait(false);
        }
        await using var command = new NpgsqlCommand($"DELETE FROM {EntityTable(entitySet)} WHERE id=@id", lease.Connection, transaction);
        command.Parameters.AddWithValue("id", id);
        var deleted = await ExecuteCatalogNonQueryAsync(command, cancellationToken).ConfigureAwait(false) > 0;
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonObject))]
internal sealed partial class EntityJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
