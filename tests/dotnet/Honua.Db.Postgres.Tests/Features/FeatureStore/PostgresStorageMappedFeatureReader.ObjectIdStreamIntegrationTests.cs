// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed partial class PostgresStorageMappedFeatureReaderEncodedFormatsIntegrationTests
{
    private const int ManyCities = 10_000;

    [Fact]
    public async Task StreamObjectIdsAsync_MoreRowsThanOneFlush_YieldsFilteredIdsInOrder()
    {
        await _fixture.ExecuteAsync($"""
            INSERT INTO {_schema}.cities (objectid, geom, name, population, attributes)
            SELECT g, ST_SetSRID(ST_MakePoint(-150 + g * 0.0001, 20), 4326), 'City ' || g, g, NULL
            FROM generate_series(3, {ManyCities + 2}) AS g;
            """);
        var reader = CreateReader();
        var query = new FeatureQuery
        {
            SqlFilter = new SqlFragment("\"population\" % 2 = @p0", [0]),
            OrderBy = [new OrderByClause("objectid", ascending: false)]
        };

        var streamed = new List<long>();
        await foreach (var objectId in reader.StreamObjectIdsAsync(1, query))
        {
            streamed.Add(objectId);
        }

        var collected = await reader.QueryObjectIdsAsync(1, query);
        streamed.Should().Equal(collected);
        streamed.Should().HaveCountGreaterThan(4_096).And.BeInDescendingOrder();
        streamed.Should().OnlyContain(objectId => objectId % 2 == 0 || objectId == 1);
    }

    [Fact]
    public async Task StreamObjectIdsAsync_ConsumerStopsEarly_ReleasesTheConnection()
    {
        await _fixture.ExecuteAsync($"""
            INSERT INTO {_schema}.cities (objectid, geom, name, population, attributes)
            SELECT g, ST_SetSRID(ST_MakePoint(-150, 20), 4326), 'City ' || g, g, NULL
            FROM generate_series(3, {ManyCities + 2}) AS g;
            """);
        var reader = CreateReader();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await foreach (var _ in reader.StreamObjectIdsAsync(1, new FeatureQuery()))
            {
                break;
            }
        }

        (await reader.QueryObjectIdsAsync(1, new FeatureQuery())).Should().HaveCount(ManyCities + 2);
    }
}
