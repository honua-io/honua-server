// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed partial class PostgresStorageMappedFeatureReaderEncodedFormatsIntegrationTests
{
    [Theory]
    [InlineData("integer", "population", "1, 2")]
    [InlineData("bigint", "population", "1, 2")]
    [InlineData("numeric", "population + 0.4", "1, 2")]
    [InlineData("boolean", "population = 1", "1, 2")]
    [InlineData("text", "population::text", "1, 2")]
    [InlineData("small_domain", "population", "1, 2")]
    public async Task QueryPageAsync_StaleSmallintHint_PreservesCanonicalResults(
        string physicalType, string conversion, string expected)
    {
        await SeedSmallintBoundariesAsync();
        var reader = CreateReader(smallintHint: true);
        var query = SmallintQuery("=", 1L);
        (await reader.QueryPageAsync(1, query)).Items.Select(feature => feature.Id).Should().Equal(1, 2);
        var type = physicalType == "small_domain" ? $"{_schema}.small_domain" : physicalType;
        await _fixture.ExecuteAsync($"""
            CREATE DOMAIN {_schema}.small_domain AS smallint;
            ALTER TABLE {_schema}.cities ALTER COLUMN population TYPE {type} USING {conversion};
            """);

        var canonical = await CreateReader().QueryPageAsync(1, query);
        var optimized = await reader.QueryPageAsync(1, query);
        optimized.Items.Select(feature => feature.Id).Should().Equal(canonical.Items.Select(feature => feature.Id));
        string.Join(", ", optimized.Items.Select(feature => feature.Id)).Should().Be(expected);
        (await reader.QueryPageAsync(1, SmallintQuery("=", 40000L))).Items.Should().BeEmpty();

        var streamed = new List<long>();
        await foreach (var feature in reader.StreamFeaturesAsync(1, query))
        {
            streamed.Add(feature.Id);
        }
        streamed.Should().Equal(canonical.Items.Select(feature => feature.Id));
    }

    [Theory]
    [InlineData("=", 1L)]
    [InlineData("!=", 1L)]
    [InlineData("<>", 1L)]
    [InlineData("<", -32768L)]
    [InlineData("<=", -32768L)]
    [InlineData(">", 32767L)]
    [InlineData(">=", 32767L)]
    [InlineData("<", long.MinValue)]
    [InlineData(">", long.MaxValue)]
    public async Task QueryPageAsync_SmallintComparison_MatchesDeclaredIntegerSemantics(string op, long value)
    {
        await SeedSmallintBoundariesAsync();
        var query = SmallintQuery(op, value);
        var canonical = await CreateReader().QueryPageAsync(1, query);
        var optimized = await CreateReader(smallintHint: true).QueryPageAsync(1, query);
        optimized.Items.Select(feature => feature.Id).Should().Equal(canonical.Items.Select(feature => feature.Id));
    }

    [Theory]
    [InlineData("NULLIF(\"attributes\" ->> 'population', '')::integer + NULLIF(\"attributes\" ->> 'population', '')::integer = @p0", 65534)]
    [InlineData("(NULLIF(\"attributes\" ->> 'population', '')::integer)::bigint = @p0", 32767)]
    [InlineData("NULLIF(\"attributes\" ->> 'population', '')::integer = @p0 OR name = 'missing'", 32767)]
    [InlineData("NULLIF(\"attributes\" ->> 'population', '')::integer IN (@p0)", 32767)]
    [InlineData("NULLIF(\"attributes\" ->> 'population', '')::integer BETWEEN @p0 AND 32767", 32767)]
    public async Task QueryPageAsync_NonAtomicSmallintExpression_RetainsCanonicalSql(string filter, int value)
    {
        await SeedSmallintBoundariesAsync();
        var query = new FeatureQuery { SqlFilter = new SqlFragment(filter, [value]) };
        var reader = CreateReader(smallintHint: true);
        var sql = BuildFeatureSql(reader, query);
        sql.Should().NotContain("pg_typeof").And.Contain("::integer");
        var canonical = await CreateReader().QueryPageAsync(1, query);
        var optimized = await reader.QueryPageAsync(1, query);
        optimized.Items.Select(feature => feature.Id).Should().Equal(canonical.Items.Select(feature => feature.Id));
    }

    [Fact]
    public async Task QueryPageAsync_StaleSmallintHint_PreservesDeclaredIntegerOverflow()
    {
        await SeedSmallintBoundariesAsync();
        await _fixture.ExecuteAsync($"ALTER TABLE {_schema}.cities ALTER COLUMN population TYPE bigint; UPDATE {_schema}.cities SET population = 2147483648 WHERE objectid = 1;");
        var query = SmallintQuery("=", 2147483648L);
        foreach (var hinted in new[] { false, true })
        {
            var read = () => CreateReader(smallintHint: hinted).QueryPageAsync(1, query);
            (await read.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.NumericValueOutOfRange);
        }
    }

    [Theory]
    [InlineData("integer", "population")]
    [InlineData("boolean", "population = 1")]
    public async Task QueryPageAsync_AutoPreparedSmallintBatch_HandlesTypeChange(string physicalType, string conversion)
    {
        await SeedSmallintBoundariesAsync();
        var settings = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            MaxAutoPrepare = 10,
            AutoPrepareMinUsages = 2,
            MaxPoolSize = 1,
            ApplicationName = _schema + "_prepare"
        };
        var reader = CreateReader(smallintHint: true, connectionString: settings.ConnectionString);
        var query = SmallintQuery("=", 1L);
        for (var i = 0; i < 6; i++)
        {
            (await reader.QueryPageAsync(1, query)).Items.Select(feature => feature.Id).Should().Equal(1, 2);
        }
        await _fixture.ExecuteAsync($"ALTER TABLE {_schema}.cities ALTER COLUMN population TYPE {physicalType} USING {conversion};");
        var canonical = await CreateReader().QueryPageAsync(1, query);
        for (var i = 0; i < 4; i++)
        {
            (await reader.QueryPageAsync(1, query)).Items.Select(feature => feature.Id)
                .Should().Equal(canonical.Items.Select(feature => feature.Id));
        }
    }

    [Fact]
    public async Task QueryPageAsync_StaleSmallintHint_DoesNotRepeatVolatileRowSecurity()
    {
        await SeedSmallintBoundariesAsync();
        var role = "smallint_reader_" + Guid.NewGuid().ToString("N");
        await _fixture.ExecuteAsync($"""
            CREATE ROLE {role};
            GRANT USAGE ON SCHEMA {_schema} TO {role};
            GRANT SELECT ON {_schema}.cities TO {role};
            CREATE SEQUENCE {_schema}.read_effects;
            GRANT USAGE, SELECT ON {_schema}.read_effects TO {role};
            ALTER TABLE {_schema}.cities ENABLE ROW LEVEL SECURITY;
            CREATE POLICY counted ON {_schema}.cities USING (nextval('{_schema}.read_effects') > 0);
            """);
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
            {
                Options = "-c role=" + role,
                Pooling = false
            };
            var query = SmallintQuery("=", 1L);
            // For stale types the only executed policy must belong to the canonical retry.
            // A valid smallint plan can legitimately visit fewer rows via predicate pushdown.
            foreach (var physicalType in new[] { "numeric", "boolean" })
            {
                var conversion = physicalType == "boolean" ? "population = 1" : "population";
                await _fixture.ExecuteAsync($"ALTER TABLE {_schema}.cities ALTER COLUMN population TYPE {physicalType} USING {conversion};");
                await _fixture.ExecuteAsync($"ALTER SEQUENCE {_schema}.read_effects RESTART WITH 1;");
                var canonical = await CreateReader(connectionString: settings.ConnectionString).QueryPageAsync(1, query);
                var canonicalEffects = await ReadEffectCountAsync();
                await _fixture.ExecuteAsync($"ALTER SEQUENCE {_schema}.read_effects RESTART WITH 1;");
                var result = await CreateReader(smallintHint: true, connectionString: settings.ConnectionString).QueryPageAsync(1, query);
                result.Items.Select(feature => feature.Id).Should().Equal(canonical.Items.Select(feature => feature.Id));
                (await ReadEffectCountAsync()).Should().Be(canonicalEffects,
                    "the discarded statement must not execute a volatile row-security policy");
            }
        }
        finally
        {
            await _fixture.ExecuteAsync($"DROP OWNED BY {role}; DROP ROLE {role};");
        }
    }

    [Fact]
    public async Task QueryPageAsync_CustomOperatorSearchPath_UsesCanonicalIntegerOperator()
    {
        await SeedSmallintBoundariesAsync();
        await _fixture.ExecuteAsync($"""
            CREATE FUNCTION {_schema}.never_equal(smallint, bigint) RETURNS boolean
                LANGUAGE sql IMMUTABLE AS 'SELECT false';
            CREATE OPERATOR {_schema}.= (FUNCTION = {_schema}.never_equal, LEFTARG = smallint, RIGHTARG = bigint);
            """);
        var settings = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            SearchPath = _schema + ",pg_catalog,public",
            Pooling = false
        };
        var reader = CreateReader(smallintHint: true, connectionString: settings.ConnectionString);
        (await reader.QueryPageAsync(1, SmallintQuery("=", 1L))).Items.Select(feature => feature.Id).Should().Equal(1, 2);
    }

    [Fact]
    public async Task QueryPageAsync_AmbientTransactionAndStaleBooleanHint_RemainsUsable()
    {
        await SeedSmallintBoundariesAsync();
        await _fixture.ExecuteAsync($"ALTER TABLE {_schema}.cities ALTER COLUMN population TYPE boolean USING population = 1;");
        using var scope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
        var reader = CreateReader(smallintHint: true);
        (await reader.QueryPageAsync(1, SmallintQuery("=", 1L))).Items.Select(feature => feature.Id).Should().Equal(1, 2);
        (await reader.QueryPageAsync(1, SmallintQuery("=", 40000L))).Items.Should().BeEmpty();
        scope.Complete();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SmallintBatch_TypeCheckHoldsRelationLockUntilBatchFinishes(bool prepared)
    {
        await SeedSmallintBoundariesAsync();
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        // A pooled backend can retain pg_temp after earlier tests drop their temp
        // tables. Exercise that history, then explicitly satisfy the catalog-first
        // guard so this test reaches the true-verdict relation-lock path.
        await using (var setup = new NpgsqlCommand("""
            CREATE TEMP TABLE smallint_lock_guard_probe (id integer);
            DROP TABLE smallint_lock_guard_probe;
            SET search_path = pg_catalog, public, pg_temp;
            """, connection))
        {
            await setup.ExecuteNonQueryAsync();
        }

        await using var blocker = await _fixture.DataSource.OpenConnectionAsync();
        await using var ddl = await _fixture.DataSource.OpenConnectionAsync();
        var key = Random.Shared.NextInt64(1, long.MaxValue);
        await using var acquire = new NpgsqlCommand("SELECT pg_advisory_lock($1)", blocker);
        acquire.Parameters.AddWithValue(key);
        await acquire.ExecuteNonQueryAsync();
        var store = CreateReader(smallintHint: true);
        var sql = typeof(PostgresStorageMappedFeatureReader)
                .GetMethod("BuildFeatureSelect", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(store, [SmallintQuery("=", 1L), true])!;
        await using var batch = (NpgsqlBatch)typeof(PostgresStorageMappedFeatureReader)
            .GetMethod("CreateSmallintReadBatch", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(store, [connection, sql, false])!;
        NpgsqlDataReader? result = null;
        Task<NpgsqlDataReader>? pendingReader = null;
        try
        {
            // Pause between the real verification and feature statements. This proves
            // that verification itself locks the source, including cached plans.
            // Observe the server-side pause independently of result buffering: PG16
            // can withhold even a padded verdict until the blocked statement finishes.
            var wait = new NpgsqlBatchCommand("SELECT pg_advisory_xact_lock($1)");
            wait.Parameters.AddWithValue(key);
            batch.BatchCommands.Insert(1, wait);
            if (prepared)
            {
                await batch.PrepareAsync();
            }
            pendingReader = batch.ExecuteReaderAsync();
            await using (var observe = new NpgsqlCommand("""
                SELECT EXISTS(SELECT FROM pg_stat_activity
                    WHERE pid = $1 AND wait_event_type = 'Lock' AND wait_event = 'advisory')
                """, ddl))
            {
                observe.Parameters.AddWithValue(connection.ProcessID);
                var elapsed = Stopwatch.StartNew();
                while (!(bool)(await observe.ExecuteScalarAsync())!)
                {
                    elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
                        "the batch must reach the advisory pause before testing its relation lock");
                    await Task.Delay(20);
                }
            }
            await using (var timeout = new NpgsqlCommand("SET lock_timeout = '200ms'", ddl))
            {
                await timeout.ExecuteNonQueryAsync();
            }
            await using var change = new NpgsqlCommand(
                $"ALTER TABLE {_schema}.cities ALTER COLUMN population TYPE integer", ddl);
            var blockedDdl = () => change.ExecuteNonQueryAsync();
            (await blockedDdl.Should().ThrowAsync<PostgresException>()).Which.SqlState
                .Should().Be(PostgresErrorCodes.LockNotAvailable);
            await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock_all()", blocker))
            {
                await release.ExecuteNonQueryAsync();
            }
            result = await pendingReader;
            (await result.ReadAsync()).Should().BeTrue();
            result.GetBoolean(0).Should().BeTrue();
            (await result.NextResultAsync()).Should().BeTrue();
            (await result.NextResultAsync()).Should().BeTrue();
            var ids = new List<long>();
            while (await result.ReadAsync())
            {
                ids.Add(result.GetInt64(0));
            }
            ids.Should().Equal(1, 2);
            (await result.NextResultAsync()).Should().BeFalse();
            await change.ExecuteNonQueryAsync();
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock_all()", blocker);
            await release.ExecuteNonQueryAsync();
            if (result is null && pendingReader is not null)
            {
                result = await pendingReader;
            }
            if (result is not null)
            {
                await result.DisposeAsync();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryPageAsync_SmallintBatchWithSerialPlanner_PreservesResultsAndSession(bool staleBoolean)
    {
        await SeedSmallintBoundariesAsync();
        await _fixture.ExecuteAsync($"UPDATE {_schema}.cities SET geom = ST_SetSRID(ST_MakePoint(0, 0), 4326);");
        if (staleBoolean)
        {
            await _fixture.ExecuteAsync($"ALTER TABLE {_schema}.cities ALTER COLUMN population TYPE boolean USING population = 1;");
        }
        var settings = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            MaxPoolSize = 1,
            NoResetOnClose = true,
            ApplicationName = _schema + "_serial"
        };
        await using (var setup = new NpgsqlConnection(settings.ConnectionString))
        {
            await setup.OpenAsync();
            await using var setting = new NpgsqlCommand("SET max_parallel_workers_per_gather = 2", setup);
            await setting.ExecuteNonQueryAsync();
        }
        var query = SmallintQuery("=", 1L) with
        {
            SpatialFilter = new SpatialFilter
            {
                Geometry = [],
                Srid = 4326,
                SpatialRelationship = SpatialRelationship.Intersects,
                IsSimpleEnvelope = true,
                EnvelopeMinX = -2,
                EnvelopeMinY = -2,
                EnvelopeMaxX = 2,
                EnvelopeMaxY = 2
            }
        };
        var reader = CreateReader(smallintHint: true, connectionString: settings.ConnectionString, preferSerialPlan: true);
        (await reader.QueryPageAsync(1, query)).Items.Select(feature => feature.Id).Should().Equal(1, 2);
        await using var check = new NpgsqlConnection(settings.ConnectionString);
        await check.OpenAsync();
        await using var command = new NpgsqlCommand("SHOW max_parallel_workers_per_gather", check);
        (await command.ExecuteScalarAsync()).Should().Be("2");
    }

    [Fact]
    public async Task QueryPageAsync_EmptySmallintSource_ReturnsEmptyPageAndStream()
    {
        await SeedSmallintBoundariesAsync();
        await _fixture.ExecuteAsync($"TRUNCATE {_schema}.cities;");
        var reader = CreateReader(smallintHint: true);
        var query = SmallintQuery("=", 1L);
        (await reader.QueryPageAsync(1, query)).Items.Should().BeEmpty();
        await using var stream = reader.StreamFeaturesAsync(1, query).GetAsyncEnumerator();
        (await stream.MoveNextAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task QueryPageAsync_BorrowedTransactionAndStaleSmallintHint_DoesNotAbortTransaction()
    {
        await SeedSmallintBoundariesAsync();
        await _fixture.ExecuteAsync($"ALTER TABLE {_schema}.cities ALTER COLUMN population TYPE boolean USING population = 1;");
        var provider = new FixtureConnectionProvider(_fixture.ConnectionString);
        var reader = CreateReader(smallintHint: true, connectionProvider: provider);
        await PostgresMutationTransaction.ExecuteAsync(provider, async () =>
        {
            await using var lease = await provider.OpenNpgsqlConnectionAsync();
            lease.Transaction.Should().NotBeNull();
            (await reader.QueryPageAsync(1, SmallintQuery("=", 1L))).Items.Select(feature => feature.Id).Should().Equal(1, 2);
            await using var command = new NpgsqlCommand("SELECT 1", lease);
            (await command.ExecuteScalarAsync()).Should().Be(1);
            return true;
        }, _ => true, CancellationToken.None);
    }

    private async Task<long> ReadEffectCountAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT CASE WHEN is_called THEN last_value ELSE 0 END FROM {_schema}.read_effects", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private Task SeedSmallintBoundariesAsync() => _fixture.ExecuteAsync($"""
        TRUNCATE {_schema}.cities;
        ALTER TABLE {_schema}.cities ALTER COLUMN population TYPE smallint;
        INSERT INTO {_schema}.cities (objectid, population) VALUES
            (1, 1), (2, 1), (3, -32768), (4, 32767), (5, NULL);
        """);

    private static FeatureQuery SmallintQuery(string op, long value) => new()
    {
        Limit = 10,
        SqlFilter = new SqlFragment($"NULLIF(\"attributes\" ->> 'population', '')::integer {op} @p0", [value])
    };

    private static string BuildFeatureSql(PostgresStorageMappedFeatureReader reader, FeatureQuery query)
        => typeof(PostgresStorageMappedFeatureReader)
            .GetMethod("BuildFeatureSelect", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(reader, [query, true])!.ToString()!;
}
