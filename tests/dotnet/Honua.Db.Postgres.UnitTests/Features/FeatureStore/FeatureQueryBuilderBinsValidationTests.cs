// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureQueryBuilderBinsValidationTests
{
    private static FeatureQueryBuilder CreateBuilder()
        => new(new DefaultObjectPoolProvider().CreateStringBuilderPool(), new GeometryProcessor());

    [UnitTest]
    public void BuildBinsQuery_FixedIntervalWithInvalidBoundsOrSize_ThrowsArgumentOutOfRange()
    {
        var builder = CreateBuilder();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.BuildBinsQuery(1, new FeatureQuery(), new BinDefinition
        {
            Type = BinType.FixedInterval,
            Field = "price",
            IntervalStart = 10,
            IntervalEnd = 10,
            IntervalSize = 5
        }));

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.BuildBinsQuery(1, new FeatureQuery(), new BinDefinition
        {
            Type = BinType.FixedInterval,
            Field = "price",
            IntervalStart = 0,
            IntervalEnd = 100,
            IntervalSize = 0
        }));

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.BuildBinsQuery(1, new FeatureQuery(), new BinDefinition
        {
            Type = BinType.FixedInterval,
            Field = "price",
            IntervalStart = double.NaN,
            IntervalEnd = 100,
            IntervalSize = 10
        }));
    }

    [UnitTest]
    public void BuildBinsQuery_AutoIntervalWithNonPositiveNumBins_ThrowsArgumentOutOfRange()
    {
        var builder = CreateBuilder();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.BuildBinsQuery(1, new FeatureQuery(), new BinDefinition
        {
            Type = BinType.AutoInterval,
            Field = "price",
            NumBins = 0
        }));
    }

    [UnitTest]
    public void BuildBinsQuery_AutoInterval_UsesNullIfForEmptyJsonbStrings()
    {
        var builder = CreateBuilder();

        var result = builder.BuildBinsQuery(1, new FeatureQuery(), new BinDefinition
        {
            Type = BinType.AutoInterval,
            Field = "price",
            NumBins = 5
        });

        result.Sql.Should().Contain("NULLIF((attributes->>'price')::text, '')::numeric");
    }

    [UnitTest]
    public async Task CachedStatement_DisposedBeforeClone_ReturnsNullWithoutThrowing()
    {
        using var connection = new NpgsqlConnection();
        using var template = new NpgsqlCommand("SELECT 1", connection);
        var cached = new PreparedStatementCache.CachedStatement
        {
            StatementName = "stmt_test",
            Command = template
        };

        cached.Dispose();

        var cloned = cached.TryCloneCommand(connection);
        cloned.Should().BeNull();

        var executionCommand = await PreparedStatementCache.CreatePreparedExecutionCommandAsync(
            cached,
            connection,
            configureParameters: null,
            metrics: null,
            CancellationToken.None);
        executionCommand.Should().BeNull();
    }

    [UnitTest]
    public async Task CreatePreparedExecutionCommandAsync_WhenConfigureParametersThrows_DisposesClonedCommandAndRethrows()
    {
        using var connection = new NpgsqlConnection();
        using var template = new NpgsqlCommand("SELECT 1", connection);
        using var cached = new PreparedStatementCache.CachedStatement
        {
            StatementName = "stmt_throw",
            Command = template
        };

        var act = () => PreparedStatementCache.CreatePreparedExecutionCommandAsync(
            cached,
            connection,
            _ => throw new InvalidOperationException("parameter failure"),
            metrics: null,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("parameter failure");
    }

    [UnitTest]
    public void BuildSelectRawGeoJsonQuery_WithMaskedFields_StripsKeysCaseInsensitively()
    {
        var builder = CreateBuilder();
        var query = new FeatureQuery
        {
            EnforcedMaskedFields = ["SecretField"],
            PublicIdAttributeName = "external_id"
        };

        var result = builder.BuildSelectRawGeoJsonQuery(1, query);

        result.Sql.Should().Contain("jsonb_object_agg(masked_attr.key, masked_attr.value)");
        result.Sql.Should().Contain("UPPER(masked_attr.key) = ANY($");
        result.WhereParameters.OfType<string[]>().Should().ContainSingle(arr =>
            arr.SequenceEqual(new[] { "SECRETFIELD" }));
    }

    [UnitTest]
    public void BuildBinsQuery_FixedBoundariesWithInvalidValues_Throws()
    {
        var builder = CreateBuilder();

        Assert.Throws<ArgumentException>(() => builder.BuildBinsQuery(1, new FeatureQuery(), FixedBoundaries(null)));
        Assert.Throws<ArgumentException>(() => builder.BuildBinsQuery(1, new FeatureQuery(), FixedBoundaries([10.0])));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.BuildBinsQuery(1, new FeatureQuery(), FixedBoundaries([0.0, double.NaN, 10.0])));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.BuildBinsQuery(1, new FeatureQuery(), FixedBoundaries([0.0, double.PositiveInfinity])));
        Assert.Throws<ArgumentException>(() => builder.BuildBinsQuery(1, new FeatureQuery(), FixedBoundaries([0.0, 10.0, 10.0])));
        Assert.Throws<ArgumentException>(() => builder.BuildBinsQuery(1, new FeatureQuery(), FixedBoundaries([10.0, 5.0])));
    }

    private static BinDefinition FixedBoundaries(double[]? boundaries) => new()
    {
        Type = BinType.FixedBoundaries,
        Field = "price",
        Boundaries = boundaries is null ? null : [.. boundaries]
    };

    [UnitTest]
    public void BuildBinsAndDateBinsQueries_InvalidStatisticAlias_ThrowsArgumentException()
    {
        var builder = CreateBuilder();
        var invalidStat = new StatisticDefinition
        {
            StatisticType = StatisticType.Sum,
            OnStatisticField = "price",
            OutStatisticFieldName = "bad;alias"
        };

        Assert.Throws<ArgumentException>(() => builder.BuildBinsQuery(1, new FeatureQuery(), new BinDefinition
        {
            Type = BinType.FixedInterval,
            Field = "price",
            IntervalStart = 0,
            IntervalEnd = 100,
            IntervalSize = 10,
            OutStatistics = [invalidStat]
        }));

        Assert.Throws<ArgumentException>(() => builder.BuildDateBinsQuery(1, new FeatureQuery(), new DateBinDefinition
        {
            BinField = "observed_at",
            CalendarUnit = "day",
            OutStatistics = [invalidStat]
        }));
    }

    [UnitTheory]
    [InlineData("selected_col = 1", null)]
    [InlineData("status = 'DROP TABLE features'", null)]
    [InlineData("status = 'it''s SELECT' AND count > 5", null)]
    [InlineData("selected_col = 1 AND SELECT 2", "SELECT")]
    [InlineData("name = 'ok' -- trailing comment", "--")]
    [InlineData("name = 'ok'; DROP TABLE features", ";")]
    [InlineData("x = 1 AND EXECUTE foo", "EXECUTE")]
    public void FindDangerousPattern_DetectsUnquotedPatternsWithWordBoundaries(string whereClause, string? expectedPattern)
    {
        var found = FeatureQueryBuilder.FindDangerousPattern(whereClause);

        found.Should().Be(expectedPattern);
    }

    [UnitTheory]
    [InlineData("SELECT objectid FROM features WHERE layer_id = $1")]
    [InlineData("SELECT objectid, geometry, attributes FROM features WHERE layer_id = $1 AND attributes->>'long_filter_payload_value' = $2 AND attributes->>'second_filter_payload_value' = $3 AND attributes->>'third_filter_payload_value' = $4 AND attributes->>'fourth_filter_payload_value' = $5 AND attributes->>'fifth_filter_payload_value' = $6 AND attributes->>'sixth_filter_payload_value' = $7 AND attributes->>'seventh_filter_payload_value' = $8 AND attributes->>'eighth_filter_payload_value' = $9 AND attributes->>'ninth_filter_payload_value' = $10")]
    public void StatementHash_Compute_MatchesSha256HexString(string sql)
    {
        var hash = PreparedStatementCache.StatementHash.Compute(sql);
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));

        hash.ToHexString().Should().Be(expected);
        PreparedStatementCache.StatementHash.Compute(sql).Should().Be(hash);
    }

    [UnitTest]
    public void PreparedStatementCache_RemoveStaleEntry_DoesNotEvictNewerReplacement()
    {
        using var cache = new PreparedStatementCache(
            Options.Create(new Honua.Core.Features.Infrastructure.Domain.QueryCacheOptions()),
            NullLogger<PreparedStatementCache>.Instance);
        using var connection = new NpgsqlConnection();
        using var staleCommand = new NpgsqlCommand("SELECT 1", connection);
        using var freshCommand = new NpgsqlCommand("SELECT 1", connection);
        using var stale = new PreparedStatementCache.CachedStatement { StatementName = "stmt_stale", Command = staleCommand };
        using var fresh = new PreparedStatementCache.CachedStatement { StatementName = "stmt_fresh", Command = freshCommand };

        const int connectionId = 42;
        const string sql = "SELECT 1";

        cache.TryAddCachedStatementForTest(connectionId, sql, fresh).Should().BeTrue();
        cache.RemoveStaleCachedStatementForTest(connectionId, sql, stale);

        using var current = cache.GetCachedStatementForTest(connectionId, sql);
        current.Should().BeSameAs(fresh);
        cache.GetConnectionCountForTest(connectionId).Should().Be(1);
    }
}
