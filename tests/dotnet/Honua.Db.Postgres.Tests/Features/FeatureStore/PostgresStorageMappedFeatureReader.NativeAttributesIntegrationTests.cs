// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.Infrastructure;
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

    [Theory]
    [InlineData(false, "name", "numeric", "objectid")]
    [InlineData(true, "name", "numeric", "objectid")]
    [InlineData(false, "population", "bigint", "population::bigint + 2147483648")]
    [InlineData(true, "population", "bigint", "population::bigint + 2147483648")]
    [InlineData(false, "name", "native_label_domain", "name")]
    [InlineData(true, "name", "native_label_domain", "name")]
    [InlineData(false, "name", "native_label_enum", "CASE WHEN objectid = 1 THEN 'open' ELSE 'closed' END")]
    [InlineData(true, "name", "native_label_enum", "CASE WHEN objectid = 1 THEN 'open' ELSE 'closed' END")]
    public async Task ReadFeatures_AutoPreparedNativeProjection_RecoversPhysicalTypeChanges(
        bool streaming, string column, string physicalType, string conversion)
    {
        await _fixture.ExecuteAsync($"""
            CREATE DOMAIN {_schema}.native_label_domain AS text;
            CREATE TYPE {_schema}.native_label_enum AS ENUM ('open', 'closed');
            """);
        var settings = NativeAutoPrepareSettings();
        var provider = CreateReader(connectionString: settings.ConnectionString);
        for (var i = 0; i < 6; i++)
        {
            AssertNativeAttributeOracle(await ReadNativeFeaturesAsync(provider, streaming),
                await NativeAttributeOracleAsync([]));
        }
        await AssertNativeAutoPreparedAsync(settings.ConnectionString);

        var type = physicalType.StartsWith("native_label_", StringComparison.Ordinal)
            ? _schema + "." + physicalType : physicalType;
        await _fixture.ExecuteAsync($"ALTER TABLE {_schema}.cities ALTER COLUMN {column} TYPE {type} USING ({conversion})::{type};");
        var expected = await NativeAttributeOracleAsync([]);
        // Verify the first recovery and later executions after Npgsql replaces
        // the invalidated auto-prepared statement, on the same physical pool.
        for (var i = 0; i < 4; i++)
        {
            var features = await ReadNativeFeaturesAsync(provider, streaming);
            features.Select(feature => feature.Id).Should().Equal(1, 2);
            AssertNativeAttributeOracle(features, expected);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReadFeatures_AutoPreparedNativeRecovery_DoesNotRepeatVolatileRowPolicy(
        bool streaming, bool smallintHint)
    {
        await _fixture.ExecuteAsync($"""
            ALTER TABLE {_schema}.cities ALTER COLUMN population TYPE smallint USING objectid::smallint;
            """);
        var role = "native_reader_" + Guid.NewGuid().ToString("N");
        var rolePassword = Guid.NewGuid().ToString("N");
        await _fixture.ExecuteAsync($"""
            CREATE ROLE {role} LOGIN PASSWORD '{rolePassword}';
            GRANT USAGE ON SCHEMA {_schema} TO {role};
            GRANT SELECT ON {_schema}.cities TO {role};
            CREATE SEQUENCE {_schema}.read_effects;
            GRANT USAGE, SELECT ON {_schema}.read_effects TO {role};
            ALTER TABLE {_schema}.cities ENABLE ROW LEVEL SECURITY;
            CREATE POLICY counted ON {_schema}.cities USING (nextval('{_schema}.read_effects') > 0);
            """);
        try
        {
            var settings = NativeAutoPrepareSettings();
            // Connect as the constrained role itself so pool reset cannot restore
            // the fixture superuser and bypass the row-security regression.
            settings.Username = role;
            settings.Password = rolePassword;
            var provider = CreateReader(smallintHint: smallintHint, connectionString: settings.ConnectionString);
            var query = smallintHint ? SmallintQuery("=", 1L) : new FeatureQuery { Limit = 10 };
            for (var i = 0; i < 6; i++)
            {
                (await ReadNativeFeaturesAsync(provider, streaming, query)).Should().NotBeEmpty();
            }
            await AssertNativeAutoPreparedAsync(settings.ConnectionString, role);
            await _fixture.ExecuteAsync($"ALTER TABLE {_schema}.cities ALTER COLUMN population TYPE bigint;");
            var canonicalSettings = new NpgsqlConnectionStringBuilder(settings.ConnectionString)
            {
                MaxAutoPrepare = 0,
                ApplicationName = _schema + "_native_canonical"
            };
            await _fixture.ExecuteAsync($"ALTER SEQUENCE {_schema}.read_effects RESTART WITH 1;");
            var canonical = await ReadNativeFeaturesAsync(CreateReader(connectionString: canonicalSettings.ConnectionString), streaming, query);
            var expectedEffects = await ReadEffectCountAsync();
            expectedEffects.Should().BeGreaterThan(0);
            for (var i = 0; i < 4; i++)
            {
                await _fixture.ExecuteAsync($"ALTER SEQUENCE {_schema}.read_effects RESTART WITH 1;");
                var features = await ReadNativeFeaturesAsync(provider, streaming, query);
                features.Select(feature => feature.Id).Should().Equal(canonical.Select(feature => feature.Id));
                AssertNativeAttributeOracle(features, canonical.Select(feature =>
                    feature.Attributes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)).ToList());
                (await ReadEffectCountAsync()).Should().Be(expectedEffects,
                    "cached-result recovery must not replay source rows or their volatile policy");
            }
        }
        finally
        {
            await _fixture.ExecuteAsync($"DROP OWNED BY {role}; DROP ROLE {role};");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadFeatures_AutoPreparedNativeDrift_InCallerTransaction_UsesStableDescriptor(bool ambient)
    {
        var settings = NativeAutoPrepareSettings();
        var connectionProvider = new FixtureConnectionProvider(settings.ConnectionString);
        var provider = CreateReader(connectionProvider: connectionProvider);
        for (var i = 0; i < 6; i++)
        {
            (await ReadNativeFeaturesAsync(provider, streaming: false)).Should().NotBeEmpty();
        }
        await AssertNativeAutoPreparedAsync(settings.ConnectionString);
        await _fixture.ExecuteAsync($"ALTER TABLE {_schema}.cities ALTER COLUMN population TYPE bigint;");
        var expected = await NativeAttributeOracleAsync([]);
        if (ambient)
        {
            using var scope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
            AssertNativeAttributeOracle(await ReadNativeFeaturesAsync(provider, streaming: false), expected);
            await using var lease = await connectionProvider.OpenNpgsqlConnectionAsync();
            await using var command = new NpgsqlCommand("SELECT 1", lease);
            (await command.ExecuteScalarAsync()).Should().Be(1);
            scope.Complete();
        }
        else
        {
            await PostgresMutationTransaction.ExecuteAsync(connectionProvider, async () =>
            {
                AssertNativeAttributeOracle(await ReadNativeFeaturesAsync(provider, streaming: false), expected);
                await using var lease = await connectionProvider.OpenNpgsqlConnectionAsync();
                lease.Transaction.Should().NotBeNull();
                await using var command = new NpgsqlCommand("SELECT 1", lease);
                (await command.ExecuteScalarAsync()).Should().Be(1);
                return true;
            }, _ => true, CancellationToken.None);
        }
    }

    [Fact]
    public async Task ReadFeatures_AutoPreparedNativeDrift_CancellationDoesNotExecuteRecovery()
    {
        var settings = NativeAutoPrepareSettings();
        var provider = CreateReader(connectionString: settings.ConnectionString);
        for (var i = 0; i < 6; i++)
        {
            (await ReadNativeFeaturesAsync(provider, streaming: false)).Should().NotBeEmpty();
        }
        await AssertNativeAutoPreparedAsync(settings.ConnectionString);
        await _fixture.ExecuteAsync($"ALTER TABLE {_schema}.cities ALTER COLUMN population TYPE bigint;");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = () => provider.QueryPageAsync(1, new FeatureQuery { Limit = 10 }, cancellation.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        // The pool remains usable and the later uncancelled request still handles drift.
        AssertNativeAttributeOracle(await ReadNativeFeaturesAsync(provider, streaming: false),
            await NativeAttributeOracleAsync([]));
    }

    [Fact]
    public async Task ReadFeatures_AutoPreparedNativeDrift_SerialPlanner_RestoresSessionSettings()
    {
        var settings = NativeAutoPrepareSettings();
        settings.NoResetOnClose = true;
        await using (var setup = new NpgsqlConnection(settings.ConnectionString))
        {
            await setup.OpenAsync();
            await using var command = new NpgsqlCommand("SET max_parallel_workers_per_gather = 2", setup);
            await command.ExecuteNonQueryAsync();
        }
        var provider = CreateReader(connectionString: settings.ConnectionString, preferSerialPlan: true);
        var query = new FeatureQuery
        {
            Limit = 10,
            SpatialFilter = new SpatialFilter
            {
                Geometry = [],
                Srid = 4326,
                SpatialRelationship = SpatialRelationship.Intersects,
                IsSimpleEnvelope = true,
                EnvelopeMinX = -180,
                EnvelopeMinY = -90,
                EnvelopeMaxX = 180,
                EnvelopeMaxY = 90
            }
        };
        for (var i = 0; i < 6; i++)
        {
            (await provider.QueryPageAsync(1, query)).Items.Select(feature => feature.Id).Should().Equal(1, 2);
        }
        await AssertNativeAutoPreparedAsync(settings.ConnectionString);
        await _fixture.ExecuteAsync($"ALTER TABLE {_schema}.cities ALTER COLUMN population TYPE bigint;");
        var expected = await NativeAttributeOracleAsync([]);
        for (var i = 0; i < 4; i++)
        {
            AssertNativeAttributeOracle((await provider.QueryPageAsync(1, query)).Items.ToList(), expected);
            await using var check = new NpgsqlConnection(settings.ConnectionString);
            await check.OpenAsync();
            await using var command = new NpgsqlCommand("SHOW max_parallel_workers_per_gather", check);
            (await command.ExecuteScalarAsync()).Should().Be("2");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadFeatures_ResultTypeMessageFromRowPolicy_IsNotRetried(bool streaming)
    {
        var role = "native_failure_" + Guid.NewGuid().ToString("N");
        var rolePassword = Guid.NewGuid().ToString("N");
        await _fixture.ExecuteAsync($"""
            CREATE ROLE {role} LOGIN PASSWORD '{rolePassword}';
            GRANT USAGE ON SCHEMA {_schema} TO {role};
            GRANT SELECT ON {_schema}.cities TO {role};
            CREATE SEQUENCE {_schema}.read_effects;
            GRANT USAGE, SELECT ON {_schema}.read_effects TO {role};
            CREATE FUNCTION {_schema}.fail_policy() RETURNS boolean LANGUAGE plpgsql VOLATILE AS $$
            BEGIN
                PERFORM nextval('{_schema}.read_effects');
                RAISE EXCEPTION USING ERRCODE = '0A000', MESSAGE = 'cached plan must not change result type';
            END $$;
            ALTER TABLE {_schema}.cities ENABLE ROW LEVEL SECURITY;
            CREATE POLICY failure ON {_schema}.cities USING ({_schema}.fail_policy());
            """);
        try
        {
            var settings = NativeAutoPrepareSettings();
            // Connect as the constrained role itself so pool reset cannot restore
            // the fixture superuser and bypass the row-security regression.
            settings.Username = role;
            settings.Password = rolePassword;
            var provider = CreateReader(connectionString: settings.ConnectionString);
            var read = () => ReadNativeFeaturesAsync(provider, streaming);
            (await read.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.FeatureNotSupported);
            (await ReadEffectCountAsync()).Should().Be(1,
                "a matching message raised during row execution is not a safe revalidation retry");
        }
        finally
        {
            await _fixture.ExecuteAsync($"DROP OWNED BY {role}; DROP ROLE {role};");
        }
    }

    private NpgsqlConnectionStringBuilder NativeAutoPrepareSettings() => new(_fixture.ConnectionString)
    {
        MaxAutoPrepare = 10,
        AutoPrepareMinUsages = 2,
        MaxPoolSize = 1,
        ApplicationName = _schema + "_native_prepare"
    };

    private async Task AssertNativeAutoPreparedAsync(string connectionString, string? expectedRole = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        if (expectedRole is not null)
        {
            await using var identity = new NpgsqlCommand("SELECT current_user", connection);
            (await identity.ExecuteScalarAsync()).Should().Be(expectedRole,
                "pooled executions must retain the constrained RLS identity");
        }
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM pg_prepared_statements
            WHERE position('__honua_native_attribute_' in statement) > 0
              AND position(@schema in statement) > 0
            """, connection);
        command.Parameters.AddWithValue("schema", _schema);
        ((long)(await command.ExecuteScalarAsync())!).Should().BeGreaterThan(0,
            "this regression requires a real auto-prepared native projection");
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

    private static void AssertNativeAttributeOracle(List<Feature> features,
        List<Dictionary<string, object?>> expected)
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
