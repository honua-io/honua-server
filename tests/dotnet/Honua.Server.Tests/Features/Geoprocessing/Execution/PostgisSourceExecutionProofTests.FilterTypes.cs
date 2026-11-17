// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Db.Postgres.Features.Geoprocessing;
using Npgsql;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

public sealed partial class PostgisSourceExecutionProofTests
{
    [Theory]
    [InlineData("updated_at >= '2026-01-02T00:00:00Z'")]
    [InlineData("external_id = '2c9dfe88-1676-4329-a6eb-f5e049891f41'")]
    [InlineData("reading = 9007199254740993.0")]
    [InlineData("reading = 90071992547409930e-1")]
    [InlineData("reading = 9007199254740992.0 + 1.0")]
    public async Task ReadAsync_TypedFilterLiterals_SelectOnlyExactMatches(string where)
    {
        var dataSource = fixture.GetService<NpgsqlDataSource>();
        var table = "source_filter_" + Guid.NewGuid().ToString("N");
        await using var setup = dataSource.CreateCommand($"""
            CREATE TABLE public.{table} (key integer, updated_at timestamptz, external_id uuid,
                reading numeric, geom geometry(Point,4326));
            INSERT INTO public.{table} VALUES
                (1,'2026-01-02T00:00:00Z','2c9dfe88-1676-4329-a6eb-f5e049891f41',9007199254740993,NULL),
                (2,'2026-01-01T00:00:00Z','00000000-0000-0000-0000-000000000000',9007199254740992,NULL);
            """);
        await setup.ExecuteNonQueryAsync();
        try
        {
            var rows = new List<DagSourceFeature>();
            var source = new ExternalPostgisDagSource(dataSource);
            await foreach (var row in source.ReadAsync(new DagSourceRequest { PostgisTable = table, Where = where }))
            {
                rows.Add(row);
            }
            rows.Should().ContainSingle();
            rows[0].Attributes["key"].Should().Be(1);
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand($"DROP TABLE public.{table}");
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
