// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Postgres.Features.Import;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Import;

/// <summary>
/// Integration tests for the OGC API Features collection sink. Exercises real PostgreSQL/PostGIS
/// via the shared Testcontainers fixture so the SQL/PostGIS contract is enforced.
/// </summary>
[Collection("Database")]
public sealed class PostgresOgcApiFeaturesCollectionSinkTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture = new();
    private string? _schema;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _schema = await _fixture.CreateIsolatedSchemaAsync(nameof(PostgresOgcApiFeaturesCollectionSinkTests));
    }

    public async Task DisposeAsync()
    {
        if (_schema != null)
        {
            await _fixture.DropSchemaAsync(_schema);
        }

        await _fixture.DisposeAsync();
    }

    [Fact]
    public async Task EnsureTargetAsync_CreatesTargetTableWithExpectedShape()
    {
        var sink = CreateSink();
        var target = new OgcApiFeaturesSinkTarget
        {
            Schema = _schema!,
            Table = "roads_target",
            CollectionId = "roads"
        };

        await sink.EnsureTargetAsync(target, CancellationToken.None);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        // Postgres truncates identifiers to NAMEDATALEN-1 (63 bytes by default), so we look up by
        // the truncated form rather than relying on the fixture-provided schema name verbatim.
        var truncatedSchema = _schema!.Length > 63 ? _schema![..63] : _schema!;
        await using var command = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = @schema AND table_name = @table ORDER BY ordinal_position",
            connection);
        command.Parameters.AddWithValue("@schema", truncatedSchema);
        command.Parameters.AddWithValue("@table", "roads_target");

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        columns.Should().BeEquivalentTo("source_feature_id", "properties", "geometry", "imported_at");
    }

    [Fact]
    public async Task WriteFeaturesAsync_InsertsRowsAndIsIdempotentOnReRun()
    {
        var sink = CreateSink();
        var target = new OgcApiFeaturesSinkTarget
        {
            Schema = _schema!,
            Table = "roads_target",
            CollectionId = "roads"
        };
        await sink.EnsureTargetAsync(target, CancellationToken.None);

        var batch = new List<OgcApiFeaturesSinkFeature>
        {
            new()
            {
                SourceFeatureId = "road.1",
                GeoJsonGeometry = "{\"type\":\"Point\",\"coordinates\":[-157.85,21.30]}",
                PropertiesJson = "{\"name\":\"King\"}"
            },
            new()
            {
                SourceFeatureId = "road.2",
                GeoJsonGeometry = "{\"type\":\"Point\",\"coordinates\":[-157.86,21.31]}",
                PropertiesJson = "{\"name\":\"Beretania\"}"
            }
        };

        var first = await sink.WriteFeaturesAsync(target, batch, CancellationToken.None);
        var firstCount = await CountRowsAsync(target);

        // Re-run with a mutated property to verify upsert semantics.
        var second = await sink.WriteFeaturesAsync(target, new List<OgcApiFeaturesSinkFeature>
        {
            batch[0] with { PropertiesJson = "{\"name\":\"King Updated\"}" },
            batch[1]
        }, CancellationToken.None);
        var secondCount = await CountRowsAsync(target);

        first.Should().Be(2);
        firstCount.Should().Be(2);
        second.Should().Be(2);
        secondCount.Should().Be(2);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT properties->>'name' FROM \"{_schema}\".\"roads_target\" WHERE source_feature_id = 'road.1'",
            connection);
        var name = (string?)await command.ExecuteScalarAsync();
        name.Should().Be("King Updated");
    }

    [Fact]
    public async Task WriteFeaturesAsync_StoresGeometryAsPostgisGeometry()
    {
        var sink = CreateSink();
        var target = new OgcApiFeaturesSinkTarget
        {
            Schema = _schema!,
            Table = "roads_geom",
            CollectionId = "roads"
        };
        await sink.EnsureTargetAsync(target, CancellationToken.None);

        await sink.WriteFeaturesAsync(target, new List<OgcApiFeaturesSinkFeature>
        {
            new()
            {
                SourceFeatureId = "road.geom",
                GeoJsonGeometry = "{\"type\":\"Point\",\"coordinates\":[-157.85,21.30]}",
                PropertiesJson = "{}"
            }
        }, CancellationToken.None);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT ST_SRID(geometry), ST_AsText(geometry) FROM \"{_schema}\".\"roads_geom\" WHERE source_feature_id = 'road.geom'",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt32(0).Should().Be(4326);
        reader.GetString(1).Should().Contain("POINT");
    }

    [Fact]
    public async Task WriteFeaturesAsync_AcceptsNullGeometry()
    {
        var sink = CreateSink();
        var target = new OgcApiFeaturesSinkTarget
        {
            Schema = _schema!,
            Table = "roads_null",
            CollectionId = "roads"
        };
        await sink.EnsureTargetAsync(target, CancellationToken.None);

        var written = await sink.WriteFeaturesAsync(target, new List<OgcApiFeaturesSinkFeature>
        {
            new()
            {
                SourceFeatureId = "missing.geom",
                GeoJsonGeometry = null,
                PropertiesJson = "{\"flag\":true}"
            }
        }, CancellationToken.None);

        written.Should().Be(1);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT geometry IS NULL FROM \"{_schema}\".\"roads_null\" WHERE source_feature_id = 'missing.geom'",
            connection);
        var isNull = (bool?)await command.ExecuteScalarAsync();
        isNull.Should().BeTrue();
    }

    [Fact]
    public async Task EnsureTargetAsync_RejectsIdentifiersWithUnsafeCharacters()
    {
        var sink = CreateSink();
        var target = new OgcApiFeaturesSinkTarget
        {
            Schema = _schema!,
            Table = "evil; DROP TABLE pg_class",
            CollectionId = "roads"
        };

        await FluentActions
            .Awaiting(() => sink.EnsureTargetAsync(target, CancellationToken.None))
            .Should()
            .ThrowAsync<ArgumentException>();
    }

    private PostgresOgcApiFeaturesCollectionSink CreateSink()
        => new(_fixture.DataSource, NullLogger<PostgresOgcApiFeaturesCollectionSink>.Instance);

    private async Task<int> CountRowsAsync(OgcApiFeaturesSinkTarget target)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM \"{target.Schema}\".\"{target.Table}\"",
            connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
