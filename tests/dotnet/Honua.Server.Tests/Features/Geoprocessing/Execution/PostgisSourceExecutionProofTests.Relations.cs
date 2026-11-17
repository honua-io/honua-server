// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Db.Postgres.Features.Geoprocessing;
using Npgsql;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

public sealed partial class PostgisSourceExecutionProofTests
{
    [IntegrationTest]
    public async Task ReadAsync_FilterOnMaterializedView_SelectsMatchingRows()
    {
        var dataSource = fixture.GetService<NpgsqlDataSource>();
        var table = "source_relation_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(dataSource, $"""
            CREATE TABLE public.{table} (key integer, status text, geom geometry(Point,4326));
            INSERT INTO public.{table} VALUES
                (1,'active',ST_SetSRID(ST_Point(1,1),4326)),
                (2,'retired',ST_SetSRID(ST_Point(2,2),4326));
            CREATE MATERIALIZED VIEW public.{table}_mv AS SELECT * FROM public.{table};
            """);
        try
        {
            var rows = await ReadAllAsync(dataSource, table + "_mv", "status = 'active'");

            rows.Should().ContainSingle();
            rows[0].Attributes["key"].Should().Be(1);
        }
        finally
        {
            await ExecuteAsync(dataSource, $"DROP MATERIALIZED VIEW public.{table}_mv; DROP TABLE public.{table}");
        }
    }

    [IntegrationTest]
    public async Task ReadAsync_FilterOnCaseAmbiguousColumn_IsRefused()
    {
        var dataSource = fixture.GetService<NpgsqlDataSource>();
        var table = "source_relation_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(dataSource, $"""
            CREATE TABLE public.{table} ("Name" text, name text, geom geometry(Point,4326));
            INSERT INTO public.{table} VALUES ('x','y',ST_SetSRID(ST_Point(1,1),4326));
            """);
        try
        {
            var read = () => ReadAllAsync(dataSource, table, "name = 'x'");

            await read.Should().ThrowAsync<ArgumentException>();
        }
        finally
        {
            await ExecuteAsync(dataSource, $"DROP TABLE public.{table}");
        }
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<DagSourceFeature>> ReadAllAsync(NpgsqlDataSource dataSource, string table, string where)
    {
        var rows = new List<DagSourceFeature>();
        var source = new ExternalPostgisDagSource(dataSource);
        await foreach (var row in source.ReadAsync(new DagSourceRequest { PostgisTable = table, Where = where }))
        {
            rows.Add(row);
        }
        return rows;
    }
}
