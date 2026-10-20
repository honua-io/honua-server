// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.SensorThings.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Db.Postgres.Features.SensorThings;
using Honua.Server.Startup;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Honua.CloudIntegration.Tests;

public sealed partial class CoreSchemaDivergenceGuardTests
{
    private static async Task AssertSensingCatalogBatchesAsync(string connectionString, string configuredSchema, long locationId)
    {
        var releases = 0;
        var connection = new SemaphoreReleasingConnection(new NpgsqlConnection(connectionString),
            () => Interlocked.Increment(ref releases));
        var commands = new ConcurrentQueue<string>();
        try
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            string physical;
            {
                using var trace = new Activity("sensing-authority-catalog-proof").SetIdFormat(ActivityIdFormat.W3C).Start();
                var traceId = trace.TraceId;
                using var listener = new ActivityListener
                {
                    ShouldListenTo = source => source.Name == "Npgsql",
                    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                    ActivityStopped = activity =>
                    {
                        if (activity.TraceId == traceId && activity.GetTagItem("db.query.text") is string text)
                            commands.Enqueue(text);
                    },
                };
                ActivitySource.AddActivityListener(listener);
                physical = await PostgresSensorThingsSchema.ResolveAsync(connection, configuredSchema,
                    sensingApplied: true, transaction: transaction, requireConfiguredBase: true);
            }
            physical.Should().Be("honua", "the literal 109 fixture preserves the complete legacy 127 family");
            releases.Should().Be(0, "the caller still owns the wrapped connection and transaction");
            connection.State.Should().Be(System.Data.ConnectionState.Open);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT name FROM honua.sta_location WHERE id=@id";
            command.Parameters.Add(new NpgsqlParameter("id", locationId));
            var originalName = await command.ExecuteScalarAsync();
            originalName.Should().Be("preserved");
            command.CommandText = "UPDATE honua.sta_location SET name='catalog-batch-rollback' WHERE id=@id RETURNING name";
            (await command.ExecuteScalarAsync()).Should().Be("catalog-batch-rollback");
            await transaction.RollbackAsync();
            command.Transaction = null;
            command.CommandText = "SELECT name FROM honua.sta_location WHERE id=@id";
            (await command.ExecuteScalarAsync()).Should().Be(originalName, "the same wrapped transaction must roll back the actual row update");
            releases.Should().Be(0);
        }
        finally
        {
            await connection.DisposeAsync();
        }
        releases.Should().Be(1, "only the caller's disposal releases the actual semaphore-owned connection");
        await connection.DisposeAsync();
        releases.Should().Be(1, "double disposal must not release a second slot");
        commands.Should().HaveCount(2, "live catalog authority validation must use two protocol batches, not fifteen sequential commands");
        var observed = commands.ToArray();
        observed[0].Should().Contain("pg_catalog.pg_class").And.Contain("pg_catalog.pg_proc");
        observed[1].Should().Contain("pg_catalog.pg_constraint").And.Contain("pg_catalog.pg_attrdef").And.Contain("pg_catalog.pg_trigger");
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SensingAuthority_RealDefaultAndLiteral109Upgrade_PreservesObjectsAndRuntimeSemantics(bool configured)
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is required for physical sensing authority proof.");
        var connectionString = await CreateSensingBaselineAsync();
        var defaultGuard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest);
        var defaultStore = new PostgresObservationStore(new TestConnectionProvider(connectionString), defaultGuard, "honua");
        var original = await defaultStore.CreateEntityAsync("Locations", SensingJson("""
            {"name":"preserved","description":"legacy","encodingType":"application/geo+json",
             "location":{"type":"Point","coordinates":[1,2]}}
            """), CancellationToken.None);
        var before = await CaptureSensingFamilyAsync(connectionString);
        var schema = configured ? "sensing_configured" : "honua";
        if (configured)
        {
            // Execute the actual immutable 109 resource as fixture setup. This proves its
            // physical result, not canonical configured full boot (which also requires #6418).
            await ApplySensingAdoptionFixtureAsync(connectionString, schema);
        }
        (await CaptureSensingFamilyAsync(connectionString)).Should().Be(before,
            "109 must leave every 127 sensing object OID, row and sequence value intact");
        await AssertSensingCatalogBatchesAsync(connectionString, schema, original);
        var guard = CreateSensingGuard(schema);
        var store = new PostgresObservationStore(new TestConnectionProvider(connectionString), guard, schema);
        (await store.GetEntityAsync("Locations", original, CancellationToken.None))!.Value.GetProperty("name").GetString().Should().Be("preserved");
        var thing = await store.CreateEntityAsync("Things", SensingJson("""
            {"name":"authority-thing","description":"test","Locations":[
              {"name":"linked","description":"test","encodingType":"application/geo+json",
               "location":{"type":"Point","coordinates":[3,4]}}]}
            """), CancellationToken.None);
        var locations = await store.QueryEntitiesAsync("Locations", new SensorThingsEntityQuery(null, [], "d.id", 0, 10,
            "Things", thing, "Locations", Count: true), CancellationToken.None);
        locations.Count.Should().Be(1);
        var location = locations.Entities.Single().GetProperty("@iot.id").GetInt64();
        (await store.PatchEntityAsync("Locations", location, SensingJson("""
            {"description":"updated","location":{"type":"Point","coordinates":[5,6]}}
            """), CancellationToken.None)).Should().BeTrue();
        (await store.GetEntityAsync("Locations", location, CancellationToken.None))!.Value.GetProperty("description").GetString().Should().Be("updated");
        var navigation = store.TranslateFilter("Things", new BinaryExpression(new PropertyReference("Locations/description"),
            BinaryOperator.Equal, new Literal("updated", LiteralType.Text)));
        navigation.Sql.Should().Contain("\"__honua_sensing_physical__\".");
        var filtered = await store.QueryEntitiesAsync("Things", new SensorThingsEntityQuery(navigation.Sql, navigation.Parameters,
            "d.id", 0, 100, Count: true), CancellationToken.None);
        filtered.Entities.Should().ContainSingle().Which.GetProperty("@iot.id").GetInt64().Should().Be(thing);
        const string literalToken = "\"__honua_sensing_physical__\".sta_location 'quoted' \"__honua_sensing_base__\".sta_thing";
        (await store.PatchEntityAsync("Locations", location,
            JsonSerializer.SerializeToElement(new { description = literalToken }), CancellationToken.None)).Should().BeTrue();
        var literalFilter = store.TranslateFilter("Locations", new BinaryExpression(new PropertyReference("description"),
            BinaryOperator.Equal, new Literal(literalToken, LiteralType.Text)));
        literalFilter.Parameters.Should().ContainSingle().Which.Should().Be(literalToken);
        var literalRows = await store.QueryEntitiesAsync("Locations", new SensorThingsEntityQuery(literalFilter.Sql,
            literalFilter.Parameters, "d.id", 0, 100), CancellationToken.None);
        literalRows.Entities.Should().ContainSingle().Which.GetProperty("@iot.id").GetInt64().Should().Be(location);
        literalFilter.Parameters.Should().ContainSingle().Which.Should().Be(literalToken);
        (await store.PatchEntityAsync("Locations", location, JsonSerializer.SerializeToElement(new
        {
            properties = new Dictionary<string, int> { [literalToken] = 5 }
        }), CancellationToken.None)).Should().BeTrue();
        var jsonKeyFilter = store.TranslateFilter("Locations", new BinaryExpression(new PropertyReference("properties/" + literalToken),
            BinaryOperator.Equal, new Literal(5, LiteralType.Number)));
        var jsonKeyRows = await store.QueryEntitiesAsync("Locations", new SensorThingsEntityQuery(jsonKeyFilter.Sql,
            jsonKeyFilter.Parameters, "d.id", 0, 100), CancellationToken.None);
        jsonKeyRows.Entities.Should().ContainSingle().Which.GetProperty("@iot.id").GetInt64().Should().Be(location);
        var history = await store.QueryEntitiesAsync("HistoricalLocations", new SensorThingsEntityQuery(null, [], "d.id", 0, 100,
            "Things", thing, "HistoricalLocations", Count: true), CancellationToken.None);
        history.Count.Should().BeGreaterThan(0);
        var datastream = await store.CreateEntityAsync("Datastreams", JsonSerializer.SerializeToElement(new
        {
            name = "stream",
            description = "test",
            unitOfMeasurement = new { name = "u", symbol = "u", definition = "urn:u" },
            Thing = new Dictionary<string, long> { ["@iot.id"] = thing },
            Sensor = new { name = "sensor", description = "test", encodingType = "text/plain", metadata = "test" },
            ObservedProperty = new { name = "property", description = "test", definition = "urn:property" }
        }), CancellationToken.None);
        var observation = await store.CreateEntityAsync("Observations", JsonSerializer.SerializeToElement(new
        {
            result = 7,
            phenomenonTime = "2026-10-19T00:00:00Z",
            Datastream = new Dictionary<string, long> { ["@iot.id"] = datastream }
        }), CancellationToken.None);
        (await store.PatchEntityAsync("Observations", observation, SensingJson("{\"result\":9}"), CancellationToken.None)).Should().BeTrue();
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            using var identifiers = new NpgsqlCommandBuilder();
            await using var command = new NpgsqlCommand($"SELECT result=9 AND result_json='9'::jsonb AND datastream_reference_id=datastream_id AND feature_of_interest_reference_id=feature_of_interest_id AND feature_of_interest_reference_id IS NOT NULL FROM {identifiers.QuoteIdentifier(schema)}.sta_observation WHERE id=@id", connection);
            command.Parameters.AddWithValue("id", observation);
            (await command.ExecuteScalarAsync()).Should().Be(true, "both preserved trigger functions must execute against the same cross-schema authority");
        }
        var areaOrder = store.TranslateOrderExpression("Datastreams", new PropertyReference("observedArea/type"));
        areaOrder.Sql.Should().Contain("\"__honua_sensing_physical__\".");
        var streamed = await store.QueryEntitiesAsync("Datastreams", new SensorThingsEntityQuery(null, areaOrder.Parameters,
            areaOrder.Sql + ",d.id", 0, 100), CancellationToken.None);
        var derived = streamed.Entities.Single(entity => entity.GetProperty("@iot.id").GetInt64() == datastream);
        var area = derived.GetProperty("observedArea");
        area.GetProperty("type").GetString().Should().Be("Polygon",
            "the production projection uses ST_MakeEnvelope even for a single-point extent");
        var ring = area.GetProperty("coordinates")[0];
        ring.GetArrayLength().Should().Be(5);
        foreach (var coordinate in ring.EnumerateArray())
        {
            coordinate.GetArrayLength().Should().Be(2);
            coordinate[0].GetDouble().Should().Be(5);
            coordinate[1].GetDouble().Should().Be(6);
        }
        (await store.DeleteEntityAsync("Observations", observation, CancellationToken.None)).Should().BeTrue();
        (await store.DeleteEntityAsync("Locations", location, CancellationToken.None)).Should().BeTrue();
        (await store.GetEntityAsync("Locations", location, CancellationToken.None)).Should().BeNull();
        var restarted = new PostgresObservationStore(new TestConnectionProvider(connectionString),
            CreateSensingGuard(schema), schema);
        (await restarted.GetEntityAsync("Locations", original, CancellationToken.None))!.Value.GetProperty("name").GetString().Should().Be("preserved");
        (await restarted.GetEntityAsync("Things", thing, CancellationToken.None)).Should().NotBeNull();
    }

    [SkippableTheory]
    [InlineData("duplicate")]
    [InlineData("partial")]
    [InlineData("mixed")]
    [InlineData("non-table")]
    [InlineData("foreign-sequence-default")]
    [InlineData("foreign-sequence-owner")]
    [InlineData("foreign-key")]
    [InlineData("disabled-trigger")]
    [InlineData("wrong-trigger-function")]
    [InlineData("function-overload")]
    public async Task SensingAuthority_AmbiguousOrMisboundFamily_RefusesWithoutMutation(string state)
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is required for physical sensing authority proof.");
        const string schema = "sensing_configured";
        var connectionString = await CreateSensingBaselineAsync();
        await ApplySensingAdoptionFixtureAsync(connectionString, schema);
        var mutation = state switch
        {
            "duplicate" => "CREATE TABLE sensing_configured.sta_location (LIKE honua.sta_location INCLUDING ALL)",
            "partial" => "DROP TABLE honua.sta_historical_location_location",
            "mixed" => "ALTER TABLE honua.sta_historical_location_location SET SCHEMA sensing_configured",
            "non-table" => "DROP TABLE honua.sta_historical_location_location; CREATE VIEW honua.sta_historical_location_location AS SELECT 1::bigint historical_location_id,1::bigint location_id",
            "foreign-sequence-default" => "CREATE SEQUENCE sensing_configured.foreign_ids; ALTER TABLE honua.sta_location ALTER COLUMN id SET DEFAULT nextval('sensing_configured.foreign_ids')",
            "foreign-sequence-owner" => "CREATE TABLE honua.foreign_owner(id bigint); ALTER SEQUENCE honua.sta_location_id_seq OWNED BY honua.foreign_owner.id",
            "foreign-key" => "ALTER TABLE honua.sta_historical_location DROP CONSTRAINT sta_historical_location_thing_id_fkey; CREATE TABLE sensing_configured.foreign_thing(id bigint PRIMARY KEY); ALTER TABLE honua.sta_historical_location ADD FOREIGN KEY(thing_id) REFERENCES sensing_configured.foreign_thing(id)",
            "disabled-trigger" => "ALTER TABLE sensing_configured.sta_observation DISABLE TRIGGER sta_observation_result_sync",
            "wrong-trigger-function" => "CREATE FUNCTION sensing_configured.wrong_sync() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$; DROP TRIGGER sta_observation_result_sync ON sensing_configured.sta_observation; CREATE TRIGGER sta_observation_result_sync BEFORE INSERT ON sensing_configured.sta_observation FOR EACH ROW EXECUTE FUNCTION sensing_configured.wrong_sync()",
            "function-overload" => "CREATE FUNCTION honua.sta_synchronize_result(integer) RETURNS integer LANGUAGE sql AS $$ SELECT $1 $$",
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
        await ExecuteAsync(connectionString, mutation);
        var before = await CaptureSensingAuthorityStateAsync(connectionString);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var resolve = () => PostgresSensorThingsSchema.ResolveAsync(connection, schema, true, requireConfiguredBase: true);
        await resolve.Should().ThrowAsync<DatabaseSchemaFloorException>();
        var store = new PostgresObservationStore(new TestConnectionProvider(connectionString),
            CreateSensingGuard(schema), schema);
        await store.Awaiting(instance => instance.GetEntityAsync("Locations", 1, CancellationToken.None)).Should().ThrowAsync<DatabaseSchemaFloorException>();
        (await CaptureSensingAuthorityStateAsync(connectionString)).Should().Be(before);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SensingAuthority_LegacyBaseIsPlanningOnly_RuntimeRefusesWithoutMutation(bool movedBack)
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is required for physical sensing authority proof.");
        var connectionString = await CreateSensingBaselineAsync();
        const string schema = "sensing_configured";
        await ExecuteAsync(connectionString, "CREATE SCHEMA sensing_configured");
        if (movedBack)
        {
            await ApplySensingAdoptionFixtureAsync(connectionString, schema);
            await using var bound = new NpgsqlConnection(connectionString);
            await bound.OpenAsync();
            (await PostgresSensorThingsSchema.ResolveAsync(bound, schema, true, requireConfiguredBase: true)).Should().Be("honua");
            foreach (var table in new[] { "sta_thing", "sta_sensor", "sta_observed_property", "sta_datastream", "sta_observation", "sta_observation_default" })
                await ExecuteAsync(connectionString, $"ALTER TABLE sensing_configured.{table} SET SCHEMA honua");
        }
        var before = await CaptureSensingAuthorityStateAsync(connectionString);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        (await PostgresSensorThingsSchema.ResolveAsync(connection, schema, true)).Should().Be("honua",
            "migration planning must inspect the complete pre-109 physical family");
        var runtime = () => PostgresSensorThingsSchema.ResolveAsync(connection, schema, true, requireConfiguredBase: true);
        await runtime.Should().ThrowAsync<DatabaseSchemaFloorException>();
        (await CaptureSensingAuthorityStateAsync(connectionString)).Should().Be(before);
    }

    [Theory]
    [InlineData(PostgresSensorThingsSchema.DeferredSchemaToken)]
    [InlineData(PostgresSensorThingsSchema.DeferredBaseSchemaToken)]
    public async Task SensingAuthority_ReservedConfiguredIdentifier_CannotBecomeAPlanningOrRuntimeSchema(string schema)
    {
        var store = new PostgresObservationStore(new TestConnectionProvider("not opened"),
            new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest), schema);
        var translate = () => store.TranslateFilter("Locations", new BinaryExpression(new PropertyReference("name"),
            BinaryOperator.Equal, new Literal("x", LiteralType.Text)));
        translate.Should().Throw<InvalidOperationException>();
        // The runtime resolver must reject this identifier before connection I/O.
        await using var connection = new NpgsqlConnection();
        var resolve = () => PostgresSensorThingsSchema.ResolveAsync(connection, schema, true, requireConfiguredBase: true);
        var rejection = await resolve.Should().ThrowAsync<DatabaseSchemaFloorException>();
        rejection.Which.Detail.Should().Contain("reserved");
    }

    [SkippableFact]
    public async Task SensingAuthority_UnrelatedRequirement_RemainsScopedWhileFullAndSensingGuardsRefuse()
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is required for scoped schema guard proof.");
        var connectionString = await CreateSensingBaselineAsync();
        await ExecuteAsync(connectionString, "DROP TABLE honua.sta_historical_location_location");
        var before = await CaptureSensingAuthorityStateAsync(connectionString);
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await guard.Awaiting(instance => instance.VerifyRequirementAsync(connection,
            DatabaseSchemaRequirement.MetadataV2Snapshot)).Should().NotThrowAsync();
        await guard.Awaiting(instance => instance.VerifyRequirementAsync(connection,
            DatabaseSchemaRequirement.SensorThings)).Should().ThrowAsync<DatabaseSchemaFloorException>();
        await guard.Awaiting(instance => instance.VerifyAsync(connection)).Should().ThrowAsync<DatabaseSchemaFloorException>();
        (await CaptureSensingAuthorityStateAsync(connectionString)).Should().Be(before);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SensingAuthority_EmptyJournalAdoption_UsesCreationFloorInsteadOfRuntimeCompleteness(bool malformedBase)
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is required for creation-floor adoption proof.");
        var raw = await postgres.CreateFreshDatabaseAsync();
        var connectionString = new NpgsqlConnectionStringBuilder(raw) { Pooling = false }.ConnectionString;
        await ExecuteAsync(connectionString, "CREATE SCHEMA honua; CREATE TABLE public.schema_versions(scriptname text NOT NULL)");
        const string name = "Honua.Server.Migrations.059_CreateSensorThings.sql";
        await using (var stream = typeof(Program).Assembly.GetManifestResourceStream(name)!)
        using (var reader = new StreamReader(stream))
            await ExecuteAsync(connectionString, (await reader.ReadToEndAsync()).Replace("$HonuaSchema$", "honua", StringComparison.Ordinal));
        // A valid subset of 127 creation-time objects is legitimate before the first
        // migration. The pending script creates the remainder; runtime completeness
        // must not run in this opt-in creation-floor validation phase.
        await ExecuteAsync(connectionString, """
            CREATE SEQUENCE honua.sta_location_id_seq;
            CREATE TABLE honua.sta_location(id bigint PRIMARY KEY DEFAULT nextval('honua.sta_location_id_seq'),
              name text NOT NULL,description text NOT NULL,encoding_type text NOT NULL,location jsonb NOT NULL,properties jsonb);
            """);
        if (malformedBase)
            await ExecuteAsync(connectionString, "ALTER TABLE honua.sta_thing ALTER COLUMN name TYPE integer USING 1");
        var before = await CaptureSensingAuthorityStateAsync(connectionString);
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var adopt = () => guard.VerifyFirstMigrationAdoptionAsync(connection);
        if (malformedBase) await adopt.Should().ThrowAsync<DatabaseSchemaFloorException>();
        else await adopt.Should().NotThrowAsync();
        (await CaptureSensingAuthorityStateAsync(connectionString)).Should().Be(before);
        await guard.Awaiting(instance => instance.VerifyConsistencyAsync(connection)).Should().ThrowAsync<DatabaseSchemaFloorException>();
        (await CaptureSensingAuthorityStateAsync(connectionString)).Should().Be(before);
    }

    private static PostgresCoreSchemaGuard CreateSensingGuard(string schema) => new(ServerCoreSchemaMigrations.Manifest,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Schema"] = schema }).Build());

    private async Task<string> CreateSensingBaselineAsync()
    {
        var raw = await postgres.CreateFreshDatabaseAsync(enablePostGisRaster: true);
        var connectionString = new NpgsqlConnectionStringBuilder(raw) { Pooling = false }.ConnectionString;
        var runner = new PostgresDatabaseMigrationRunner(new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest), ServerCoreSchemaMigrations.Manifest);
        var result = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);
        result.Successful.Should().BeTrue(result.ErrorMessage);
        await ExecuteAsync(connectionString, """INSERT INTO honua.sta_location(id,name,description,encoding_type,location) VALUES(7101,'preserved baseline','source row','application/geo+json','{"type":"Point","coordinates":[1,2]}')""");
        return connectionString;
    }

    private static async Task ApplySensingAdoptionFixtureAsync(string connectionString, string schema)
    {
        const string name = "Honua.Server.Migrations.109_AdoptConfiguredGuardedSchema.sql";
        await using var stream = typeof(Program).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var source = await reader.ReadToEndAsync();
        using var identifiers = new NpgsqlCommandBuilder();
        await ExecuteAsync(connectionString, source.Replace("$HonuaSchema$", identifiers.QuoteIdentifier(schema), StringComparison.Ordinal));
    }

    private static JsonElement SensingJson(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static async Task<string> CaptureSensingFamilyAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_build_object('objects',(SELECT jsonb_agg(jsonb_build_array(c.relname,c.oid,c.relkind::text) ORDER BY c.relname)
                FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='honua'
                AND c.relname IN ('sta_location','sta_historical_location','sta_feature_of_interest','sta_thing_location','sta_historical_location_location','sta_location_id_seq','sta_historical_location_id_seq','sta_feature_of_interest_id_seq')),
              'functions',(SELECT jsonb_agg(jsonb_build_array(p.proname,p.oid,pg_get_functiondef(p.oid)) ORDER BY p.proname)
                FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='honua' AND p.proname IN ('sta_synchronize_result','sta_synchronize_references')),
              'locations',(SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM honua.sta_location t),
              'sequences',(SELECT jsonb_agg(to_jsonb(s) ORDER BY sequencename) FROM pg_sequences s WHERE schemaname='honua' AND sequencename IN ('sta_location_id_seq','sta_historical_location_id_seq','sta_feature_of_interest_id_seq')))::text
            """, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> CaptureSensingAuthorityStateAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_build_object('objects',(SELECT jsonb_agg(jsonb_build_array(n.nspname,c.relname,c.oid,c.relkind::text) ORDER BY n.nspname,c.relname)
                FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname !~ '^pg_' AND n.nspname<>'information_schema'),
              'columns',(SELECT jsonb_agg(jsonb_build_array(n.nspname,c.relname,a.attname,a.atttypid,a.attnotnull,pg_get_expr(d.adbin,d.adrelid)) ORDER BY n.nspname,c.relname,a.attnum)
                FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
                LEFT JOIN pg_attrdef d ON d.adrelid=c.oid AND d.adnum=a.attnum WHERE n.nspname !~ '^pg_' AND n.nspname<>'information_schema'),
              'constraints',(SELECT jsonb_agg(jsonb_build_array(n.nspname,c.conname,c.oid,pg_get_constraintdef(c.oid),c.convalidated) ORDER BY n.nspname,c.oid)
                FROM pg_constraint c JOIN pg_namespace n ON n.oid=c.connamespace WHERE n.nspname !~ '^pg_' AND n.nspname<>'information_schema'),
              'functions',(SELECT jsonb_agg(jsonb_build_array(n.nspname,p.proname,p.oid,pg_get_functiondef(p.oid)) ORDER BY n.nspname,p.oid)
                FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname IN ('honua','sensing_configured') AND p.prokind='f'),
              'triggers',(SELECT jsonb_agg(jsonb_build_array(t.oid,t.tgrelid,t.tgfoid,t.tgenabled::text,pg_get_triggerdef(t.oid)) ORDER BY t.oid)
                FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname IN ('honua','sensing_configured')),
              'sequences',(SELECT jsonb_agg(to_jsonb(s) ORDER BY schemaname,sequencename) FROM pg_sequences s WHERE schemaname IN ('honua','sensing_configured')),
              'rows',(SELECT jsonb_agg(to_jsonb(l) ORDER BY id) FROM honua.sta_location l),
              'journal',(SELECT jsonb_agg(scriptname ORDER BY scriptname) FROM public.schema_versions))::text
            """, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
