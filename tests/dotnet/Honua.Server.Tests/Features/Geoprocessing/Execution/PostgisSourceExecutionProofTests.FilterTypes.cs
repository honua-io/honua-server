// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Db.Postgres.Features.Geoprocessing;
using Npgsql;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

[Protocol(TestProtocols.Infrastructure)]
public sealed partial class PostgisSourceExecutionProofTests
{
    [IntegrationTheory]
    [Operation(Operations.SecurityTesting)]
    [InlineData("LEFT(name, 1) = 'f'")]
    [InlineData("RIGHT(name, 1) = 'o'")]
    [InlineData("LEN(name) = 3 AND key = 1")]
    [InlineData("CONTAINS(name, 'oo')")]
    [InlineData("STARTSWITH(name, 'f')")]
    [InlineData("ENDSWITH(name, 'o')")]
    [InlineData("IIF(key = 1, 1, 0) = 1")]
    [InlineData("SIGN(key) = 1 AND key = 1")]
    [InlineData("ST_X(geom) > 0")]
    [InlineData("ST_Y(geom) > 0")]
    [InlineData("ST_CONTAINS(geom, geom) AND key = 1")]
    [InlineData("ST_WITHIN(geom, geom) AND key = 1")]
    [InlineData("ST_INTERSECTS(geom, geom) AND key = 1")]
    [InlineData("NOT ST_DISJOINT(geom, geom) AND key = 1")]
    [InlineData("NOT ST_TOUCHES(geom, geom) AND key = 1")]
    [InlineData("NOT ST_CROSSES(geom, geom) AND key = 1")]
    [InlineData("NOT ST_OVERLAPS(geom, geom) AND key = 1")]
    [InlineData("ST_EQUALS(geom, geom) AND key = 1")]
    [InlineData("updated_at >= '2026-01-02T00:00:00Z'")]
    [InlineData("external_id = '2c9dfe88-1676-4329-a6eb-f5e049891f41'")]
    [InlineData("CONCAT(name, '-x') = 'foo-x'")]
    [InlineData("name || '-x' = 'foo-x'")]
    [InlineData("ROUND(fraction, 2) = 1.23")]
    [InlineData("reading = 9007199254740993.0")]
    [InlineData("reading = 90071992547409930e-1")]
    [InlineData("reading = 9007199254740992.0 + 1.0")]
    public async Task ReadAsync_TypedFilterLiterals_SelectOnlyExactMatches(string where)
    {
        var dataSource = fixture.GetService<NpgsqlDataSource>();
        var table = "source_filter_" + Guid.NewGuid().ToString("N");
        await using var setup = dataSource.CreateCommand($"""
            CREATE TABLE public.{table} (key integer, updated_at timestamptz, external_id uuid,
                reading numeric, name text, fraction numeric, geom geometry(Point,4326));
            INSERT INTO public.{table} VALUES
                (1,'2026-01-02T00:00:00Z','2c9dfe88-1676-4329-a6eb-f5e049891f41',9007199254740993,'foo',1.234,ST_SetSRID(ST_Point(1,1),4326)),
                (2,'2026-01-01T00:00:00Z','00000000-0000-0000-0000-000000000000',9007199254740992,'bar',1.244,ST_SetSRID(ST_Point(-1,-1),4326));
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
