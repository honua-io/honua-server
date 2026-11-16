// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed partial class PostgresStorageMappedFeatureReaderEncodedFormatsIntegrationTests
{
    private static readonly MetadataV2Field[] ObservedAtField =
        [new MetadataV2Field { Name = "observed_at", Type = MetadataV2FieldType.DateTime }];

    [Fact]
    public async Task QueryDateBinsAsync_CalendarMonth_CountsFilteredRowsPerBoundary()
    {
        await SeedObservationsAsync();
        var reader = CreateReader(additionalFields: ObservedAtField);

        var rows = await reader.QueryDateBinsAsync(
            1,
            new FeatureQuery { Where = "population > 1000" },
            new DateBinDefinition { BinField = "observed_at", CalendarUnit = "month" });

        rows.Select(row => (row["boundary"], row["count"])).Should().Equal(
            (new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), 2L),
            (new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero), 1L));
    }

    [Fact]
    public async Task QueryDateBinsAsync_SourceSessionOutsideUtc_KeepsCalendarBoundariesInUtc()
    {
        await SeedObservationsAsync();
        var newYorkSession = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Timezone = "America/New_York" };
        var reader = CreateReader(additionalFields: ObservedAtField, connectionString: newYorkSession.ConnectionString);

        var rows = await reader.QueryDateBinsAsync(
            1,
            new FeatureQuery(),
            new DateBinDefinition { BinField = "observed_at", CalendarUnit = "month" });

        rows.Select(row => row["boundary"]).Should().Equal(
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task QueryDateBinsAsync_FixedIntervalWithOffsetOrigin_AggregatesStatisticsFromOrigin()
    {
        await SeedObservationsAsync();
        var reader = CreateReader(additionalFields: ObservedAtField);
        var origin = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.FromHours(10));

        var rows = await reader.QueryDateBinsAsync(
            1,
            new FeatureQuery(),
            new DateBinDefinition
            {
                BinField = "observed_at",
                FixedInterval = TimeSpan.FromDays(30),
                FixedIntervalOrigin = origin,
                OutStatistics = ImmutableArray.Create(new StatisticDefinition
                {
                    StatisticType = StatisticType.Sum,
                    OnStatisticField = "population",
                    OutStatisticFieldName = "total_population"
                })
            });

        rows.Select(row => (row["boundary"], row["total_population"])).Should().Equal(
            (origin.ToUniversalTime(), 376000d),
            (origin.ToUniversalTime().AddDays(30), 500d),
            (origin.ToUniversalTime().AddDays(60), 9000d));
    }

    [Fact]
    public async Task QueryDateBinsAsync_EpochMillisecondJsonAttribute_BinsLikeTimestampColumn()
    {
        await SeedObservationsAsync();
        await _fixture.ExecuteAsync($"""
            UPDATE {_schema}.cities
            SET attributes = coalesce(attributes, jsonb_build_object())
                || jsonb_build_object('observed_ms', (extract(epoch FROM observed_at) * 1000)::bigint::text);
            """);
        var reader = CreateReader(
            attributesColumn: "attributes",
            additionalFields: [new MetadataV2Field { Name = "observed_ms", Type = MetadataV2FieldType.DateTime }]);

        var rows = await reader.QueryDateBinsAsync(
            1,
            new FeatureQuery(),
            new DateBinDefinition { BinField = "observed_ms", CalendarUnit = "month" });

        rows.Select(row => (row["boundary"], row["count"])).Should().Equal(
            (new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), 2L),
            (new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero), 1L),
            (new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero), 1L));
    }

    [Fact]
    public async Task QueryDateBinsAsync_TemporalFilter_RestrictsBinnedRows()
    {
        await SeedObservationsAsync();
        var reader = CreateReader(additionalFields: ObservedAtField);

        var rows = await reader.QueryDateBinsAsync(
            1,
            new FeatureQuery
            {
                TemporalFilter = new TemporalFilter
                {
                    PropertyName = "observed_at",
                    PropertyType = TemporalPropertyType.DateTime,
                    Start = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero)
                }
            },
            new DateBinDefinition { BinField = "observed_at", CalendarUnit = "month" });

        rows.Select(row => (row["boundary"], row["count"])).Should().Equal(
            (new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero), 1L),
            (new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero), 1L));
    }

    private Task SeedObservationsAsync() => _fixture.ExecuteAsync($"""
        ALTER TABLE {_schema}.cities ADD COLUMN observed_at timestamptz, ADD COLUMN boundary text DEFAULT 'source column';
        UPDATE {_schema}.cities SET observed_at = '2024-01-15T12:00:00Z' WHERE objectid = 1;
        UPDATE {_schema}.cities SET observed_at = '2024-01-20T12:00:00Z' WHERE objectid = 2;
        INSERT INTO {_schema}.cities (objectid, geom, name, population, observed_at) VALUES
            (3, ST_SetSRID(ST_MakePoint(-155.9969, 19.6400), 4326), 'Kailua-Kona', 500, '2024-02-20T12:00:00Z'),
            (4, ST_SetSRID(ST_MakePoint(-155.0868, 19.7241), 4326), 'Hilo', 9000, '2024-03-31T12:00:00Z');
        """);
}
