// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.SensorThings;

/// <summary>
/// Resolves the immutable physical sensing family left behind when migration 109 adopts
/// the earlier SensorThings family. Catalog inspection never creates, moves or repairs objects.
/// </summary>
internal static class PostgresSensorThingsSchema
{
    internal const string DeferredSchemaToken = "__honua_sensing_physical__";
    internal const string SensingMigration = "Honua.Server.Migrations.127_CompleteSensorThingsSensing.sql";
    internal static readonly string[] SensingObjects =
    [
        "sta_location", "sta_historical_location", "sta_feature_of_interest", "sta_thing_location",
        "sta_historical_location_location", "sta_location_id_seq", "sta_historical_location_id_seq",
        "sta_feature_of_interest_id_seq"
    ];
    private static readonly string[] _functions = ["sta_synchronize_result", "sta_synchronize_references"];
    private static readonly string[] _baseObjects =
    [
        "sta_thing", "sta_sensor", "sta_observed_property", "sta_datastream", "sta_observation", "sta_observation_default",
        "sta_thing_id_seq", "sta_sensor_id_seq", "sta_observed_property_id_seq", "sta_datastream_id_seq", "sta_observation_id_seq"
    ];
    private static readonly (string Table, string Column, string Target, string TargetColumn)[] _foreignKeys =
    [
        ("sta_historical_location", "thing_id", "sta_thing", "id"),
        ("sta_feature_of_interest", "source_location_id", "sta_location", "id"),
        ("sta_thing_location", "thing_id", "sta_thing", "id"),
        ("sta_thing_location", "location_id", "sta_location", "id"),
        ("sta_historical_location_location", "historical_location_id", "sta_historical_location", "id"),
        ("sta_historical_location_location", "location_id", "sta_location", "id"),
        ("sta_observation", "feature_of_interest_reference_id", "sta_feature_of_interest", "id"),
        ("sta_observation", "datastream_reference_id", "sta_datastream", "id")
    ];

