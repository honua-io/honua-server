// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.GeoETL.Domain;
using Honua.Postgres.Features.GeoETL.Services.Connectors;
using Honua.Server.Tests.Infrastructure;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.GeoETL;

/// <summary>
/// Testcontainers integration coverage for the external PostGIS sink (#361 Child Ticket
/// D). Proves the sink creates its destination table in a PostGIS database that is not the
/// Honua catalog and inserts features (with reprojected-or-tagged geometry and a batch id)
/// through the managed Npgsql + WKB path — no GDAL. Uses an isolated schema as the
/// "external" target.
/// </summary>
[Collection("Database")]
public sealed class ExternalPostgisSinkTests : IAsyncLifetime
{
    private readonly DatabaseFixtureAdapter _fixture;
    private readonly ITestOutputHelper _output;
    private string _schemaName = null!;

    public ExternalPostgisSinkTests(DatabaseFixtureAdapter fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _schemaName = await _fixture.CreateIsolatedSchemaAsync(nameof(ExternalPostgisSinkTests));
        _output.WriteLine($"Created isolated schema: {_schemaName}");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropSchemaAsync(_schemaName);
    }

    [Fact]
    public async Task WriteAsync_CreatesTableAndInsertsFeaturesWithBatchTag()
    {
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        var sink = new ExternalPostgisSinkConnector();
        var config = new ConnectorConfig
        {
            Type = ExternalPostgisSinkConnector.ConnectorType,
            Options = new Dictionary<string, string>
            {
                ["connectionString"] = _fixture.ConnectionString,
                ["schema"] = _schemaName,
                ["table"] = "external_out",
                ["targetSrid"] = "4326"
            }
        };

        var features = Features(
            new Feature(factory.CreatePoint(new Coordinate(13.405, 52.52)),
                new AttributesTable { { "name", "berlin" } }),
            new Feature(factory.CreatePoint(new Coordinate(-122.4, 37.6)),
                new AttributesTable { { "name", "sf" } }),
            new Feature(null, new AttributesTable { { "name", "no-geom" } }));

        var result = await sink.WriteAsync(config, features, "batch-ext-1");

        Assert.Equal(2, result.FeaturesWritten);
        Assert.Equal(1, result.FeaturesRejected);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();

        await using var countCommand = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM \"{_schemaName}\".external_out", connection);
        var count = (long)(await countCommand.ExecuteScalarAsync())!;
        Assert.Equal(2, count);

        // Every row carries the run batch id in its attributes JSONB for soft-delete rollback.
        await using var batchCommand = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM \"{_schemaName}\".external_out " +
            "WHERE attributes->>'__pipeline_batch_id' = 'batch-ext-1'", connection);
        var tagged = (long)(await batchCommand.ExecuteScalarAsync())!;
        Assert.Equal(2, tagged);

        // The geometry persisted with the requested SRID.
        await using var sridCommand = new NpgsqlCommand(
            $"SELECT DISTINCT ST_SRID(geom) FROM \"{_schemaName}\".external_out", connection);
        var srid = (int)(await sridCommand.ExecuteScalarAsync())!;
        Assert.Equal(4326, srid);
    }

    private static async IAsyncEnumerable<IFeature> Features(params IFeature[] features)
    {
        foreach (var feature in features)
        {
            yield return feature;
        }

        await Task.CompletedTask;
    }
}
