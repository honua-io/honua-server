// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Crs;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[Collection("Database")]
public sealed class DatumTransformIdentityTests(PostgresFixture fixture)
{
    [IntegrationTheory]
    [InlineData(4269, 4326, true)]
    [InlineData(4326, 4269, false)]
    public async Task NullTransformation_BothDirections_PreservesOrdinatesAndSetsDestinationSrid(
        int fromSrid, int toSrid, bool forward)
    {
        var selection = new DatumTransformationSelection
        {
            Name = "NAD_1983_To_WGS_1984_1",
            FromSrid = fromSrid,
            ToSrid = toSrid,
            ProjPipeline = "+proj=noop",
            TransformForward = forward
        };
        var expression = DatumTransformSql.BuildTransformExpression(
            "ST_GeomFromEWKT(@geometry)", toSrid, selection);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT ST_AsEWKT({expression})";
        command.Parameters.AddWithValue("geometry", $"SRID={fromSrid};POINT ZM(-100 40 12 7)");

        (await command.ExecuteScalarAsync()).Should().Be($"SRID={toSrid};POINT(-100 40 12 7)");
    }
}
