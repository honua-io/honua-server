// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Geometry;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.Topology;

[Collection("Database")]
public sealed class PostgresGeometryTopologyValidatorTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task ValidateTopologies_OneConnectionPreservesOrderedResultsAcrossChunks()
    {
        await using var setup = new NpgsqlConnection(fixture.ConnectionString);
        await setup.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT ST_AsBinary(ST_GeomFromText('POLYGON((0 0,2 0,2 2,0 2,0 0))')),
                   ST_AsBinary(ST_GeomFromText('POLYGON((0 0,2 2,2 0,0 2,0 0))'))
            """, setup);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var valid = reader.GetFieldValue<byte[]>(0);
        var invalid = reader.GetFieldValue<byte[]>(1);

        var opened = 0;
        var provider = Substitute.For<IAdoNetDatabaseConnectionProvider>();
        provider.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            opened++;
            var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync(call.Arg<CancellationToken>());
            return (DbConnection)connection;
        });
        var validator = new PostgresGeometryTopologyValidator(provider);
        var wkbs = Enumerable.Range(0, 300).Select(index => index % 2 == 0 ? valid : invalid).ToArray();

        var results = await validator.ValidateTopologiesAsync(wkbs);

        Assert.Equal(300, results.Count);
        Assert.Equal(1, opened);
        for (var index = 0; index < results.Count; index++)
        {
            Assert.Equal(index % 2 == 0, results[index].IsValid);
        }
    }
}
