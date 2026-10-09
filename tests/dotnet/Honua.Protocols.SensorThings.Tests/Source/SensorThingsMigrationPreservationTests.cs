// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.SensorThings.Abstractions;
using Honua.Core.Features.SensorThings.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

[Collection("Database")]
[Protocol(TestProtocols.SensorThings)]
public sealed class SensorThingsMigrationPreservationTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();
    public Task InitializeAsync() => _postgres.InitializeAsync();
    public Task DisposeAsync() => _postgres.DisposeAsync();

    [IntegrationTest]
    public async Task Upgrade_PreservesLegacyValuesAndOrphanReferences_WithoutIdentifierCollision()
    {
        var schema = "sta_upgrade_" + Guid.NewGuid().ToString("N");
        await _postgres.CreateSchemaUnderLockAsync(schema);
        try
        {
            foreach (var script in new[] { "059_CreateSensorThings.sql", "116_AddSensorThingsIdSequences.sql" })
                await ApplyAsync(schema, script);
            await _postgres.ExecuteAsync("""
                UPDATE sta_observation SET feature_of_interest_id=1 WHERE id=1;
                INSERT INTO sta_observation(id,datastream_id,phenomenon_time,result,feature_of_interest_id)
                    VALUES (9001,9001,'2020-01-01T00:00:00Z',123.75,9001);
                """, schema);
            await using var connection = await _postgres.GetConnectionAsync(schema);
            var before = await SnapshotAsync(connection);
            await ApplyAsync(schema, "126_CompleteSensorThingsSensing.sql");
            var after = await SnapshotAsync(connection);
            after.Should().Be(before, "the additive migration must retain identifiers, numeric values, timestamps and unresolved reference IDs");
            await using var allocate = new NpgsqlCommand("INSERT INTO sta_feature_of_interest(name,description,encoding_type,feature) VALUES ('Unrelated new feature','Synthetic','application/geo+json','{\"type\":\"Feature\",\"geometry\":null,\"properties\":{}}') RETURNING id", connection);
            var featureId = (long)(await allocate.ExecuteScalarAsync())!;
            featureId.Should().BeGreaterThan(9001, "automatic identifiers must never collide with retained unresolved references");
            await _postgres.ExecuteAsync("""
                INSERT INTO sta_feature_of_interest(id,name,description,encoding_type,feature)
                    VALUES (1,'Unrelated manually numbered feature','Synthetic','application/geo+json','{"type":"Feature","geometry":null,"properties":{}}');
                """, schema);
            await using (var unresolved = new NpgsqlCommand("SELECT feature_of_interest_reference_id FROM sta_observation WHERE id=1", connection))
                (await unresolved.ExecuteScalarAsync()).Should().Be(DBNull.Value, "an unrelated matching ID must not reconcile a legacy observation");
            await _postgres.ExecuteAsync("""
                INSERT INTO sta_datastream(id,name,description,observation_type,unit_name,unit_symbol,unit_definition,thing_id,sensor_id,observed_property_id)
                    VALUES (9001,'Unrelated new datastream','Synthetic','http://www.opengis.net/def/observationType/OGC-OM/2.0/OM_TruthObservation',NULL,NULL,NULL,1,1,1);
                """, schema);
            var fixture = new WebAppFixture().ConfigureServices(services => services.AddScoped<ISchemaContext>(_ => new FixedSchema(schema)));
            try
            {
                await fixture.InitializeAsync();
                var store = fixture.GetService<IObservationStore>();
                var datastream = await store.GetDatastreamAsync(9001, CancellationToken.None);
                datastream.Should().NotBeNull();
                datastream!.PhenomenonTimeStart.Should().BeNull("an unresolved legacy datastream ID must not link to a later matching catalog ID");
                datastream.PhenomenonTimeEnd.Should().BeNull();
                datastream.UnitName.Should().BeNull();
                datastream.UnitSymbol.Should().BeNull();
                datastream.UnitDefinition.Should().BeNull();
                var listed = (await store.ListDatastreamsAsync(CatalogQuery.Page(0, 100), CancellationToken.None)).Single(item => item.Id == 9001);
                listed.PhenomenonTimeStart.Should().BeNull();
            }
            finally { await fixture.DisposeAsync(); }
            await _postgres.ExecuteDdlUnderLockAsync($"CREATE TABLE {schema}.sta_observation_future PARTITION OF {schema}.sta_observation FOR VALUES FROM ('2030-01-01') TO ('2031-01-01')", schema);
            await using (var insert = new NpgsqlCommand("INSERT INTO sta_observation(id,datastream_id,phenomenon_time,result,feature_of_interest_id) VALUES (9002,1,'2030-02-01',17,@feature)", connection))
            {
                insert.Parameters.AddWithValue("feature", featureId);
                await insert.ExecuteNonQueryAsync();
            }
            var invalidInsert = async () =>
            {
                await using var invalid = new NpgsqlCommand("INSERT INTO sta_observation(id,datastream_id,phenomenon_time,result,feature_of_interest_id) VALUES (9003,1,'2030-02-01',18,999999)", connection);
                await invalid.ExecuteNonQueryAsync();
            };
            (await invalidInsert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
            await using (var delete = new NpgsqlCommand("DELETE FROM sta_feature_of_interest WHERE id=@feature", connection))
            {
                delete.Parameters.AddWithValue("feature", featureId);
                await delete.ExecuteNonQueryAsync();
            }
            await using (var remaining = new NpgsqlCommand("SELECT count(*) FROM sta_observation WHERE id=9002", connection))
                ((long)(await remaining.ExecuteScalarAsync())!).Should().Be(0, "deletion cascades must cover future partitions");
            (await SnapshotAsync(connection)).Should().Be(before);
        }
        finally { await _postgres.ExecuteDdlUnderLockAsync($"DROP SCHEMA {schema} CASCADE"); }
    }

    private async Task ApplyAsync(string schema, string filename)
    {
        var assembly = typeof(Program).Assembly;
        var name = assembly.GetManifestResourceNames().Single(name => name.EndsWith(filename, StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var sql = (await reader.ReadToEndAsync()).Replace("$HonuaSchema$", schema, StringComparison.Ordinal);
        await _postgres.ExecuteDdlUnderLockAsync(sql, schema);
    }

    private static async Task<string> SnapshotAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SELECT jsonb_agg(to_jsonb(v) ORDER BY id)::text FROM (SELECT id,datastream_id,phenomenon_time,result_time,result,feature_of_interest_id FROM sta_observation) v", connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private sealed class FixedSchema(string schema) : ISchemaContext
    {
        public string? CurrentSchema => schema;
    }
}
