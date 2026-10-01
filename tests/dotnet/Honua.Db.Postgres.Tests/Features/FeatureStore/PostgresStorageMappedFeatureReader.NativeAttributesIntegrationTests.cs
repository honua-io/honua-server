// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed partial class PostgresStorageMappedFeatureReaderEncodedFormatsIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadFeatures_NativePrimitives_PreservesCanonicalTypesNullsAndProjection(bool streaming)
    {
        await _fixture.ExecuteAsync($"""
            ALTER TABLE {_schema}.cities ADD COLUMN active boolean, ADD COLUMN tiny smallint,
                ADD COLUMN huge bigint, ADD COLUMN label varchar(200);
            UPDATE {_schema}.cities SET name = 'Kāneʻohe 🌋', active = true, tiny = -32768,
                huge = 9223372036854775807, label = 'a\b "quoted"' WHERE objectid = 1;
            UPDATE {_schema}.cities SET name = NULL, population = NULL WHERE objectid = 2;
            """);
        var fields = new[]
        {
            new MetadataV2Field { Name = "active", Type = MetadataV2FieldType.Boolean },
            new MetadataV2Field { Name = "tiny", Type = MetadataV2FieldType.Integer },
            new MetadataV2Field { Name = "huge", Type = MetadataV2FieldType.BigInteger },
            new MetadataV2Field { Name = "label", Type = MetadataV2FieldType.String }
        };
        var provider = CreateReader(additionalFields: fields);
        var features = await ReadNativeFeaturesAsync(provider, streaming);
        features.Select(feature => feature.Id).Should().Equal(1, 2);
        features.Should().OnlyContain(feature => feature.Geometry != null);
        features[0].Attributes["population"].Should().BeOfType<long>().Which.Should().Be(350000);
        features[0].Attributes["tiny"].Should().BeOfType<long>().Which.Should().Be(short.MinValue);
        features[0].Attributes["huge"].Should().BeOfType<long>().Which.Should().Be(long.MaxValue);
        features[0].Attributes["active"].Should().BeOfType<bool>().Which.Should().BeTrue();
        features[0].Attributes["name"].Should().Be("Kāneʻohe 🌋");
        features[0].Attributes["label"].Should().Be("a\\b \"quoted\"");
        foreach (var name in new[] { "name", "population", "active", "tiny", "huge", "label" })
        {
            features[1].Attributes.Should().ContainKey(name).WhoseValue.Should().BeNull();
        }

        var projected = await ReadNativeFeaturesAsync(provider, streaming, new FeatureQuery
        {
            Limit = 10,
            OutFields = ["name", "population", "active"],
            EnforcedMaskedFields = ["POPULATION"]
        });
        projected.Should().OnlyContain(feature => feature.Attributes.Count == 3);
        projected[0].Attributes.Keys.Should().BeEquivalentTo(["objectid", "name", "active"]);
        var excluded = await ReadNativeFeaturesAsync(provider, streaming, new FeatureQuery { Limit = 10, ExcludeAttributes = true });
        excluded.Should().OnlyContain(feature => feature.Attributes.Count == 1 && feature.Attributes.ContainsKey("objectid"));

        // Execute the actual projection, rather than assert only its SQL text.
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = CreateNativeTestCommand(provider, new FeatureQuery { Limit = 10 }, connection, out _);
        await using var wire = await command.ExecuteReaderAsync();
        (await wire.ReadAsync()).Should().BeTrue();
        wire.GetDataTypeName(2).Should().Be("jsonb");
        using var document = wire.GetFieldValue<JsonDocument>(2);
        document.RootElement.EnumerateObject().Should().BeEmpty("all primitive attributes use their typed columns");
        wire.FieldCount.Should().BeGreaterThan(3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadFeatures_StaleStringHintsAndUnknownTypes_MatchPostgresJsonOracle(bool streaming)
    {
        await _fixture.ExecuteAsync($"""
            CREATE DOMAIN {_schema}.native_amount AS integer;
            CREATE TYPE {_schema}.native_status AS ENUM ('open', 'closed');
            ALTER TABLE {_schema}.cities ADD COLUMN precise numeric, ADD COLUMN reading real,
                ADD COLUMN observed timestamptz, ADD COLUMN day date, ADD COLUMN token uuid,
                ADD COLUMN nested jsonb, ADD COLUMN numbers integer[], ADD COLUMN amount {_schema}.native_amount,
                ADD COLUMN status {_schema}.native_status, ADD COLUMN active boolean;
            UPDATE {_schema}.cities SET precise = 0.99999999999999999999, reading = 12.5,
                observed = '2026-09-26 00:00:00.123456+00', day = '2026-09-26',
                token = '12345678-1234-1234-1234-123456789abc',
                nested = jsonb_build_object('null', NULL, 'array', jsonb_build_array(1, NULL, true)),
                numbers = ARRAY[1, NULL, 3], amount = 42, status = 'open', active = true WHERE objectid = 1;
            """);
        var names = new[] { "precise", "reading", "observed", "day", "token", "nested", "numbers", "amount", "status", "active" };
        // Actual physical types, including a boolean, override stale publication hints.
        var provider = CreateReader(additionalFields: names.Select(name => new MetadataV2Field
        {
            Name = name,
            Type = MetadataV2FieldType.String
        }).ToArray());
        var expected = await NativeAttributeOracleAsync(names);
        var features = await ReadNativeFeaturesAsync(provider, streaming);
        AssertNativeAttributeOracle(features, expected);
        // Every database reader/document is closed; nested values remain independently owned.
        var nested = features[0].Attributes["nested"].Should().BeOfType<JsonElement>().Subject;
        nested.GetProperty("null").ValueKind.Should().Be(JsonValueKind.Null);
        nested.GetProperty("array")[1].ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadFeatures_PhysicalTypeChanges_RebindsWithoutChangingPublishedHints(bool streaming)
    {
        var provider = CreateReader();
        var original = await ReadNativeFeaturesAsync(provider, streaming);
        original[0].Attributes["name"].Should().BeOfType<string>();
        await _fixture.ExecuteAsync($"""
            ALTER TABLE {_schema}.cities ALTER COLUMN name TYPE numeric
                USING CASE WHEN objectid = 1 THEN 0.99999999999999999999 ELSE 2 END;
            """);
        var features = await ReadNativeFeaturesAsync(provider, streaming);
        AssertNativeAttributeOracle(features, await NativeAttributeOracleAsync([]));
        features[0].Attributes["name"].Should().BeOfType<double>().Which.Should().Be(1);
        features[1].Attributes["name"].Should().BeOfType<long>().Which.Should().Be(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadFeatures_CustomTypeEqualityOperator_CannotSuppressJsonFallback(bool streaming)
    {
        await _fixture.ExecuteAsync($"""
            ALTER TABLE {_schema}.cities ALTER COLUMN name TYPE numeric USING objectid;
            CREATE FUNCTION {_schema}.always_same(pg_catalog.regtype, pg_catalog.regtype) RETURNS boolean
                LANGUAGE sql IMMUTABLE AS 'SELECT true';
            CREATE OPERATOR {_schema}.= (FUNCTION = {_schema}.always_same,
                LEFTARG = pg_catalog.regtype, RIGHTARG = pg_catalog.regtype);
            """);
        var settings = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            SearchPath = _schema + ",pg_catalog,public",
            Pooling = false
        };
        var provider = CreateReader(connectionString: settings.ConnectionString);
        var features = await ReadNativeFeaturesAsync(provider, streaming);
        AssertNativeAttributeOracle(features, await NativeAttributeOracleAsync([]));
        features.Select(feature => feature.Attributes["name"]).Should().Equal([1L, 2L]);
    }

    [Fact]
    public async Task ReadFeature_NativePrimitivePage_AllocatesLessThanCanonicalJsonbStorage()
    {
        var fields = Enumerable.Range(0, 16).Select(i => new MetadataV2Field
        {
            Name = "native_text_" + i,
            Type = MetadataV2FieldType.String
        }).ToArray();
        var columns = string.Join(", ", fields.Select(field => "ADD COLUMN " + field.Name + " text"));
        var updates = string.Join(", ", fields.Select(field => field.Name + " = repeat('🌋unicode', 8)"));
        await _fixture.ExecuteAsync($"""
            ALTER TABLE {_schema}.cities {columns};
            INSERT INTO {_schema}.cities (objectid, geom, name, population)
                SELECT i, ST_SetSRID(ST_MakePoint(-157.8,21.3),4326), 'point-' || i, i FROM generate_series(3,100) i;
            UPDATE {_schema}.cities SET {updates};
            UPDATE {_schema}.cities SET attributes = to_jsonb(cities) - 'attributes' - 'geom';
            """);
        var native = CreateReader(additionalFields: fields);
        var canonical = CreateReader(attributesColumn: "attributes", additionalFields: fields);
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        _ = DecodeNativeTestPage(native, connection, out _);
        _ = DecodeNativeTestPage(canonical, connection, out _);
        var nativeBytes = DecodeNativeTestPage(native, connection, out var nativeFeatures);
        var canonicalBytes = DecodeNativeTestPage(canonical, connection, out var canonicalFeatures);
        nativeFeatures.Length.Should().Be(100);
        canonicalFeatures.Length.Should().Be(100);
        for (var i = 0; i < nativeFeatures.Length; i++)
        {
            nativeFeatures[i].Id.Should().Be(canonicalFeatures[i].Id);
            foreach (var pair in canonicalFeatures[i].Attributes)
            {
                nativeFeatures[i].Attributes[pair.Key].Should().Be(pair.Value);
                nativeFeatures[i].Attributes[pair.Key]?.GetType().Should().Be(pair.Value?.GetType());
            }
        }

        Console.WriteLine($"100-feature native row decoding allocated {nativeBytes} bytes; canonical JSONB storage allocated {canonicalBytes} bytes. Query execution/binding are outside this allocation fixture.");
        nativeBytes.Should().BeLessThan(canonicalBytes);
    }

    private static NpgsqlCommand CreateNativeTestCommand(PostgresStorageMappedFeatureReader provider, FeatureQuery query,
        NpgsqlConnection connection, out object sql)
    {
        sql = typeof(PostgresStorageMappedFeatureReader).GetMethod("BuildFeatureSelect", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(provider, [query, false])!;
        var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        foreach (var value in (IReadOnlyList<object?>)sql.GetType().GetProperty("Parameters")!.GetValue(sql)!)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }

        return command;
    }

    private static long DecodeNativeTestPage(PostgresStorageMappedFeatureReader provider, NpgsqlConnection connection,
        out Feature[] features)
    {
        using var command = CreateNativeTestCommand(provider, new FeatureQuery { Limit = 100 }, connection, out var sql);
        using var wire = command.ExecuteReader();
        var decoder = typeof(PostgresStorageMappedFeatureReader).GetMethod("BindNativeAttributeDecoder", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [sql, wire]);
        var read = typeof(PostgresStorageMappedFeatureReader).GetMethod("ReadFeature", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object?[] arguments = [wire, false, decoder];
        features = new Feature[100];
        var index = 0;
        var start = GC.GetAllocatedBytesForCurrentThread();
        while (wire.Read())
        {
            features[index++] = (Feature)read.Invoke(provider, arguments)!;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        index.Should().Be(100);
        return allocated;
    }

    private async Task<List<Feature>> ReadNativeFeaturesAsync(PostgresStorageMappedFeatureReader provider, bool streaming,
        FeatureQuery? query = null)
    {
        var effectiveQuery = query ?? new FeatureQuery { Limit = 10 };
        if (!streaming)
        {
            return (await provider.QueryPageAsync(1, effectiveQuery)).Items.ToList();
        }

        var features = new List<Feature>();
        await foreach (var feature in provider.StreamFeaturesAsync(1, effectiveQuery))
        {
            features.Add(feature);
        }

        return features;
    }

    private async Task<List<Dictionary<string, object?>>> NativeAttributeOracleAsync(string[] extraFields)
    {
        var names = new[] { "objectid", "name", "population" }.Concat(extraFields);
        var pairs = string.Join(", ", names.Select(name => "'" + name + "', \"" + name + "\""));
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT jsonb_build_object({pairs})::text FROM {_schema}.cities ORDER BY objectid", connection);
        await using var wire = await command.ExecuteReaderAsync();
        var result = new List<Dictionary<string, object?>>();
        while (await wire.ReadAsync())
        {
            var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            FeatureAttributeJsonReader.ReadInto(wire.GetString(0), attributes);
            result.Add(attributes);
        }

        return result;
    }

    private static void AssertNativeAttributeOracle(IReadOnlyList<Feature> features,
        IReadOnlyList<Dictionary<string, object?>> expected)
    {
        features.Count.Should().Be(expected.Count);
        for (var i = 0; i < features.Count; i++)
        {
            features[i].Attributes.Keys.Should().BeEquivalentTo(expected[i].Keys);
            foreach (var pair in expected[i])
            {
                var actual = features[i].Attributes[pair.Key];
                if (pair.Value is JsonElement element)
                {
                    var value = actual.Should().BeOfType<JsonElement>().Subject;
                    JsonElement.DeepEquals(value, element).Should().BeTrue();
                }
                else
                {
                    actual.Should().Be(pair.Value);
                    actual?.GetType().Should().Be(pair.Value?.GetType());
                }
            }
        }
    }
}
