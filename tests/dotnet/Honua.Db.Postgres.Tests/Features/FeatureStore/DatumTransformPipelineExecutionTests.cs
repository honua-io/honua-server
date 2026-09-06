// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Crs;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[Collection("Database")]
[Protocol(ProtocolNames.TestQuality)]
[Operation(Operations.Project)]
public sealed class DatumTransformPipelineExecutionTests(PostgresFixture fixture)
{
    [IntegrationTheory]
    [InlineData(true, -98, 37, 17)]
    [InlineData(false, -102, 43, 7)]
    public async Task ExplicitPipeline_AppliesOperationAndDirection_PreservingMeasureAndSrid(
        bool forward, double expectedX, double expectedY, double expectedZ)
    {
        // Independent arithmetic oracle: forward adds (2,-3,5), inverse subtracts it.
        // M is not part of the coordinate operation and must remain 7.
        var selection = new DatumTransformationSelection
        {
            Name = "fixture-affine",
            FromSrid = 4267,
            ToSrid = 4269,
            ProjPipeline = "+proj=pipeline +step +proj=affine +xoff=2 +yoff=-3 +zoff=5",
            TransformForward = forward
        };
        var expression = DatumTransformSql.BuildTransformExpression(
            "ST_GeomFromEWKT(@geometry)", 4269, selection);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT ST_X(g), ST_Y(g), ST_Z(g), ST_M(g), ST_SRID(g) FROM (SELECT {expression} AS g) AS transformed";
        command.Parameters.AddWithValue("geometry", "SRID=4267;POINT ZM(-100 40 12 7)");
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetDouble(0).Should().Be(expectedX);
        reader.GetDouble(1).Should().Be(expectedY);
        reader.GetDouble(2).Should().Be(expectedZ);
        reader.GetDouble(3).Should().Be(7);
        reader.GetInt32(4).Should().Be(4269);
    }
}