    internal static async Task<string> ResolveAsync(DbConnection connection, string? configuredSchema,
        bool sensingApplied, DbTransaction? transaction = null, CancellationToken cancellationToken = default, bool requireConfiguredBase = false)
    {
        var configured = string.IsNullOrWhiteSpace(configuredSchema) ? "honua" : configuredSchema;
        _ = SchemaSearchPath.ValidateAndQuote(configured);
        if (configured == DeferredSchemaToken) throw Failure("The configured schema collides with the reserved sensing query identifier.");
        var objects = new Dictionary<(string Schema, string Name), (uint Oid, char Kind)>();
        var functions = new Dictionary<(string Schema, string Name), uint>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT n.nspname, c.relname, c.oid, c.relkind::text
                FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname=ANY(@schemas) AND c.relname=ANY(@objects)
                """;
            command.Parameters.Add(new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = new[] { configured, "honua" } });
            command.Parameters.Add(new NpgsqlParameter("objects", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = _baseObjects.Concat(SensingObjects).ToArray() });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                objects.Add((reader.GetString(0), reader.GetString(1)), (reader.GetFieldValue<uint>(2), reader.GetString(3)[0]));
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT n.nspname,p.proname,p.oid,p.pronargs,p.prorettype='pg_catalog.trigger'::regtype,p.prokind::text
                FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                WHERE n.nspname=ANY(@schemas) AND p.proname=ANY(@functions)
                """;
            command.Parameters.Add(new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = new[] { configured, "honua" } });
            command.Parameters.Add(new NpgsqlParameter("functions", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = _functions });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetInt16(3) != 0 || !reader.GetBoolean(4) || reader.GetString(5) != "f" ||
                    !functions.TryAdd((reader.GetString(0), reader.GetString(1)), reader.GetFieldValue<uint>(2)))
                    throw Failure("Sensing trigger function identity is ambiguous or has an incompatible signature.");
            }
        }

        bool Any(string schema) => SensingObjects.Any(name => objects.ContainsKey((schema, name))) || _functions.Any(name => functions.ContainsKey((schema, name)));
        bool Complete(string schema) => SensingObjects.All(name => objects.TryGetValue((schema, name), out var value) &&
            (name.EndsWith("_seq", StringComparison.Ordinal) ? value.Kind == 'S' : value.Kind == 'r')) &&
            _functions.All(name => functions.ContainsKey((schema, name)));
        if (!sensingApplied)
        {
            if (Any(configured) || Any("honua"))
                throw new DatabaseSchemaFloorException(SensingMigration, DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal,
                    "Sensing objects exist without their numbered migration receipt.");
            return configured;
        }

        string physical;
        if (configured == "honua")
        {
            if (!Complete(configured)) throw Failure("The canonical sensing family is incomplete or has incompatible object kinds.");
            physical = configured;
        }
        else if (Complete(configured) && !Any("honua")) physical = configured;
        else if (Complete("honua") && !Any(configured)) physical = "honua";
        else throw Failure("The configured and legacy sensing families are partial, duplicate or ambiguous.");

        bool BaseComplete(string schema) => _baseObjects.All(name => objects.TryGetValue((schema, name), out var value) &&
            (name.EndsWith("_seq", StringComparison.Ordinal) ? value.Kind == 'S' : name == "sta_observation" ? value.Kind == 'p' : value.Kind == 'r'));
        var baseSchema = configured;
        if (configured != "honua")
        {
            var configuredAny = _baseObjects.Any(name => objects.ContainsKey((configured, name)));
            var legacyAny = _baseObjects.Any(name => objects.ContainsKey(("honua", name)));
            if (BaseComplete(configured) && !legacyAny) baseSchema = configured;
            else if (BaseComplete("honua") && !configuredAny && physical == "honua") baseSchema = "honua";
            else throw Failure("The original SensorThings family is partial, duplicate or incompatible with sensing authority.");
        }
        else if (!BaseComplete(baseSchema)) throw Failure("The original SensorThings family is incomplete.");

        if (requireConfiguredBase && baseSchema != configured)
            throw Failure("The configured runtime base family has not been adopted; legacy base authority is permitted only during migration planning.");

        uint Oid(string table) => objects[(_baseObjects.Contains(table, StringComparer.Ordinal) ? baseSchema : physical, table)].Oid;
        foreach (var (table, column, target, targetColumn) in _foreignKeys)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_constraint c
                JOIN pg_catalog.pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=c.conkey[1]
                JOIN pg_catalog.pg_attribute b ON b.attrelid=c.confrelid AND b.attnum=c.confkey[1]
                WHERE c.contype='f' AND c.convalidated AND cardinality(c.conkey)=1 AND cardinality(c.confkey)=1
                  AND c.conrelid=@source AND c.confrelid=@target AND a.attname=@column AND b.attname=@target_column)
                """;
            command.Parameters.Add(new NpgsqlParameter("source", NpgsqlDbType.Oid) { Value = Oid(table) });
            command.Parameters.Add(new NpgsqlParameter("target", NpgsqlDbType.Oid) { Value = Oid(target) });
            command.Parameters.Add(new NpgsqlParameter("column", column));
            command.Parameters.Add(new NpgsqlParameter("target_column", targetColumn));
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw Failure($"Sensing foreign-key authority is incompatible for {table}.{column}.");
        }
        foreach (var table in new[] { "sta_location", "sta_historical_location", "sta_feature_of_interest" })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_attrdef d
                JOIN pg_catalog.pg_attribute a ON a.attrelid=d.adrelid AND a.attnum=d.adnum
                JOIN pg_catalog.pg_depend dep ON dep.classid='pg_catalog.pg_attrdef'::regclass AND dep.objid=d.oid
                WHERE d.adrelid=@table AND a.attname='id' AND dep.refclassid='pg_catalog.pg_class'::regclass
                  AND dep.refobjid=@sequence)
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_depend owner
                WHERE owner.classid='pg_catalog.pg_class'::regclass AND owner.objid=@sequence
                  AND owner.deptype IN ('a','i')
                  AND (owner.refobjid<>@table OR owner.refobjsubid<>(SELECT attnum FROM pg_catalog.pg_attribute
                       WHERE attrelid=@table AND attname='id')))
                """;
            command.Parameters.Add(new NpgsqlParameter("table", NpgsqlDbType.Oid) { Value = Oid(table) });
            command.Parameters.Add(new NpgsqlParameter("sequence", NpgsqlDbType.Oid) { Value = objects[(physical, table + "_id_seq")].Oid });
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw Failure("Sensing identifier default does not bind the selected physical sequence authority.");
        }
        foreach (var (trigger, function) in new[] { ("sta_observation_result_sync", _functions[0]), ("sta_observation_reference_sync", _functions[1]) })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_trigger
                WHERE tgrelid=@table AND tgname=@trigger AND tgfoid=@function AND NOT tgisinternal AND tgenabled IN ('O','A'))
                """;
            command.Parameters.Add(new NpgsqlParameter("table", NpgsqlDbType.Oid) { Value = Oid("sta_observation") });
            command.Parameters.Add(new NpgsqlParameter("trigger", trigger));
            command.Parameters.Add(new NpgsqlParameter("function", NpgsqlDbType.Oid) { Value = functions[(physical, function)] });
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw Failure("Sensing observation trigger does not bind the selected physical function authority.");
        }
        return physical;
    }

    private static DatabaseSchemaFloorException Failure(string detail) =>
        new(SensingMigration, DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema, detail);
}
